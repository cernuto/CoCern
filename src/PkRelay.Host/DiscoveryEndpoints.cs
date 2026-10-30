using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using PkRelay.Contracts;
using PkRelay.Core;

namespace PkRelay.Host;

/// <summary>Reads the <c>Relay:Discovery</c> settings. Anything other than a literal <c>true</c> leaves discovery off.</summary>
internal static class DiscoverySettings
{
    private const int CredentialLength = 64;

    public static bool Enabled(IConfiguration configuration) => bool.TryParse(configuration["Relay:Discovery:Enabled"], out var enabled) && enabled;

    /// <summary>The repositories every machine principal may use: the configured list, or <c>pkrelay</c> only when the setting is absent.</summary>
    public static string[] Repositories(IConfiguration configuration)
    {
        var section = configuration.GetSection("Relay:Discovery:Repositories");
        return section.Exists() ? (section.Get<string[]>() ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray() : ["pkrelay"];
    }

    /// <summary>True for the shape the hub issues (32 random bytes, hex). Anything else cannot be a machine credential, so it never reaches the discovery grain.</summary>
    public static bool IsCredential(string token) => token.Length == CredentialLength && token.All(Uri.IsHexDigit);
}

/// <summary>Remembers each machine's last announced routes so a change can be told from a repeat. In memory only: after a restart the first change simply sends one extra hint.</summary>
internal sealed class MachineRouteHints
{
    private const string NoRoutes = "[]";
    private readonly Dictionary<string, string> last = new(StringComparer.Ordinal);

    /// <summary>Records the routes and reports whether they differ from the machine's previous announcement (empty when none).</summary>
    public bool Changed(string machineId, string routesJson)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(routesJson)));
        var empty = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NoRoutes)));
        lock (last)
        {
            var changed = last.GetValueOrDefault(machineId, empty) != fingerprint;
            last[machineId] = fingerprint;
            return changed;
        }
    }
}

/// <summary>
/// Limits failed machine-credential validations to <see cref="FailuresPerMinute"/> per remote address (the enroll limiter's partitioning). Over the limit, a credential-shaped token
/// fails authentication without reaching the discovery grain; configured clients and credentials already cached as valid are unaffected.
/// </summary>
internal sealed class MachineCredentialLimiter : IDisposable
{
    public const int FailuresPerMinute = 20;
    private readonly PartitionedRateLimiter<HttpContext> limiter = PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(DiscoveryEndpoints.Address(context), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = FailuresPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
    }));

    /// <summary>Whether this address may still have a credential checked by the grain; consumes nothing.</summary>
    public bool MayValidate(HttpContext context) { using var lease = limiter.AttemptAcquire(context, 0); return lease.IsAcquired; }

    /// <summary>Counts one failed validation against the address.</summary>
    public void Failed(HttpContext context) { using var lease = limiter.AttemptAcquire(context, 1); }

    public void Dispose() => limiter.Dispose();
}

/// <summary>Anonymous enrollment and machine announcements. Neither endpoint exists unless <c>Relay:Discovery:Enabled</c> is true; neither logs request or response bodies.</summary>
internal static partial class DiscoveryEndpoints
{
    public const string EnrollPolicy = "discovery-enroll";
    private const int EnrollPermitsPerMinute = 10;
    private const int MaxNameLength = 128;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex MachineIdPattern();

    public static void AddDiscovery(this IServiceCollection services)
    {
        services.AddSingleton<MachineRouteHints>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, token) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsJsonAsync(new { ok = false, code = RelayErrorCodes.RateLimited }, token);
            };
            options.AddPolicy(EnrollPolicy, context => RateLimitPartition.GetFixedWindowLimiter(Address(context), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = EnrollPermitsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
            }));
        });
    }

    public static void MapDiscoveryEndpoints(this WebApplication app)
    {
        if (!DiscoverySettings.Enabled(app.Configuration)) return;
        app.MapPost("/api/discovery/enroll", EnrollAsync).AllowAnonymous().RequireRateLimiting(EnrollPolicy);
        app.MapPost("/api/discovery/announce", AnnounceAsync).RequireAuthorization();
    }

    /// <summary>One bucket per IPv4 address or IPv6 /64, so a host cannot dodge the limit by rotating addresses inside its own prefix.</summary>
    internal static string Address(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is not { } address) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? Convert.ToHexString(address.GetAddressBytes().AsSpan(0, 8)) : address.ToString();
    }

    private static IResult Failure(int status, string code) => Results.Json(ToolResults.Failure(code), statusCode: status);

    private static async Task<IResult> EnrollAsync(HttpContext context, IMachineDirectory machines, RelayClient[] clients, IConfiguration configuration)
    {
        // The reply carries a bearer secret, so it never travels in clear text off the machine.
        if (!context.Request.IsHttps && !(context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote))) return Failure(StatusCodes.Status403Forbidden, RelayErrorCodes.HttpsRequired);
        string? machineId, machineName;
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, new JsonDocumentOptions { MaxDepth = 8 }, context.RequestAborted);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Failure(StatusCodes.Status400BadRequest, RelayErrorCodes.InvalidArguments);
            machineId = Text(document.RootElement, "machineId");
            machineName = Text(document.RootElement, "machineName");
        }
        catch (JsonException) { return Failure(StatusCodes.Status400BadRequest, RelayErrorCodes.InvalidArguments); }
        if (machineId is null || !MachineIdPattern().IsMatch(machineId) || !ValidMachineName(machineName))
            return Failure(StatusCodes.Status400BadRequest, RelayErrorCodes.InvalidArguments);
        // An enrolled machine must never share a principal with a configured client or OAuth subject.
        var principalId = "machine:" + machineId;
        if (ConfiguredPrincipals(clients, configuration).Contains(principalId)) return Failure(StatusCodes.Status409Conflict, RelayErrorCodes.AliasConflict);

        var credential = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var result = await machines.TryEnrollAsync(machineId, machineName, credential, context.RequestAborted);
        if (result.Machine is null) return Failure(EnrollStatus(result.Code), result.Code ?? RelayErrorCodes.InternalError);
        return Results.Json(new { machineId = result.Machine.MachineId, credential });
    }

    /// <summary>The enroll rules for a display name: 1 to 128 characters, not blank, no control characters. Announce applies the same rules.</summary>
    private static bool ValidMachineName(string? name) => name is { Length: > 0 and <= MaxNameLength } && !name.Any(char.IsControl) && !string.IsNullOrWhiteSpace(name);

    private static async Task<IResult> AnnounceAsync(HttpContext context, IMachineDirectory machines, IHubContext<CoordinationHub> hub, MachineRouteHints hints)
    {
        if (RelayAuthentication.Machine(context.User) is not { } machine) return Failure(StatusCodes.Status403Forbidden, RelayErrorCodes.MachineRequired);
        var allowed = context.User.FindAll("relay_repository").Select(x => x.Value).ToHashSet(StringComparer.Ordinal);
        JsonObject? body;
        try { body = (await JsonNode.ParseAsync(context.Request.Body, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }, cancellationToken: context.RequestAborted)) as JsonObject; }
        catch (JsonException) { body = null; }
        if (body is null || !(body["machineName"] is JsonValue name && name.TryGetValue<string>(out var machineName) && ValidMachineName(machineName))) return Failure(StatusCodes.Status400BadRequest, RelayErrorCodes.InvalidArguments);
        var refused = new JsonArray();
        if (body["chats"] is { } chats)
        {
            if (chats is not JsonArray list) return Failure(StatusCodes.Status400BadRequest, RelayErrorCodes.InvalidArguments);
            var bySession = new List<(JsonObject Chat, string Repository, string Session)>();
            foreach (var node in list)
            {
                if (node is not JsonObject chat || chat["repositoryId"] is not JsonValue value || !value.TryGetValue<string>(out var repository)) return Failure(StatusCodes.Status400BadRequest, RelayErrorCodes.InvalidArguments);
                if (!allowed.Contains(repository)) return Failure(StatusCodes.Status403Forbidden, RelayErrorCodes.RepositoryDenied);
                bySession.Add((chat, repository, chat["relaySessionId"] is JsonValue session && session.TryGetValue<string>(out var id) ? id : ""));
            }
            // A chat is announced only when the announcing machine opened its relay session, so one device cannot publish chats that impersonate another's.
            // Only the offending chats are refused; each is named in the reply with the denial code, and the rest are announced.
            var owned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in bySession.Where(x => x.Session.Length > 0).GroupBy(x => x.Repository, StringComparer.Ordinal))
            {
                if (await machines.OwnedSessionsAsync(machine, group.Key, group.Select(x => x.Session).ToArray(), context.RequestAborted) is not { } mine)
                    return Failure(StatusCodes.Status503ServiceUnavailable, RelayErrorCodes.Unavailable);
                owned.UnionWith(mine);
            }
            foreach (var (chat, _, session) in bySession.Where(x => !owned.Contains(x.Session)))
            {
                list.Remove(chat);
                refused.Add(new JsonObject { ["relaySessionId"] = session, ["code"] = RelayErrorCodes.SessionDenied });
            }
        }
        var reply = await machines.AnnounceAsync(machine, body, context.RequestAborted);
        if (!reply.Ok) return Failure(ToolResults.Status(reply), reply.Code);
        var routes = JsonNode.Parse(reply.DataJson)?["routes"]?.ToJsonString() ?? "[]";
        if (hints.Changed(machine.MachineId, routes))
        {
            // A lost hint costs one announce interval, never correctness, so a delivery failure does not fail the announcement.
            try { await hub.Clients.Group(CoordinationHub.MachineGroup(machine.MachineId)).SendAsync("discovery.changed", new { }, context.RequestAborted); }
            catch (Exception exception) when (exception is not OperationCanceledException) { }
        }
        if (refused.Count == 0) return Results.Content(reply.DataJson, "application/json");
        var data = JsonNode.Parse(reply.DataJson)!.AsObject();
        data["refused"] = refused;
        return Results.Content(data.ToJsonString(), "application/json");
    }

    private static int EnrollStatus(string? code) => code switch
    {
        RelayErrorCodes.MachineRevoked => StatusCodes.Status403Forbidden,
        RelayErrorCodes.AliasConflict => StatusCodes.Status409Conflict,
        RelayErrorCodes.CapacityExceeded or RelayErrorCodes.StorageFailure => StatusCodes.Status503ServiceUnavailable,
        RelayErrorCodes.PayloadTooLarge => StatusCodes.Status413PayloadTooLarge,
        RelayErrorCodes.InvalidArguments => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError
    };

    private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static HashSet<string> ConfiguredPrincipals(RelayClient[] clients, IConfiguration configuration)
    {
        var set = new HashSet<string>(clients.Select(x => x.PrincipalId), StringComparer.OrdinalIgnoreCase);
        foreach (var principal in configuration.GetSection("Relay:OAuth:Principals").Get<OAuthPrincipal[]>() ?? []) set.Add(principal.PrincipalId);
        return set;
    }
}
