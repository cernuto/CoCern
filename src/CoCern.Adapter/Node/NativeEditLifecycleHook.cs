using System.Text.Json;
using CoCern.Adapter.Setup;

namespace CoCern.Adapter.Node;

/// <summary>
/// <c>--file-claim-hook</c>: the PostToolUse half of the file claim lifecycle, opt-in behind <c>COCERN_FILE_CLAIMS</c>. Nothing installs it. It is a separate entry from <c>--native-hook</c> because that one
/// fails closed on input it cannot read, and a tool result can be far larger than a pre-edit event. This one prints nothing and always exits 0.
/// <para>Client shapes are assumed from documentation and have not been tried on a real client. Claude: <c>PostToolUse</c> carrying the same <c>tool_use_id</c> as its PreToolUse, a <c>tool_response</c> object with
/// <c>filePath</c>, and <c>PostToolUseFailure</c> for a failed tool. Codex: apply_patch answering a "Success" string, or an object or JSON text with <c>success</c> true or exit code 0. Any other form records no evidence.</para>
/// </summary>
internal static class NativeEditLifecycleHook
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(9);
    private static readonly string[] ShellTools = ["Bash", "PowerShell", "shell", "shell_command", "local_shell", "exec_command", "functions.shell", "functions.shell_command", "functions.exec_command"];

    internal static async Task<int> RunCommandAsync(string[] args)
    {
        try
        {
            // Standard error enters the chat.
            Console.SetError(TextWriter.Null);
            string? folder = null, profile = null, developmentRoot = null;
            var valid = args.Length is 4 or 6 or 8 && args[1] is "codex" or "claude" && args[2] == "--connection" && Path.IsPathFullyQualified(args[3]);
            if (valid) folder = args[3];
            for (var i = 4; valid && i < args.Length; i += 2)
            {
                if (!Path.IsPathFullyQualified(args[i + 1]) || args[i + 1].Length > 32768 || args[i + 1].Any(char.IsControl)) valid = false;
                else if (args[i] == "--profile" && profile is null) profile = args[i + 1];
                else if (args[i] == "--development-root" && developmentRoot is null) developmentRoot = args[i + 1];
                else valid = false;
            }
            using var budget = new CancellationTokenSource(Budget);
            if (!valid || !NativeEditHook.Enabled || folder is null || NativeJoinHook.ReadConnection(folder) is not { } connection)
            {
                // Read to the end, so the client's write never blocks.
                await PostToolHook.ReadBoundedAsync(Console.In).WaitAsync(budget.Token);
                return 0;
            }
            if (developmentRoot is not null)
            {
                await NativeJoinHook.RunDevelopmentAsync(developmentRoot, SetupEnvironment.Host().Dotnet, args[1], folder, Console.In, TextWriter.Null, TextWriter.Null, budget.Token, profile, entry: "--file-claim-hook");
                return 0;
            }
            if (profile is not null && NativeRegistration.FromClientArguments(["--native-client", args[1], "--connection", folder, "--profile", profile]) is null)
            {
                await PostToolHook.ReadBoundedAsync(Console.In).WaitAsync(budget.Token);
                return 0;
            }
            using var http = new HttpClient(RelayForwarder.CreateHandler(connection)) { Timeout = Budget };
            await using var join = new NativeJoin(folder, connection, new RelayForwarder(http, connection));
            return await RunAsync(args[1], Console.In, join, budget.Token);
        }
        catch (Exception) { /* A hook must never fail the turn. */ }
        return 0;
    }

    internal static async Task<int> RunAsync(string provider, TextReader input, NativeJoin join, CancellationToken ct)
    {
        try
        {
            if (await PostToolHook.ReadBoundedAsync(input).WaitAsync(ct) is not { } text || !NativeEditHook.Enabled) return 0;
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return 0;
            var eventName = ChatLocators.Text(root, "hook_event_name");
            if (eventName is not ("PostToolUse" or "PostToolUseFailure")) return 0;
            // A subagent's edit is not this chat's own evidence, and a present field of any shape counts as one.
            if (root.TryGetProperty("agent_id", out var agent) && agent.ValueKind != JsonValueKind.Null) return 0;
            var tool = ChatLocators.Text(root, "tool_name");
            var edit = NativeEditHook.IsEdit(tool);
            // Reads, searches and every CoCern tool, check_inbox included, leave here: chat activity and waiting never renew a claim.
            if (!edit && (eventName != "PostToolUse" || !ShellTools.Contains(tool))) return 0;
            if (NativeJoinHook.ParseContext(provider, root, postTool: true) is not { } capture) return 0;
            if (!edit && !ShellWorthSweeping(join.PrivateFolder, capture, root)) return 0;
            var claims = await join.EditClaimsAsync(capture, ct);
            var lifecycle = claims.Lifecycle(ct);
            if (edit && ChatLocators.Text(root, "tool_use_id") is { Length: > 0 and <= 256 } invocation && !invocation.Any(char.IsControl))
            {
                string? reported = null;
                var succeeded = eventName == "PostToolUse" && Succeeded(tool!, root, out reported);
                await lifecycle.ObserveAsync(NativeEditHook.IntentId(invocation), succeeded, reported, ct);
            }
            EditClaimLifecycleMarker.Set(join.PrivateFolder, capture, await lifecycle.SweepAsync(ct));
        }
        catch (Exception) { /* A hook must never fail the turn. */ }
        return 0;
    }

    /// <summary>Costs one small file read. A shell command matters only with a held claim, and then only to look at Git after naming it or when a renewal is due. The text only says when to look; Git decides.</summary>
    private static bool ShellWorthSweeping(string folder, NativeHookContext capture, JsonElement root)
    {
        if (EditClaimLifecycleMarker.Read(folder, capture) is not { } due) return false;
        return due <= DateTimeOffset.UtcNow || ShellText(root)?.Contains("git", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? ShellText(JsonElement root)
    {
        if (!root.TryGetProperty("tool_input", out var args)) return null;
        if (args.ValueKind == JsonValueKind.String) return args.GetString();
        if (args.ValueKind != JsonValueKind.Object) return null;
        foreach (var field in new[] { "command", "cmd", "script" })
        {
            if (!args.TryGetProperty(field, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Array) return string.Join(' ', value.EnumerateArray().Where(part => part.ValueKind == JsonValueKind.String).Select(part => part.GetString()));
        }
        return null;
    }

    /// <summary>Whether the tool's own result says it succeeded, in a form this hook knows. This is only one input: the file must also have changed and Git must call it dirty.</summary>
    internal static bool Succeeded(string tool, JsonElement root, out string? reportedPath)
    {
        reportedPath = null;
        if (!root.TryGetProperty("tool_response", out var response)) return false;
        if (tool is "apply_patch" or "functions.apply_patch") return PatchSucceeded(response, nested: false);
        if (response.ValueKind != JsonValueKind.Object || Failed(response)) return false;
        reportedPath = ChatLocators.Text(response, "filePath") ?? ChatLocators.Text(response, "file_path");
        return reportedPath is { Length: > 0 };
    }

    private static bool PatchSucceeded(JsonElement response, bool nested)
    {
        if (response.ValueKind == JsonValueKind.String)
        {
            var text = response.GetString()?.TrimStart() ?? "";
            if (text.StartsWith("Success", StringComparison.Ordinal)) return true;
            if (nested || !text.StartsWith('{')) return false;
            try
            {
                using var inner = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
                return PatchSucceeded(inner.RootElement, nested: true);
            }
            catch (JsonException) { return false; }
        }
        if (response.ValueKind != JsonValueKind.Object || Failed(response)) return false;
        return Flag(response, "success") == true || ExitCode(response) == 0 ||
            response.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object && !Failed(metadata) && ExitCode(metadata) == 0;
    }

    private static bool Failed(JsonElement response) =>
        Flag(response, "is_error") == true || Flag(response, "isError") == true || Flag(response, "interrupted") == true || Flag(response, "success") == false ||
        response.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null || ExitCode(response) is not (null or 0);

    private static bool? Flag(JsonElement value, string name) => value.TryGetProperty(name, out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : null;

    private static long? ExitCode(JsonElement value)
    {
        foreach (var name in new[] { "exit_code", "exitCode" })
            if (value.TryGetProperty(name, out var code)) return code.TryGetInt64(out var parsed) ? parsed : -1;
        return null;
    }
}

/// <summary>When this chat next needs looking at. It is only a hint for skipping work: losing it means a lapse, and forging it means a sweep that still proves everything.</summary>
internal static class EditClaimLifecycleMarker
{
    private static string PathFor(string folder, NativeHookContext capture) =>
        Path.Combine(folder, "file-claim-due-" + NativeCheckout.Hash(capture.Provider + "\n" + capture.NativeId));

    internal static DateTimeOffset? Read(string folder, NativeHookContext capture)
    {
        try
        {
            var path = PathFor(folder, capture);
            if (!File.Exists(path) || new FileInfo(path).Length > 32) return null;
            return long.TryParse(File.ReadAllText(path), out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Null clears it: this chat holds no claim.</summary>
    internal static void Set(string folder, NativeHookContext capture, DateTimeOffset? due)
    {
        try
        {
            var path = PathFor(folder, capture);
            if (due is { } when) NodeState.WritePrivately(path, System.Text.Encoding.ASCII.GetBytes(when.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
            else File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
