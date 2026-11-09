using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Diagnostics;
using CoCern.Adapter.Development;
using System.IO.Pipes;
using System.Buffers.Binary;
using CoCern.Contracts;

namespace CoCern.Adapter.Node;

internal static class NativeJoinHook
{
    internal const int MaximumInputBytes = 1024 * 1024;

    internal static AdapterOptions? ReadConnection(string folder, Func<string, string?>? environment = null)
    {
        try
        {
            if (!Path.IsPathFullyQualified(folder)) return null;
            folder = Path.GetFullPath(folder);
            var repository = AdapterOptions.ParseRepository((environment ?? Environment.GetEnvironmentVariable)("COCERN_REPOSITORY"));
            for (DirectoryInfo? directory = new(folder); directory is not null; directory = directory.Parent)
                if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            if (NodeState.HasIdentityFiles(folder))
            {
                if (NodeState.NativeCredentialIdentity(folder) is null || NodeState.ReadEnrollment(folder) is not { } node) return null;
                return new(node.Hub.AbsoluteUri, node.Credential, signalR: true) { PinnedFingerprint = node.Fingerprint, NodeFolder = folder, Repository = repository };
            }
            if (LocalWakeConnection.NativeCredentialIdentity(folder) is null || LocalWakeConnection.Read(folder)?.Options is not { } local) return null;
            return new(local.BaseUri.AbsoluteUri, local.Token, local.Admin, local.SignalR)
            {
                PinnedFingerprint = local.PinnedFingerprint, NodeFolder = local.NodeFolder,
                Repository = repository ?? local.Repository, ChatOnly = local.ChatOnly, Diagnostics = local.Diagnostics,
                EventWaits = local.EventWaits, ClientChat = local.ClientChat, ExportedChat = local.ExportedChat
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or AdapterConfigurationException) { return null; }
    }

    // The setup-owned launcher identity stays stable while its helper follows the current deployment.
    internal static async Task<int> RunDevelopmentAsync(string root, string dotnet, string provider, string folder, TextReader input, TextWriter output, TextWriter error, CancellationToken ct, string? profile = null,
        Action<ProcessStartInfo>? configureProcess = null, string entry = "--native-hook")
    {
        string? text;
        try { text = await ReadInputAsync(input, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && NativeEditHook.Enabled)
        { await error.WriteLineAsync("CoCern native edit capture could not be verified."); return 2; }
        if (text is null)
        {
            if (NativeEditHook.Enabled) { await error.WriteLineAsync("CoCern native edit capture could not be verified."); return 2; }
            return 0;
        }
        var adapter = AdapterDeployment.AdapterPath(root, AdapterDeployment.Read(root));
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { adapter, entry, provider, "--connection", folder }) start.ArgumentList.Add(argument);
        if (profile is not null) { start.ArgumentList.Add("--profile"); start.ArgumentList.Add(profile); }
        configureProcess?.Invoke(start);
        NativeRegistration.RestrictPrivateEnvironment(start);
        using var process = Process.Start(start) ?? throw new IOException("The current native helper could not start.");
        using var copying = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var standard = CopyAsync(process.StandardOutput, output, copying.Token);
        var diagnostic = CopyAsync(process.StandardError, error, copying.Token);
        try
        {
            await process.StandardInput.WriteAsync(text.AsMemory(), ct);
            process.StandardInput.Close();
            var exited = process.WaitForExitAsync(ct);
            await await Task.WhenAny(exited, failed.Task);
            await Task.WhenAll(standard, diagnostic).WaitAsync(TimeSpan.FromSeconds(2), ct);
            return process.ExitCode;
        }
        finally
        {
            copying.Cancel();
            try { process.StandardInput.Close(); } catch (IOException) { }
            if (!process.HasExited)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException)
                {
                    if (!process.HasExited) process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            try { await Task.WhenAll(standard, diagnostic).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or TimeoutException) { }
            if (failed.Task.IsFaulted) _ = failed.Task.Exception;
        }

        async Task CopyAsync(TextReader from, TextWriter to, CancellationToken cancel)
        {
            try
            {
                var buffer = new char[4096];
                while (await from.ReadAsync(buffer.AsMemory(), cancel) is var read && read != 0)
                { await to.WriteAsync(buffer.AsMemory(0, read), cancel); await to.FlushAsync(cancel); }
            }
            catch (Exception exception) { failed.TrySetException(exception); throw; }
        }
    }

    internal static async Task<int> RunRegisteredAsync(string provider, TextReader input, TextWriter output, TextWriter error,
        NativeJoin join, NativeRegistration registration, CancellationToken ct)
    {
        if (provider != registration.Provider || !registration.StillCurrent()) return await Refuse();
        var requiresEditValidation = NativeEditHook.Enabled;
        try
        {
            var text = await ReadInputAsync(input, ct);
            if (text is null) return requiresEditValidation ? await Refuse() : 0;
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            requiresEditValidation = NativeEditHook.RequiresValidation(document.RootElement);
            if (ParseContext(provider, document.RootElement) is not null &&
                ChatLocators.Text(document.RootElement, "hook_event_name") is "SessionStart" or "PreToolUse")
            {
                // A hook can precede discovery or follow a backend restart. Drain the bounded set of waiting
                // backends within the existing hook budget; every backend must authenticate its own nonce.
                // Only the endpoint consumes a successful exchange as registration proof; no answering chat ID is retained.
                await registration.AnswerPendingAsync(ct, TimeSpan.FromMilliseconds(250));
            }
            if (!registration.StillCurrent()) return await Refuse();
            return await RunAsync(provider, new StringReader(text), output, error, join, ct, registration);
        }
        catch (JsonException) { return requiresEditValidation ? await Refuse() : 0; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && requiresEditValidation) { return await Refuse(); }
        catch (Exception failure) when (requiresEditValidation && failure is IOException or ArgumentException or InvalidOperationException)
        { return await Refuse(); }

        async Task<int> Refuse() { await error.WriteLineAsync("CoCern native capture could not be verified."); return 2; }
    }

    internal static async Task<int> RunAsync(string provider, TextReader input, TextWriter output, TextWriter error, NativeJoin join, CancellationToken ct, NativeRegistration? registration = null)
    {
        var editGuardEnabled = NativeEditHook.Enabled;
        // Until a valid envelope proves this is unrelated, an opted-in edit hook cannot safely abstain on malformed input.
        var recognized = editGuardEnabled;
        var blockingEdit = editGuardEnabled;
        try
        {
            var text = await ReadInputAsync(input, ct);
            if (text is null)
            {
                if (recognized) { await error.WriteLineAsync("CoCern native edit capture could not be verified."); return 2; }
                return 0;
            }
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            var eventName = ChatLocators.Text(root, "hook_event_name");
            var toolName = ChatLocators.Text(root, "tool_name");
            var edit = editGuardEnabled && eventName == "PreToolUse" && NativeEditHook.IsEdit(toolName);
            recognized = NativeEditHook.RequiresValidation(root);
            blockingEdit = recognized;
            if (recognized && !edit) throw new ArgumentException("Missing native edit tool identity.");
            var capture = ParseContext(provider, root);
            if (capture is null)
            {
                if (edit) throw new ArgumentException("Missing native edit context.");
                return 0;
            }
            switch (eventName)
            {
                case "SessionStart":
                    await join.RecordPendingAsync(capture, ct);
                    if (provider == "codex" && registration is not null)
                    {
                        const string pendingContext = "CoCern is available. Your first CoCern tool call joins this chat automatically using its private connection; do not open a replacement session. Check the inbox before coordinated work and read the task scope before accepting it.";
                        await output.WriteLineAsync(JsonSerializer.Serialize(new { hookSpecificOutput = new { hookEventName = "SessionStart", additionalContext = pendingContext } }));
                    }
                    return 0;
                case "Stop" when provider == "claude":
                    return await StopHook.RunNativeAsync(new StringReader(text), error, join, ct, ownerGone: OwningProcess.WaitUntilGoneAsync);
                case "PreToolUse":
                    var name = ChatLocators.Text(root, "tool_name");
                    if (edit) return await NativeEditHook.RunAsync(join, capture, root, error, ct);
                    const string prefix = "mcp__cocern__";
                    if (name is null || !name.StartsWith(prefix, StringComparison.Ordinal)) return 0;
                    recognized = true;
                    var tool = name[prefix.Length..];
                    if (ChatLocators.Text(root, "tool_use_id") is not { } invocation || !ValidInvocation(tool, invocation) ||
                        !root.TryGetProperty("tool_input", out var arguments)) throw new ArgumentException("Invalid native invocation.");
                    var updated = await StampAsync(join, capture, tool, invocation, arguments, ct);
                    if (provider == "codex" && registration is not null)
                        await NativeStartupEvidence.RecordStampedAsync(registration, join, capture, updated, ct);
                    await output.WriteLineAsync(WrapOutput(provider, updated).GetRawText());
                    return 0;
                default: return 0;
            }
        }
        catch (NativeJoin.Refused refused) when (provider == "codex" && registration is not null &&
            refused.Code is RelayErrorCodes.RepositoryDenied or "native_private_connection_required" or "native_handle_conflict" or "native_join_handle_conflict")
        {
            NativeStartupEvidence.RecordRefusal(registration, refused.Code);
            await error.WriteLineAsync("CoCern refused native startup: " + refused.Code + ". Check Connect for the next action.");
            return recognized ? 2 : 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (blockingEdit) { await error.WriteLineAsync("CoCern native capture could not be verified."); return 2; }
            if (provider == "codex" && registration is not null) await output.WriteLineAsync("{\"systemMessage\":\"CoCern startup is unverified. Check Connect before coordinated work; do not create a replacement session.\"}");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            if (recognized) { await error.WriteLineAsync("CoCern native capture could not be verified."); return 2; }
            if (provider == "codex" && registration is not null) await output.WriteLineAsync("{\"systemMessage\":\"CoCern startup is unverified. Check Connect before coordinated work; do not create a replacement session.\"}");
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

    internal static NativeHookContext? ParseContext(string provider, JsonElement input, bool postTool = false)
    {
        if (provider is not ("codex" or "claude") || input.ValueKind != JsonValueKind.Object || !Unique(input)) return null;
        var eventName = ChatLocators.Text(input, "hook_event_name");
        var native = ChatLocators.Text(input, "session_id");
        var cwd = ChatLocators.Text(input, "cwd");
        if (eventName is not ("SessionStart" or "PreToolUse" or "Stop") && !(postTool && eventName is "PostToolUse" or "PostToolUseFailure") || native is not { Length: 32 or 36 } ||
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
        TimeProvider? clock = null, Func<string, CancellationToken, Task<string>>? resolveCheckout = null)
    {
        if (!ValidInvocation(tool, invocation) || capture.Provider is not ("claude" or "codex")) throw new ArgumentException("Invalid native invocation.");
        var business = CloneArguments(input);
        // Validate all canonical scalars before networking or recording a pending intent.
        _ = Canonical(business);
        return await join.WithEstablishedSigningBindingAsync(capture, binding =>
        {
            if (binding.CurrentHandle is null) throw new IOException("An established native binding is required before signing.");
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
        }, ct, resolveCheckout);
    }

    internal static async Task<AdapterReply> InvokeAsync(NativeJoin join, string provider, string tool, JsonElement input, CancellationToken ct, TimeProvider? clock = null,
        Func<string, CancellationToken, Task<string>>? resolveCheckout = null,
        Func<string, byte[], CancellationToken, Task>? writeReadCache = null, Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        Func<string, string>? normalizeWorkspace = null)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("_cocernNative", out var stamp)) return new(false, "native_stamp_required");
        string? authenticationFailure = null;
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
            await using (var binding = await NativeJoinBinding.AcquireAsync(join.PrivateFolder, provider, native, Environment.MachineName, repository, cwd, ct, existingOnly: true,
                resolveCheckout: resolveCheckout, authenticate: candidate =>
                {
                    var authenticatedNow = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
                    if (expires <= authenticatedNow || expires > authenticatedNow + 120) { authenticationFailure = "native_stamp_expired"; return false; }
                    return candidate.Generation == generation && candidate.CurrentHandle is not null && candidate.Provider == provider && candidate.NativeId == native &&
                        candidate.Workspace == cwd && candidate.RepositoryId == repository &&
                        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(mac), Convert.FromHexString(Mac(candidate, tool, business, JsonSerializer.SerializeToElement(unsigned))));
                }, normalizeWorkspace: normalizeWorkspace))
            {
                now = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
                if (expires <= now || expires > now + 120) return new(false, "native_stamp_expired");
                ownSession = binding.CurrentHandle!.SessionId;
                // The stamp authenticates the original input, but only the verified binding may supply acting proof.
                var cleanBusiness = JsonNode.Parse(business.GetRawText())!.AsObject();
                cleanBusiness.Remove("actingSessionId");
                cleanBusiness.Remove("actingSessionToken");
                business = JsonSerializer.SerializeToElement(cleanBusiness);
                if (await NativeJoin.AdmitStampedInvocationAsync(binding, tool, invocation, business, expires, now, ct, writeReadCache, retryDelay) is { } refusal) return new(false, refusal);
                now = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
                if (expires <= now || expires > now + 120) return new(false, "native_stamp_expired");
            }
            // No preflight, status probe, session allocation or business call is possible above this point.
            // CallAsync chooses canonical wire for versioned fences and preserves exact legacy wire.
            return ProjectReply(await join.CallAsync(new(provider, native, cwd), invocation, tool, business, ct, generation), ownSession, repository);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return new(false, authenticationFailure ?? "native_stamp_invalid"); }
    }

    private sealed record ReadAdmission(string Id, string Digest, long Expires);

    // This one bounded adjunct is protected by the existing binding lease. It never consumes or
    // removes mutation/uncertain history and contains no original tool arguments or credentials.
    internal static async Task<string?> BindReadInvocationAsync(NativeJoinBinding binding, string tool, string invocation, JsonElement business, long expires, long now,
        bool admit = true, CancellationToken ct = default, Func<string, byte[], CancellationToken, Task>? writeReadCache = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        if (expires <= now || expires > now + 120) return "native_stamp_expired";
        var generation = binding.Generation; // Also revalidates the lease's exact credential scope.
        var path = Path.ChangeExtension(binding.StatePath, ".reads");
        ReadAdmission[] prior = [];
        byte[]? priorBytes = null;
        FileAttributes? attributes = null;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { } // Only a genuinely absent optional cache is empty history.
        if (attributes is { } present)
        {
            if ((present & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("The native read history is not a regular private file.");
            if (!NodeState.IsPrivateFile(path) || new FileInfo(path).Length > MaximumInputBytes) throw new IOException("The native read cache is not private or bounded.");
            priorBytes = File.ReadAllBytes(path);
            using var document = JsonDocument.Parse(priorBytes, new JsonDocumentOptions { MaxDepth = 4 });
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
            if (!admit) return null;
            if (live.Count >= 4096) return "native_read_capacity";
            live.Add(new(key, digest, expires));
        }
        if (!admit) return null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(live);
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (writeReadCache is null) NodeState.WritePrivately(path, bytes);
                else await writeReadCache(path, bytes, ct);
                break;
            }
            catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) &&
                (exception.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070020) or unchecked((int)0x80070021)))
            {
                ct.ThrowIfCancellationRequested();
                if (attempt >= 3 || !UnchangedPrior()) throw;
                if (retryDelay is null) await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
                else await retryDelay(TimeSpan.FromMilliseconds(25), ct);
                ct.ThrowIfCancellationRequested();
                if (!UnchangedPrior()) throw;
            }
        }
        return null;

        bool UnchangedPrior()
        {
            try
            {
                // Recovery is confined to the already-validated ephemeral cache. Never repair or
                // replace missing, linked, corrupt or externally changed state, or touch mutation history.
                return priorBytes is not null && binding.Generation == generation && NodeState.IsPrivateFile(path) &&
                    new FileInfo(path).Length == priorBytes.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(priorBytes);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
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

// Inactive registration prototype. Its only authority is a setup-owned private candidate and
// connection, never an answering chat identity. Normal setup does not launch this endpoint.
internal sealed class NativeRegistration
{
    internal const string Refusal = "Native client registration could not be verified.";
    internal const string ProtocolMetadata = "CoCern.NativeClientProtocol";
    internal const string ProtocolVersion = "1";
    internal const string PrivateServerName = "CoCern native private v1";
    internal static bool HasPrivateOptionName(string argument)
    {
        var name = argument.AsSpan();
        if (name.IsEmpty) return false;
        while (!name.IsEmpty && (name[0] is '-' or '/')) name = name[1..];
        var separator = name.IndexOfAny('=', ':');
        if (separator >= 0) name = name[..separator];
        return name.Equals("native-client", StringComparison.OrdinalIgnoreCase) || name.Equals("native-hook", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("connection", StringComparison.OrdinalIgnoreCase) || name.Equals("profile", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("development-root", StringComparison.OrdinalIgnoreCase);
    }
    internal static void RestrictPrivateEnvironment(ProcessStartInfo start)
    {
        // This noncredential feature switch can only add a refusal gate; it never supplies native identity or grants permission.
        var fileClaims = start.Environment.TryGetValue("COCERN_FILE_CLAIMS", out var flag)
            && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
        start.Environment.TryGetValue("COCERN_ROLE", out var role);
        start.Environment.TryGetValue("COCERN_PROFILE", out var profile);
        start.Environment.TryGetValue("COCERN_EVENT_WAITS", out var eventWaits);
        start.Environment.TryGetValue("COCERN_REPOSITORY", out var repository);
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("COCERN_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        // Explicit private context is the only authority. An old flag-ignoring helper or an
        // accidental backend fallback must fail before default node/profile discovery.
        start.Environment["COCERN_URL"] = "invalid-private-fallback";
        start.Environment["COCERN_TOKEN"] = "invalid-private-fallback";
        if (fileClaims) start.Environment["COCERN_FILE_CLAIMS"] = "true";
        // Catalog preferences do not supply identity or credentials; retain the app's explicit tool choices.
        if (string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase)) start.Environment["COCERN_ROLE"] = "admin";
        if (string.Equals(profile, "chat", StringComparison.OrdinalIgnoreCase)) start.Environment["COCERN_PROFILE"] = "chat";
        if (string.Equals(profile, "diagnostics", StringComparison.OrdinalIgnoreCase)) start.Environment["COCERN_PROFILE"] = "diagnostics";
        if (string.Equals(eventWaits, "true", StringComparison.OrdinalIgnoreCase)) start.Environment["COCERN_EVENT_WAITS"] = "true";
        // A configured repository is only a mapping hint. The hub still applies MayAccess
        // and validates any known clone origin; it supplies neither credentials nor identity.
        if (repository is not null) start.Environment["COCERN_REPOSITORY"] = repository;
    }

    internal static AdapterOptions ClientOptions(AdapterOptions connection, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        return new(connection.BaseUri.AbsoluteUri, connection.Token,
            admin: string.Equals(environment("COCERN_ROLE"), "admin", StringComparison.OrdinalIgnoreCase), signalR: connection.SignalR)
        {
            PinnedFingerprint = connection.PinnedFingerprint, NodeFolder = connection.NodeFolder,
            Repository = AdapterOptions.ParseRepository(environment("COCERN_REPOSITORY")) ?? connection.Repository,
            ChatOnly = string.Equals(environment("COCERN_PROFILE"), "chat", StringComparison.OrdinalIgnoreCase),
            Diagnostics = string.Equals(environment("COCERN_PROFILE"), "diagnostics", StringComparison.OrdinalIgnoreCase),
            EventWaits = string.Equals(environment("COCERN_EVENT_WAITS"), "true", StringComparison.OrdinalIgnoreCase)
        };
    }
    internal static bool ValidClientArguments(string[] args) =>
        args is ["--native-client", "codex" or "claude", "--connection", var folder, "--profile", var profile] &&
        ValidPath(folder) && ValidPath(profile);

    private static bool ValidPath(string path) => path.Length is > 0 and <= 32768 && !path.Any(char.IsControl) && Path.IsPathFullyQualified(path);

    internal static NativeRegistration? FromClientArguments(string[] args, string? helperVersion = null) => ValidClientArguments(args)
        ? Read(Path.Combine(args[3], "native-client-" + args[1] + ".candidate.json"), args[1], args[3], args[5], helperVersion) : null;

    internal AdapterOptions Connection => options;

    /// <summary>
    /// Where this registration's backends remember the hub's tool selections (<see cref="ToolSurface"/>): beside the candidate, scoped to the
    /// provider, credential and client profile but not to the helper build, so an adapter update keeps it and a new credential starts empty.
    /// </summary>
    internal string ToolSurfacePath => Path.Combine(Folder, "native-tool-surface-" + Provider + "-" +
        NativeCheckout.Hash(JsonSerializer.Serialize(new[] { "cocern-native-tool-surface-v1", Provider, scope, profileKey }))[..32] + ".json");
    internal const int MaximumFrameBytes = 2048;
    internal const int MaximumInstances = 4;
    internal static TimeSpan MaximumWait => TimeSpan.FromSeconds(15);
    internal string Provider { get; }
    internal string Folder { get; }
    internal string Profile { get; }
    internal string Definition { get; }
    internal string PipeName { get; }
    private readonly string path, scope, profileKey;
    private readonly AdapterOptions options;

    private NativeRegistration(string path, string provider, string folder, string profile, string scope, string definition, AdapterOptions options)
    {
        (this.path, Provider, Folder, Profile, this.scope, Definition, this.options) = (path, provider, folder, profile, scope, definition, options);
        profileKey = NativeCheckout.Hash("cocern-native-profile-v1:" + profile);
        PipeName = "cocern-native-" + NativeCheckout.Hash(JsonSerializer.Serialize(new[] { provider, scope, profileKey, definition }));
    }

    internal static NativeRegistration? Read(string path, string provider, string folder, string profile, string? helperVersion = null)
    {
        try
        {
            if (provider is not ("codex" or "claude") || !Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(folder) ||
                profile is not { Length: > 0 and <= 32768 } || !Path.IsPathFullyQualified(profile) || profile.Any(char.IsControl) || !Directory.Exists(profile)) return null;
            path = Path.GetFullPath(path); folder = Path.GetFullPath(folder); profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(path, Path.Combine(folder, "native-client-" + provider + ".candidate.json"), comparison) ||
                !NodeState.IsPrivateFile(path) || new FileInfo(path).Length > 65536 || NativeJoinHook.ReadConnection(folder) is not { } connection) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 12 });
            var data = document.RootElement;
            if (data.ValueKind != JsonValueKind.Object || !NativeJoinHook.Unique(data) || ChatLocators.Text(data, "kind") != "cocern-native-candidate-v1" ||
                ChatLocators.Text(data, "state") != "inactive" || ChatLocators.Text(data, "provider") != provider ||
                ChatLocators.Text(data, "connection") is not { } capturedFolder || !string.Equals(capturedFolder, folder, comparison) ||
                ChatLocators.Text(data, "profile") is not { } capturedProfile || !string.Equals(capturedProfile, profile, comparison) ||
                ChatLocators.Text(data, "helperVersion") != (helperVersion ?? typeof(NativeRegistration).Assembly.ManifestModule.ModuleVersionId.ToString("D")) ||
                ChatLocators.Text(data, "credentialScope") is not { Length: 64 } capturedScope ||
                ChatLocators.Text(data, "definition") is not { Length: 64 } definition) return null;
            var currentScope = NodeState.HasIdentityFiles(folder) ? NodeState.NativeCredentialIdentity(folder) : LocalWakeConnection.NativeCredentialIdentity(folder);
            var unsigned = JsonNode.Parse(data.GetRawText())!.AsObject(); unsigned.Remove("definition");
            if (currentScope != capturedScope || NativeCheckout.Hash(NativeJoinHook.Canonical(JsonSerializer.SerializeToElement(unsigned))) != definition) return null;
            return new(path, provider, folder, capturedProfile, capturedScope, definition, connection);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException) { return null; }
    }

    internal bool StillCurrent() => Read(path, Provider, Folder, Profile) is { } current && current.Definition == Definition && current.scope == scope;

    internal async Task<NativeRegistration?> AcceptAsync(CancellationToken ct, TimeSpan? budget = null, bool deferUntilHook = false)
    {
        ct.ThrowIfCancellationRequested();
        var limit = budget ?? MaximumWait;
        if (limit <= TimeSpan.Zero || limit > MaximumWait || !StillCurrent()) return null;
        var trace = deferUntilHook ? null : ConnectionTrace.Begin("unknown", "adapter");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var accepted = false;
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct); waiting.CancelAfter(limit);
        try
        {
            await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, MaximumInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, MaximumFrameBytes, MaximumFrameBytes);
            await pipe.WaitForConnectionAsync(waiting.Token);
            trace ??= ConnectionTrace.Begin("unknown", "adapter");
            var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var deadline = DateTimeOffset.UtcNow.Add(limit).ToUnixTimeMilliseconds();
            await WriteFrameAsync(pipe, Frame(nonce, deadline, "challenge"), waiting.Token);
            var response = await ReadFrameAsync(pipe, waiting.Token);
            if (!Valid(response, "response", nonce, deadline) || !StillCurrent()) return null;
            await WriteFrameAsync(pipe, Frame(nonce, deadline, "ack"), waiting.Token);
            accepted = true;
            return this; // Registration context only: no native/session ID is captured or retained.
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or FormatException) { return null; }
        finally
        {
            // An idle deferred listener is healthy pending work, not a failed MCP attachment.
            if (trace is not null)
            {
                var failure = accepted ? null : ct.IsCancellationRequested ? "cancelled" : "native_registration_unverified";
                ConnectionTrace.Stage("native_registration", started, accepted ? "ok" : ct.IsCancellationRequested ? "cancelled" : "error", failure);
                trace.Complete(accepted, failure);
                trace.Dispose();
            }
        }
    }

    internal async Task<bool> AnswerAsync(CancellationToken ct, TimeSpan? budget = null)
    {
        ct.ThrowIfCancellationRequested();
        var limit = budget ?? MaximumWait;
        if (limit <= TimeSpan.Zero || limit > MaximumWait || !StillCurrent()) return false;
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct); waiting.CancelAfter(limit);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(waiting.Token);
            var challenge = await ReadFrameAsync(pipe, waiting.Token);
            if (!Valid(challenge, "challenge") || !StillCurrent()) return false;
            var nonce = challenge.GetProperty("nonce").GetString()!; var deadline = challenge.GetProperty("deadline").GetInt64();
            await WriteFrameAsync(pipe, Frame(nonce, deadline, "response"), waiting.Token);
            return Valid(await ReadFrameAsync(pipe, waiting.Token), "ack", nonce, deadline) && StillCurrent();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or FormatException) { return false; }
    }

    internal async Task<bool> AnswerPendingAsync(CancellationToken ct, TimeSpan budget)
    {
        ct.ThrowIfCancellationRequested();
        if (budget <= TimeSpan.Zero || budget > MaximumWait || !StillCurrent()) return false;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var answered = false;
        // The simultaneous pipe limit is not the number of waiting backends. Retired listeners free slots for others.
        for (var instance = 0; instance < 32; instance++)
        {
            var remaining = budget - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) break;
            // Successful listeners retire, so the next connection reaches another backend, never a shared acceptance flag.
            if (await AnswerAsync(ct, remaining)) answered = true;
        }
        return answered;
    }

    private JsonElement Frame(string nonce, long deadline, string purpose)
    {
        var data = JsonSerializer.SerializeToElement(new { version = 1, nonce, deadline, provider = Provider, scope, profile = profileKey, definition = Definition });
        var frame = JsonNode.Parse(data.GetRawText())!.AsObject(); frame["mac"] = Mac(purpose, data);
        return JsonSerializer.SerializeToElement(frame);
    }

    private bool Valid(JsonElement frame, string purpose, string? nonce = null, long? deadline = null)
    {
        if (frame.ValueKind != JsonValueKind.Object || !NativeJoinHook.Unique(frame) || frame.EnumerateObject().Count() != 8 ||
            !frame.TryGetProperty("version", out var version) || !version.TryGetInt32(out var parsedVersion) || parsedVersion != 1 ||
            ChatLocators.Text(frame, "provider") != Provider || ChatLocators.Text(frame, "scope") != scope || ChatLocators.Text(frame, "profile") != profileKey ||
            ChatLocators.Text(frame, "definition") != Definition || ChatLocators.Text(frame, "nonce") is not { Length: 64 } capturedNonce ||
            !capturedNonce.All(char.IsAsciiHexDigit) || ChatLocators.Text(frame, "mac") is not { Length: 64 } mac ||
            !frame.TryGetProperty("deadline", out var expiry) || !expiry.TryGetInt64(out var capturedDeadline)) return false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (capturedDeadline <= now || capturedDeadline > now + (long)MaximumWait.TotalMilliseconds ||
            nonce is not null && nonce != capturedNonce || deadline is not null && deadline != capturedDeadline) return false;
        var unsigned = JsonNode.Parse(frame.GetRawText())!.AsObject(); unsigned.Remove("mac");
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(mac), Convert.FromHexString(Mac(purpose, JsonSerializer.SerializeToElement(unsigned))));
    }

    private string Mac(string purpose, JsonElement data)
    {
        var key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Token), Encoding.UTF8.GetBytes("cocern-native-registration-key-v1"));
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("cocern-native-registration-" + purpose + "-v1\n" + NativeJoinHook.Canonical(data))));
    }

    private static async Task<JsonElement> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, ct);
        var count = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (count is <= 0 or > MaximumFrameBytes) throw new IOException("Invalid native registration frame bound.");
        var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, ct);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 3 });
        return document.RootElement.Clone();
    }

    private static async Task WriteFrameAsync(Stream stream, JsonElement frame, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(frame.GetRawText());
        if (bytes.Length is <= 0 or > MaximumFrameBytes) throw new IOException("Invalid native registration frame bound.");
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }
    public override string ToString() => "Native registration (private)";
}
