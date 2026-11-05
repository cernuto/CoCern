using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CoCern.Adapter.Node;
using CoCern.Contracts;

namespace CoCern.Adapter;

public sealed record AdapterReply(bool Ok, string Code, JsonElement? Data = null, long? Version = null)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

/// <param name="callOrigin">Who this forwarder's calls report as their origin (<see cref="CallOrigins"/>). Telemetry only; null reports none.</param>
public sealed class RelayForwarder(HttpClient client, AdapterOptions options, string? callOrigin = null)
{
    public const int MaxRequestBytes = RelayLimits.MaxPayloadBytes; // the hub refuses anything larger
    public const int MaxResponseBytes = 128 * 1024;
    private const string ResponseTooLarge = "response_too_large";

    /// <summary>The same transport, reporting another origin.</summary>
    internal RelayForwarder Tagged(string origin) => new(client, options, origin);

    public static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false };
    /// <summary>The pinned handler when the options carry a certificate fingerprint, otherwise the plain one.</summary>
    public static HttpClientHandler CreateHandler(AdapterOptions options) => options.PinnedFingerprint is { } fingerprint ? PinnedCertificate.CreateHandler(fingerprint) : CreateHandler();

    // Uses this transport's closed connection options; callers cannot substitute an unrelated authentication claim.
    internal bool MatchesNativeBinding(NativeJoinBinding binding) => binding.MatchesConnection(options);

    public Task<AdapterReply> ForwardAsync(string tool, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<AdapterReply>(cancellationToken);
        return CoordinationTools.IsAllowed(tool, options.Admin, options.ChatOnly, options.Diagnostics, options.EventWaits)
            ? SendAsync(tool, arguments, cancellationToken)
            : Task.FromResult(new AdapterReply(false, "unknown_tool", ChatGuidance.ToolOff(tool) is { } hint ? JsonSerializer.SerializeToElement(new Dictionary<string, string> { [ChatGuidance.HintField] = hint }) : null));
    }

    /// <summary>A <c>session_open</c> reply with its startup line narrowed to the tools this adapter lists (<see cref="ChatGuidance.NarrowStartHint"/>). A reply without one stays as it is.</summary>
    internal AdapterReply WithOwnStartHint(AdapterReply reply)
    {
        if (reply.Data is not { ValueKind: JsonValueKind.Object } data || !data.TryGetProperty(ChatGuidance.StartHintField, out var line) || line.ValueKind != JsonValueKind.String) return reply;
        var narrowed = ChatGuidance.NarrowStartHint(line.GetString()!, options.Admin, options.ChatOnly);
        if (narrowed == line.GetString()) return reply;
        var node = System.Text.Json.Nodes.JsonNode.Parse(data.GetRawText())!.AsObject();
        node[ChatGuidance.StartHintField] = narrowed;
        return reply with { Data = JsonSerializer.SerializeToElement(node) };
    }

    // A fixed, internal status read for the exact connection's current private handle, including in the compact profile.
    internal Task<AdapterReply> ProbeNativeAsync(NativeJoinBinding binding, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!MatchesNativeBinding(binding) || binding.CurrentHandle is not { } own) return Task.FromResult(new AdapterReply(false, "native_handle_unverified"));
        return SendAsync(RelayTools.GetStatus, JsonSerializer.SerializeToElement(new
        { repositoryId = own.RepositoryId, sessionId = own.SessionId, sessionToken = own.SessionToken, backgroundCall = true }), ct);
    }

    // The same fixed read for one local handle a restarted adapter would watch again, including in the compact profile.
    internal Task<AdapterReply> ProbeHandleAsync(Node.SessionHandle handle, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return SendAsync(RelayTools.GetStatus, JsonSerializer.SerializeToElement(new
        { repositoryId = handle.RepositoryId, sessionId = handle.SessionId, sessionToken = handle.SessionToken, backgroundCall = true }), ct);
    }

    private async Task<AdapterReply> SendAsync(string tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return new(false, "invalid_arguments");
        var json = arguments.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > MaxRequestBytes) return new(false, "request_too_large");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUri, $"api/tools/{tool}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        request.Content = new StringContent(WithCallOrigin(arguments, json), Encoding.UTF8, "application/json");
        // HttpClient.Timeout stops at the headers under ResponseHeadersRead; this bounds the body read too.
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(client.Timeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token);
            if ((int)response.StatusCode is >= 300 and < 400) return new(false, "redirect_rejected");
            var failureKind = DomainFailureKind(response.StatusCode);
            if (!response.IsSuccessStatusCode && failureKind is null)
                return response.StatusCode == HttpStatusCode.Unauthorized ? ConnectionFailure("unauthorized") : new(false, TransportCode(response.StatusCode));
            if (failureKind is { } kind)
            {
                // A domain failure keeps its code, data and version only when the body is the hub's own envelope for
                // a known failure code of exactly this status; the adapter's own size bound stays explicit, and
                // anything else stays a generic transport code.
                try
                {
                    var failure = await ReadEnvelopeAsync(response, bounded.Token);
                    // Only the reader's own size error passes; a parsed hub envelope always carries a version.
                    if (failure is { Code: ResponseTooLarge, Version: null }) return failure;
                    return !failure.Ok && KnownCodes.Contains(failure.Code) && RelayErrorCodes.Classify(failure.Code) == kind ? failure : new(false, TransportCode(response.StatusCode));
                }
                catch (JsonException) { return new(false, TransportCode(response.StatusCode)); }
                catch (InvalidOperationException) { return new(false, TransportCode(response.StatusCode)); }
            }
            return await ReadEnvelopeAsync(response, bounded.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false, "hub_timeout"); }
        catch (HttpRequestException) { return ConnectionFailure(request.Options.TryGetValue(PinnedCertificate.IdentityMismatch, out var mismatch) && mismatch ? "hub_identity_changed" : "hub_unavailable"); }
        catch (IOException) { return ConnectionFailure(request.Options.TryGetValue(PinnedCertificate.IdentityMismatch, out var mismatch) && mismatch ? "hub_identity_changed" : "hub_unavailable"); }
        catch (JsonException) { return new(false, "invalid_hub_reply"); }
        catch (InvalidOperationException) { return new(false, "invalid_hub_reply"); }
    }

    private static AdapterReply ConnectionFailure(string code)
    {
        var message = code switch
        {
            "hub_identity_changed" => "The hub's identity changed. Check with the person who runs the hub before reconnecting this PC.",
            "unauthorized" => "The hub didn't accept this PC's sign-in. Reconnect this PC to the hub and try again.",
            _ => "Can't reach the hub. Check that it is running and try again."
        };
        return new(false, code, JsonSerializer.SerializeToElement(new { message }));
    }

    /// <summary>
    /// The body to send: <paramref name="json"/> with this forwarder's origin added and every other byte as it was. The tag never costs a call: a native
    /// envelope is an exact shape that a hub older than the origin strip refuses with any extra field, and a body the tag would push past the size limit goes without it.
    /// </summary>
    private string WithCallOrigin(JsonElement arguments, string json)
    {
        if (callOrigin is null || arguments.TryGetProperty("nativeJoin", out _) || arguments.TryGetProperty("nativePreflight", out _)) return json;
        string tagged;
        if (arguments.TryGetProperty(CallOrigins.Field, out _))
        {
            // The adapter knows who is calling, so a value in the arguments gives way; a second copy of the key would get the call refused.
            try
            {
                var values = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
                values[CallOrigins.Field] = callOrigin;
                tagged = values.ToJsonString();
            }
            catch (ArgumentException) { return json; } // A repeated key: the hub refuses it as it always has.
        }
        else tagged = json[..^1] + (arguments.EnumerateObject().Any() ? "," : "") + JsonSerializer.Serialize(CallOrigins.Field) + ":" + JsonSerializer.Serialize(callOrigin) + "}";
        return Encoding.UTF8.GetByteCount(tagged) > MaxRequestBytes ? json : tagged;
    }

    /// <summary>Stamps reported origin at the adapter boundary; this field grants no permission and changes no call behavior.</summary>
    internal static JsonElement WithOrigin(JsonElement arguments, bool background)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return arguments;
        var values = System.Text.Json.Nodes.JsonNode.Parse(arguments.GetRawText())!.AsObject();
        values["backgroundCall"] = background;
        return JsonSerializer.SerializeToElement(values);
    }

    /// <summary>Every published domain reply code; a failure body naming anything else is never passed through.</summary>
    private static readonly FrozenSet<string> KnownCodes = typeof(RelayErrorCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The failure kind the hub answers with this status (the inverse of its REST status mapping), or null for a transport failure.</summary>
    private static RelayErrorKind? DomainFailureKind(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest => RelayErrorKind.Invalid,
        HttpStatusCode.Forbidden => RelayErrorKind.Denied,
        HttpStatusCode.NotFound => RelayErrorKind.Missing,
        HttpStatusCode.Conflict => RelayErrorKind.Conflict,
        HttpStatusCode.RequestEntityTooLarge => RelayErrorKind.TooLarge,
        _ => null
    };

    private static string TransportCode(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "unauthorized",
        HttpStatusCode.Forbidden => "forbidden",
        HttpStatusCode.RequestEntityTooLarge => "request_too_large",
        HttpStatusCode.TooManyRequests => "rate_limited",
        _ => "hub_error"
    };

    /// <summary>Reads the bounded hub envelope; a body that is not one becomes a generic adapter failure.</summary>
    private static async Task<AdapterReply> ReadEnvelopeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes) return new(false, ResponseTooLarge);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxResponseBytes + 1 - (int)bytes.Length)), cancellationToken);
            if (count == 0) break;
            bytes.Write(buffer, 0, count);
            if (bytes.Length > MaxResponseBytes) return new(false, ResponseTooLarge);
        }

        using var document = JsonDocument.Parse(bytes.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("dataJson", out var dataJson) || dataJson.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("version", out var version) || !version.TryGetInt64(out var number))
            return new(false, "invalid_hub_reply");
        var codeText = code.GetString()!;
        if (codeText.Length is < 1 or > 100 || codeText.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.'))
            return new(false, "invalid_hub_reply");
        using var data = JsonDocument.Parse(dataJson.GetString()!);
        return new(ok.GetBoolean(), codeText, data.RootElement.Clone(), number);
    }
}
