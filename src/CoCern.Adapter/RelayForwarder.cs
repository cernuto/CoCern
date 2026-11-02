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

public sealed class RelayForwarder(HttpClient client, AdapterOptions options)
{
    public const int MaxRequestBytes = RelayLimits.MaxPayloadBytes; // the hub refuses anything larger
    public const int MaxResponseBytes = 128 * 1024;
    private const string ResponseTooLarge = "response_too_large";

    public static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false };
    /// <summary>The pinned handler when the options carry a certificate fingerprint, otherwise the plain one.</summary>
    public static HttpClientHandler CreateHandler(AdapterOptions options) => options.PinnedFingerprint is { } fingerprint ? PinnedCertificate.CreateHandler(fingerprint) : CreateHandler();

    public async Task<AdapterReply> ForwardAsync(string tool, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CoordinationTools.IsAllowed(tool, options.Admin, options.ChatOnly)) return new(false, "unknown_tool");
        if (arguments.ValueKind != JsonValueKind.Object) return new(false, "invalid_arguments");
        var json = arguments.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > MaxRequestBytes) return new(false, "request_too_large");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUri, $"api/tools/{tool}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400) return new(false, "redirect_rejected");
            var failureKind = DomainFailureKind(response.StatusCode);
            if (!response.IsSuccessStatusCode && failureKind is null) return new(false, TransportCode(response.StatusCode));
            if (failureKind is { } kind)
            {
                // A domain failure keeps its code, data and version only when the body is the hub's own envelope for
                // a known failure code of exactly this status; the adapter's own size bound stays explicit, and
                // anything else stays a generic transport code.
                try
                {
                    var failure = await ReadEnvelopeAsync(response, cancellationToken);
                    // Only the reader's own size error passes; a parsed hub envelope always carries a version.
                    if (failure is { Code: ResponseTooLarge, Version: null }) return failure;
                    return !failure.Ok && KnownCodes.Contains(failure.Code) && RelayErrorCodes.Classify(failure.Code) == kind ? failure : new(false, TransportCode(response.StatusCode));
                }
                catch (JsonException) { return new(false, TransportCode(response.StatusCode)); }
                catch (InvalidOperationException) { return new(false, TransportCode(response.StatusCode)); }
            }
            return await ReadEnvelopeAsync(response, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false, "hub_timeout"); }
        catch (HttpRequestException) { return new(false, "hub_unavailable"); }
        catch (IOException) { return new(false, "hub_unavailable"); }
        catch (JsonException) { return new(false, "invalid_hub_reply"); }
        catch (InvalidOperationException) { return new(false, "invalid_hub_reply"); }
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
