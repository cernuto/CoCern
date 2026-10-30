using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PkRelay.Adapter;

public sealed record AdapterReply(bool Ok, string Code, JsonElement? Data = null, long? Version = null)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed class RelayForwarder(HttpClient client, AdapterOptions options)
{
    private static readonly HashSet<string> RecognizedConflicts = ["VersionConflict", "RequestConflict", "ClaimBusy", "StaleClaim", "Paused", "Busy", "ActionConflict", "UnresolvedAction", "DeltaRestartRequired", "WorkflowRecheckRequired", "WorkflowIncomplete", "WorkflowNotReady", "version_conflict"];
    public const int MaxRequestBytes = 64 * 1024;
    public const int MaxResponseBytes = 128 * 1024;

    public static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false };

    public async Task<AdapterReply> ForwardAsync(string tool, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CoordinationTools.IsAllowed(tool, options.Admin)) return new(false, "unknown_tool");
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
            var domainConflict = response.StatusCode == HttpStatusCode.Conflict;
            if (!response.IsSuccessStatusCode && !domainConflict)
                return new(false, response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "unauthorized",
                    HttpStatusCode.Forbidden => "forbidden",
                    HttpStatusCode.RequestEntityTooLarge => "request_too_large",
                    HttpStatusCode.TooManyRequests => "rate_limited",
                    _ => "hub_error"
                });

            if (response.Content.Headers.ContentLength > MaxResponseBytes) return new(false, "response_too_large");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxResponseBytes + 1 - (int)bytes.Length)), cancellationToken);
                if (count == 0) break;
                bytes.Write(buffer, 0, count);
                if (bytes.Length > MaxResponseBytes) return new(false, "response_too_large");
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
            if (domainConflict && (ok.GetBoolean() || !RecognizedConflicts.Contains(codeText))) return new(false, "hub_error");
            using var data = JsonDocument.Parse(dataJson.GetString()!);
            return new(ok.GetBoolean(), codeText, data.RootElement.Clone(), number);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false, "hub_timeout"); }
        catch (HttpRequestException) { return new(false, "hub_unavailable"); }
        catch (IOException) { return new(false, "hub_unavailable"); }
        catch (JsonException) { return new(false, "invalid_hub_reply"); }
        catch (InvalidOperationException) { return new(false, "invalid_hub_reply"); }
    }
}
