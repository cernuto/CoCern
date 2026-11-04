using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Diagnostics;
using CoCern.Adapter.Development;
using CoCern.Contracts;

namespace CoCern.Adapter.Node;

internal static class NativeJoinHook
{
    internal const int MaximumInputBytes = 1024 * 1024;

    internal static AdapterOptions? ReadConnection(string folder)
    {
        try
        {
            if (!Path.IsPathFullyQualified(folder)) return null;
            folder = Path.GetFullPath(folder);
            for (DirectoryInfo? directory = new(folder); directory is not null; directory = directory.Parent)
                if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            if (NodeState.HasIdentityFiles(folder))
            {
                if (NodeState.NativeCredentialIdentity(folder) is null || NodeState.ReadEnrollment(folder) is not { } node) return null;
                return new(node.Hub.AbsoluteUri, node.Credential, signalR: true) { PinnedFingerprint = node.Fingerprint, NodeFolder = folder };
            }
            return LocalWakeConnection.NativeCredentialIdentity(folder) is not null ? LocalWakeConnection.Read(folder)?.Options : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or AdapterConfigurationException) { return null; }
    }

    // The setup-owned launcher identity stays stable while its helper follows the current deployment.
    internal static async Task<int> RunDevelopmentAsync(string root, string dotnet, string provider, string folder, TextReader input, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var text = await ReadInputAsync(input, ct);
        if (text is null) return 0;
        var adapter = AdapterDeployment.AdapterPath(root, AdapterDeployment.Read(root));
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { adapter, "--native-hook", provider, "--connection", folder }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("The current native helper could not start.");
        var standard = CopyAsync(process.StandardOutput, output, ct);
        var diagnostic = CopyAsync(process.StandardError, error, ct);
        try
        {
            await process.StandardInput.WriteAsync(text.AsMemory(), ct);
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(standard, diagnostic);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }

        static async Task CopyAsync(TextReader from, TextWriter to, CancellationToken cancel)
        {
            var buffer = new char[4096];
            while (await from.ReadAsync(buffer.AsMemory(), cancel) is var read && read != 0)
            { await to.WriteAsync(buffer.AsMemory(0, read), cancel); await to.FlushAsync(cancel); }
        }
    }

    internal static async Task<int> RunAsync(string provider, TextReader input, TextWriter output, TextWriter error, NativeJoin join, CancellationToken ct)
    {
        var recognized = false;
        try
        {
            var text = await ReadInputAsync(input, ct);
            if (text is null) return 0;
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            var capture = ParseContext(provider, root);
            if (capture is null) return 0;
            switch (ChatLocators.Text(root, "hook_event_name"))
            {
                case "SessionStart": await join.EnsureAsync(capture, ct); return 0;
                case "Stop" when provider == "claude":
                    return await StopHook.RunNativeAsync(new StringReader(text), error, join, ct, ownerGone: OwningProcess.WaitUntilGoneAsync);
                case "PreToolUse":
                    var name = ChatLocators.Text(root, "tool_name");
                    const string prefix = "mcp__cocern__";
                    if (name is null || !name.StartsWith(prefix, StringComparison.Ordinal)) return 0;
                    recognized = true;
                    var tool = name[prefix.Length..];
                    if (ChatLocators.Text(root, "tool_use_id") is not { } invocation || !ValidInvocation(tool, invocation) ||
                        !root.TryGetProperty("tool_input", out var arguments)) throw new ArgumentException("Invalid native invocation.");
                    var updated = await StampAsync(join, capture, tool, invocation, arguments, ct);
                    await output.WriteLineAsync(WrapOutput(provider, updated).GetRawText());
                    return 0;
                default: return 0;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            if (recognized) { await error.WriteLineAsync("CoCern native capture could not be verified."); return 2; }
            return 0;
        }
    }

    internal static async Task<string?> ReadInputAsync(TextReader input, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        while (await input.ReadAsync(buffer.AsMemory(), ct) is var read && read != 0)
        {
            if (text.Length + read > MaximumInputBytes) return null;
            text.Append(buffer, 0, read);
        }
        var value = text.ToString();
        return Encoding.UTF8.GetByteCount(value) <= MaximumInputBytes ? value : null;
    }

    internal static NativeHookContext? ParseContext(string provider, JsonElement input)
    {
        if (provider is not ("codex" or "claude") || input.ValueKind != JsonValueKind.Object || !Unique(input)) return null;
        var eventName = ChatLocators.Text(input, "hook_event_name");
        var native = ChatLocators.Text(input, "session_id");
        var cwd = ChatLocators.Text(input, "cwd");
        if (eventName is not ("SessionStart" or "PreToolUse" or "Stop") || native is not { Length: 32 or 36 } ||
            !Guid.TryParseExact(native, native.Length == 32 ? "N" : "D", out var id) ||
            cwd is not { Length: > 0 and <= 32768 } || !Path.IsPathFullyQualified(cwd) || cwd.Any(char.IsControl)) return null;
        // session_id is the documented parent capture for delegated events. Neither agent_id nor a
        // title can establish lineage. Refuse conflicting explicit parent claims instead of guessing.
        foreach (var field in new[] { "parentID", "parent_session_id", "parent_sessionId" })
            if (input.TryGetProperty(field, out var parent) && (parent.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(parent.GetString(), out var parentId) || parentId != id)) return null;
        var title = provider == "claude" ? ChatLocators.Text(input, "session_title") : null;
        if (title is { Length: > 512 } || title?.Any(char.IsControl) == true) title = null;
        return new(provider, id.ToString("D"), cwd, title);
    }

    internal static bool Unique(JsonElement value, int depth = 0)
    {
        if (depth > 32) return false;
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject())
                if (!names.Add(item.Name) || !Unique(item.Value, depth + 1)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (!Unique(item, depth + 1)) return false;
        return true;
    }

    private static readonly string[] SelfFields = ["sessionId", "sessionToken", "repositoryId", "requestId", "_cocernNative"];

    internal static JsonElement CloneArguments(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !Unique(input) || Encoding.UTF8.GetByteCount(input.GetRawText()) > MaximumInputBytes)
            throw new ArgumentException("A bounded native argument object is required.");
        var copy = JsonNode.Parse(input.GetRawText())!.AsObject();
        foreach (var field in SelfFields) copy.Remove(field);
        return JsonSerializer.SerializeToElement(copy);
    }

    // Codex's allow form stays an inactive test candidate until its installed permission gate passes.
    internal static JsonElement WrapOutput(string provider, JsonElement updated)
    {
        if (provider is not ("claude" or "codex")) throw new ArgumentException("Unsupported native provider.");
        var output = new JsonObject { ["hookEventName"] = "PreToolUse", ["updatedInput"] = JsonNode.Parse(updated.GetRawText()) };
        if (provider == "codex") output["permissionDecision"] = "allow";
        return JsonSerializer.SerializeToElement(new JsonObject { ["hookSpecificOutput"] = output });
    }

    internal static async Task<JsonElement> StampAsync(NativeJoin join, NativeHookContext capture, string tool, string invocation, JsonElement input, CancellationToken ct,
        TimeProvider? clock = null)
    {
        if (!ValidInvocation(tool, invocation) || capture.Provider is not ("claude" or "codex")) throw new ArgumentException("Invalid native invocation.");
        var business = CloneArguments(input);
        // Validate all canonical scalars before networking or recording a pending intent.
        _ = Canonical(business);
        var handle = await join.EnsureAsync(capture, ct);
        await using var binding = await NativeJoinBinding.AcquireAsync(join.PrivateFolder, capture.Provider, capture.NativeId,
            Environment.MachineName, handle.RepositoryId, capture.Cwd, ct, existingOnly: true);
        if (binding.CurrentHandle != handle) throw new IOException("The native binding changed before signing.");
        var expires = (clock ?? TimeProvider.System).GetUtcNow().AddMinutes(2).ToUnixTimeSeconds();
        var unsigned = JsonSerializer.SerializeToElement(new
        {
            version = 1, provider = binding.Provider, nativeId = binding.NativeId, cwd = binding.Workspace,
            repositoryId = binding.RepositoryId, invocation, expires, generation = binding.Generation
        });
        var stamp = JsonNode.Parse(unsigned.GetRawText())!.AsObject();
        stamp["mac"] = Mac(binding, tool, business, unsigned);
        var result = JsonNode.Parse(business.GetRawText())!.AsObject();
        result["_cocernNative"] = stamp;
        return JsonSerializer.SerializeToElement(result);
    }

    internal static async Task<AdapterReply> InvokeAsync(NativeJoin join, string provider, string tool, JsonElement input, CancellationToken ct, TimeProvider? clock = null)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("_cocernNative", out var stamp)) return new(false, "native_stamp_required");
        try
        {
            if (provider is not ("claude" or "codex") || !Unique(input) || Encoding.UTF8.GetByteCount(input.GetRawText()) > MaximumInputBytes ||
                stamp.ValueKind != JsonValueKind.Object || stamp.EnumerateObject().Count() != 9 ||
                stamp.GetProperty("version").GetInt32() != 1 || ChatLocators.Text(stamp, "provider") != provider ||
                ChatLocators.Text(stamp, "nativeId") is not { } native || ChatLocators.Text(stamp, "cwd") is not { } cwd ||
                ChatLocators.Text(stamp, "repositoryId") is not { Length: > 0 and <= 256 } repository ||
                ChatLocators.Text(stamp, "invocation") is not { } invocation || !ValidInvocation(tool, invocation) ||
                ChatLocators.Text(stamp, "generation") is not { Length: 32 } generation ||
                ChatLocators.Text(stamp, "mac") is not { Length: 64 } mac || !stamp.GetProperty("expires").TryGetInt64(out var expires)) return new(false, "native_stamp_invalid");
            var now = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
            if (expires <= now || expires > now + 120) return new(false, "native_stamp_expired");
            // Root self fields may only come from the verified coordinator, never the MCP caller.
            if (SelfFields.Where(field => field != "_cocernNative").Any(field => input.TryGetProperty(field, out _))) return new(false, "native_stamp_conflict");
            var business = CloneArguments(input);
            var unsigned = JsonNode.Parse(stamp.GetRawText())!.AsObject();
            unsigned.Remove("mac");
            string ownSession;
            await using (var binding = await NativeJoinBinding.AcquireAsync(join.PrivateFolder, provider, native, Environment.MachineName, repository, cwd, ct, existingOnly: true))
            {
                if (binding.Generation != generation || binding.CurrentHandle is null || binding.Provider != provider || binding.NativeId != native ||
                    binding.Workspace != cwd || binding.RepositoryId != repository ||
                    !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(mac), Convert.FromHexString(Mac(binding, tool, business, JsonSerializer.SerializeToElement(unsigned)))))
                    return new(false, "native_stamp_invalid");
                ownSession = binding.CurrentHandle!.SessionId;
                if (RelayToolCatalog.TryGet(tool, out var definition) && !definition.IsMutation &&
                    BindReadInvocation(binding, tool, invocation, business, expires, now) is { } refusal) return new(false, refusal);
            }
            // No preflight, status probe, session allocation or business call is possible above this point.
            // The hub's existing replay fingerprint is representation-sensitive. Keep stable request IDs
            // paired with the same canonical wire, including equivalent property orders/numeric spellings.
            using var canonical = JsonDocument.Parse(Canonical(business));
            return ProjectReply(await join.CallAsync(new(provider, native, cwd), invocation, tool, canonical.RootElement, ct, generation), ownSession, repository);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return new(false, "native_stamp_invalid"); }
    }

    private sealed record ReadAdmission(string Id, string Digest, long Expires);

    // This one bounded adjunct is protected by the existing binding lease. It never consumes or
    // removes mutation/uncertain history and contains no original tool arguments or credentials.
    internal static string? BindReadInvocation(NativeJoinBinding binding, string tool, string invocation, JsonElement business, long expires, long now)
    {
        if (expires <= now || expires > now + 120) return "native_stamp_expired";
        var generation = binding.Generation; // Also revalidates the lease's exact credential scope.
        var path = Path.ChangeExtension(binding.StatePath, ".reads");
        ReadAdmission[] prior = [];
        if (File.Exists(path))
        {
            if (!NodeState.IsPrivateFile(path) || new FileInfo(path).Length > MaximumInputBytes) throw new IOException("The native read cache is not private or bounded.");
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 4 });
            if (!Unique(document.RootElement) || document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 4096)
                throw new IOException("The native read cache is invalid.");
            prior = JsonSerializer.Deserialize<ReadAdmission[]>(document.RootElement) ?? throw new IOException("The native read cache is invalid.");
            if (prior.Any(row => row is null || row.Id is not { Length: 64 } || row.Digest is not { Length: 64 } || row.Expires <= 0) ||
                prior.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != prior.Length) throw new IOException("The native read cache is invalid.");
        }
        var key = NativeCheckout.Hash(invocation);
        var digest = NativeCheckout.Hash(JsonSerializer.Serialize(new[] { generation, tool, Canonical(business) }));
        var live = prior.Where(row => row.Expires > now).ToList();
        var existing = live.FindIndex(row => row.Id == key);
        if (existing >= 0)
        {
            if (live[existing].Digest != digest) return "native_invocation_conflict";
            if (live[existing].Expires >= expires) return null;
            live[existing] = new(key, digest, expires);
        }
        else
        {
            if (live.Count >= 4096) return "native_read_capacity";
            live.Add(new(key, digest, expires));
        }
        NodeState.WritePrivately(path, JsonSerializer.SerializeToUtf8Bytes(live));
        return null;
    }

    private static AdapterReply ProjectReply(AdapterReply reply, string ownSession, string repository)
    {
        if (reply.Data is not { ValueKind: JsonValueKind.Object } data) return reply;
        var projected = JsonNode.Parse(data.GetRawText())!.AsObject();
        foreach (var field in new[] { "sessionToken", "requestId", "_cocernNative", "nativeJoin", "nativePreflight" }) projected.Remove(field);
        if (ChatLocators.Text(data, "sessionId") == ownSession) projected.Remove("sessionId");
        if (ChatLocators.Text(data, "repositoryId") == repository) projected.Remove("repositoryId");
        return reply with { Data = JsonSerializer.SerializeToElement(projected) };
    }

    private static bool ValidInvocation(string tool, string invocation) => invocation is { Length: > 0 and <= 256 } && !invocation.Any(char.IsControl) &&
        RelayToolCatalog.TryGet(tool, out _) && tool is not (RelayTools.SessionOpen or RelayTools.SessionBind);

    private static string Mac(NativeJoinBinding binding, string tool, JsonElement business, JsonElement stamp)
    {
        var value = JsonSerializer.SerializeToElement(new { protocol = "cocern-native-call-v1", scope = binding.BindingKey, tool, stamp, arguments = business });
        return Convert.ToHexStringLower(HMACSHA256.HashData(Convert.FromBase64String(binding.Secret), Encoding.UTF8.GetBytes(Canonical(value))));
    }

    internal static string Canonical(JsonElement value)
    {
        if (!Unique(value)) throw new ArgumentException("Duplicate native JSON properties are invalid.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(value, writer, 0);
        return Encoding.UTF8.GetString(stream.ToArray());

        static void Write(JsonElement item, Utf8JsonWriter writer, int depth)
        {
            if (depth > 32) throw new ArgumentException("Native JSON exceeds its depth bound.");
            switch (item.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in item.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    { writer.WritePropertyName(property.Name); Write(property.Value, writer, depth + 1); }
                    writer.WriteEndObject(); break;
                case JsonValueKind.Array:
                    writer.WriteStartArray(); foreach (var child in item.EnumerateArray()) Write(child, writer, depth + 1); writer.WriteEndArray(); break;
                case JsonValueKind.String: writer.WriteStringValue(item.GetString()); break;
                case JsonValueKind.Number: writer.WriteRawValue(Number(item.GetRawText())); break;
                case JsonValueKind.True: writer.WriteBooleanValue(true); break;
                case JsonValueKind.False: writer.WriteBooleanValue(false); break;
                case JsonValueKind.Null: writer.WriteNullValue(); break;
                default: throw new ArgumentException("Invalid native JSON value.");
            }
        }

        static string Number(string raw)
        {
            if (raw.Length > 128) throw new ArgumentException("Native number exceeds its bound.");
            var pieces = raw.ToLowerInvariant().Split('e');
            var exponent = pieces.Length == 2 ? int.Parse(pieces[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
            if (exponent is < -100000 or > 100000) throw new ArgumentException("Native number exponent exceeds its bound.");
            var negative = pieces[0].StartsWith('-');
            var mantissa = negative ? pieces[0][1..] : pieces[0];
            var dot = mantissa.IndexOf('.');
            if (dot >= 0) exponent -= mantissa.Length - dot - 1;
            var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
            if (digits.Length == 0) return "0";
            var trimmed = digits.TrimEnd('0'); exponent += digits.Length - trimmed.Length;
            var sign = negative ? "-" : "";
            var point = trimmed.Length + exponent;
            // Keep integral business versions readable by Int64 validators. Expand only within the
            // already-small numeric token bound; extreme exponents stay exact scientific notation.
            if (exponent >= 0 && sign.Length + point <= 128) return sign + trimmed + new string('0', exponent);
            if (exponent < 0 && point > 0) return sign + trimmed.Insert(point, ".");
            if (exponent < 0 && sign.Length + 2 - point + trimmed.Length <= 128) return sign + "0." + new string('0', -point) + trimmed;
            return sign + trimmed + (exponent == 0 ? "" : "e" + exponent.ToString(CultureInfo.InvariantCulture));
        }
    }
}
