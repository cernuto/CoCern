using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Domain.Messages;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// What the outbox does with a typed message its target refuses: a state transfer is retried, a report its sender sends again is dropped
/// and counted, and a malformed one is logged once, by its field names and never its values.
/// </summary>
public sealed class EffectDeliveryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T09:00:00Z");
    private const string Token = "5EC2E7A11CE0000000000000000000000000000000000000000000000000BEEF";

    [Theory]
    [InlineData(Operations.Open, RelayErrorCodes.SessionDenied, "Retry")]
    [InlineData(Operations.SessionExpire, RelayErrorCodes.MalformedEffect, "Retry")]
    [InlineData(Operations.SessionRestore, RelayErrorCodes.MalformedEffect, "Retry")]
    [InlineData(Operations.SessionExpired, RelayErrorCodes.MalformedEffect, "Retry")]
    [InlineData(Operations.SessionClosed, RelayErrorCodes.RepositoryDenied, "Retry")]
    [InlineData(Operations.SessionWork, RelayErrorCodes.MalformedEffect, "Retry")]
    [InlineData(Operations.Register, RelayErrorCodes.SessionDenied, "Retry")]
    [InlineData(Operations.SessionOwns, RelayErrorCodes.MalformedEffect, "DropAndCount")]
    [InlineData(Operations.SessionActive, RelayErrorCodes.SessionDenied, "DropAndCount")]
    [InlineData(Operations.SessionActive, RelayErrorCodes.StorageFailure, "Retry")]
    [InlineData(Operations.Block, RelayErrorCodes.Busy, "Drop")]
    [InlineData(Operations.Block, RelayErrorCodes.ClaimRequired, "Drop")]
    [InlineData(Operations.Block, RelayErrorCodes.MalformedEffect, "Retry")]
    [InlineData(Operations.Unblock, RelayErrorCodes.ActionDenied, "Retry")]
    [InlineData(Operations.AttentionRaise, RelayErrorCodes.SessionUnknown, "Drop")]
    [InlineData(Operations.Deliver, RelayErrorCodes.InvalidArguments, "Retry")]
    public void A_typed_message_is_retried_or_dropped_by_its_delivery_class(string operation, string code, string expected)
    {
        var effect = new DurableEffect("e", AggregateKinds.Control, AggregateKinds.ControlKey, operation, "{}", Now);
        Assert.Equal(expected, EffectDelivery.OnFailure(effect, new JsonObject(), code, AggregateKinds.Session, ordersOn: true).ToString());
    }

    [Fact]
    public void The_malformed_effect_log_names_fields_and_never_carries_a_secret()
    {
        // An open that lost its principal still carries the session's secret.
        var open = new SessionOpen(SessionRecord.Opened("s1", Token, "alice", "repo", new JsonObject { ["label"] = "chat" })).ToJson();
        open.Remove("principalId");
        var refused = CoordinationStateMachine.Apply(new StateEnvelope<CoordinationDocument>(), AggregateKinds.Session, "s1", new RelayCaller("alice", false, ["repo"], true), Operations.Open, open, Now);
        Assert.Equal(RelayErrorCodes.MalformedEffect, refused.Code);
        Assert.DoesNotContain(Token, refused.DataJson);
        var effect = new DurableEffect("effect-1", AggregateKinds.Session, "s1", Operations.Open, open.ToJsonString(), Now);
        var logger = new CapturingLogger();
        EffectDelivery.LogMalformed(logger, AggregateKinds.Control, AggregateKinds.ControlKey, effect, refused, EffectDelivery.Failure.Retry);
        var line = Assert.Single(logger.Lines);
        Assert.Equal(LogLevel.Error, line.Level);
        foreach (var expected in new[] { Operations.Open, "effect-1", AggregateKinds.Control, AggregateKinds.ControlKey, AggregateKinds.Session, "s1", "principalId" }) Assert.Contains(expected, line.Message);
        Assert.DoesNotContain(Token, line.Message);
        Assert.DoesNotContain(Token, string.Join("|", line.State.Select(entry => entry.Value)));

        // Even a handler that put a value where a field name belongs cannot get it into the log.
        var careless = Replies.Malformed(["sessionToken", Token, "2026-11-03T09:00:00Z"]);
        EffectDelivery.LogMalformed(logger, AggregateKinds.Control, AggregateKinds.ControlKey, effect, careless, EffectDelivery.Failure.Retry);
        Assert.Contains("sessionToken, ?, ?", logger.Lines[1].Message);
        Assert.DoesNotContain(Token, logger.Lines[1].Message);
    }

    [Fact]
    public async Task A_malformed_report_is_dropped_and_counted_and_a_malformed_state_transfer_retries_and_is_logged_once()
    {
        var logs = new CapturingProvider();
        var activity = new RecordingSink();
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders().AddProvider(logs)).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(), serviceId: "effect-delivery-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
            .ConfigureServices(services => services.AddCoCernCore().AddSingleton<IActivitySink>(activity)
                // The test seeds outboxes straight into memory storage, which then reads them back through its JSON type allow-list.
                .Configure<Orleans.Serialization.Configuration.TypeManifestOptions>(options => options.AllowAllTypes = true)).Build();
        await host.StartAsync();
        try
        {
            var factory = host.Services.GetRequiredService<IGrainFactory>();
            var storage = host.Services.GetRequiredKeyedService<Orleans.Storage.IGrainStorage>("relay");
            var dispatcher = new RelayCaller("relay-dispatcher", false, [], true);
            DurableEffect Effect(string kind, string key, string operation, JsonObject payload)
            {
                payload["__principalId"] = "alice"; payload["__administrator"] = false; payload["__repositories"] = new JsonArray("repo");
                return new(Guid.NewGuid().ToString("N"), kind, key, operation, payload.ToJsonString(), DateTimeOffset.UtcNow.AddSeconds(-1));
            }

            // A session's activity report without its time: a report, so it is dropped and counted, and logged once.
            var session = new StateEnvelope<CoordinationDocument> { Value = new() { Json = """{"repositoryId":"repo"}""" } };
            session.Effects.Add(Effect(AggregateKinds.Control, AggregateKinds.ControlKey, Operations.SessionActive, new() { ["repositoryId"] = "repo", ["sessionId"] = "seeded" }));
            await storage.WriteStateAsync("session", GrainId.Create("cocern.session", "seeded"), new GrainState<StateEnvelope<CoordinationDocument>>(session));
            Assert.True((await factory.GetGrain<ISessionGrain>("seeded").ExecuteAsync(dispatcher, Operations.Dispatch, "{}")).Ok);
            var dropped = Assert.Single(activity.Events);
            Assert.Equal((ActivityEvent.EffectRefused, Operations.SessionActive, RelayErrorCodes.MalformedEffect, "seeded"), (dropped.Kind, dropped.Tool, dropped.Code, dropped.SessionId));
            var line = Assert.Single(logs.Lines);
            Assert.Contains("activeAt", line.Message);
            Assert.Contains("dropped", line.Message);

            // An open that lost its principal: a state transfer, so it stays and is retried, logged and counted once, its secret never shown.
            var open = new SessionOpen(SessionRecord.Opened("lost", Token, "alice", "repo", new JsonObject())).ToJson();
            open.Remove("principalId");
            var board = new StateEnvelope<CoordinationDocument> { Value = new() { Json = """{"repositoryId":"repo"}""" } };
            board.Effects.Add(Effect(AggregateKinds.Session, "lost", Operations.Open, open));
            await storage.WriteStateAsync("board", GrainId.Create("cocern.board", "repo/seeded"), new GrainState<StateEnvelope<CoordinationDocument>>(board));
            var grain = factory.GetGrain<ITaskBoardGrain>("repo/seeded");
            Assert.Equal(RelayErrorCodes.DeliveryPending, (await grain.ExecuteAsync(dispatcher, Operations.Dispatch, "{}")).Code);
            // The board keeps that it reported the effect, so a reloaded grain does not log or count it again.
            var stored = new GrainState<StateEnvelope<CoordinationDocument>>();
            await storage.ReadStateAsync("board", GrainId.Create("cocern.board", "repo/seeded"), stored);
            Assert.True(Assert.Single(stored.State!.Effects).MalformedReported);
            await Task.Delay(TimeSpan.FromSeconds(5.5));
            Assert.Equal(RelayErrorCodes.DeliveryPending, (await grain.ExecuteAsync(dispatcher, Operations.Dispatch, "{}")).Code);
            Assert.Equal(2, logs.Lines.Count);
            Assert.Contains("principalId", logs.Lines[1].Message);
            Assert.Contains("retried", logs.Lines[1].Message);
            Assert.All(logs.Lines, logged => Assert.DoesNotContain(Token, logged.Message));
            Assert.Equal(2, activity.Events.Count);
            Assert.Equal(Operations.Open, activity.Events[1].Tool);
        }
        finally { await host.StopAsync(); }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed record Line(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State);

    private sealed class CapturingLogger : ILogger
    {
        public List<Line> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(new(logLevel, formatter(state, exception), state as IReadOnlyList<KeyValuePair<string, object?>> ?? []));
    }

    /// <summary>Keeps the Error lines the coordination grains write; everything else (Orleans' own logging) is ignored.</summary>
    private sealed class CapturingProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<Line> lines = new();
        public List<Line> Lines => [.. lines];
        public ILogger CreateLogger(string categoryName) => categoryName == typeof(CoordinationGrain).FullName ? new Forwarding(lines) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public void Dispose() { }

        private sealed class Forwarding(ConcurrentQueue<Line> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) lines.Enqueue(new(logLevel, formatter(state, exception), state as IReadOnlyList<KeyValuePair<string, object?>> ?? []));
            }
        }
    }

    private sealed class RecordingSink : IActivitySink
    {
        private readonly ConcurrentQueue<ActivityEvent> events = new();
        public List<ActivityEvent> Events => [.. events.Where(entry => entry.Kind == ActivityEvent.EffectRefused)];
        public void Record(ActivityEvent activity) => events.Enqueue(activity);
    }
}
