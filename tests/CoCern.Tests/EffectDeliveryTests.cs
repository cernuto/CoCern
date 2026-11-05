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
