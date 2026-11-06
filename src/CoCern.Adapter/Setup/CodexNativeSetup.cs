using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter.Node;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;

namespace CoCern.Adapter.Setup;

/// <summary>Owned native setup. Hook trust remains exclusively Codex's explicit review.</summary>
internal static class CodexNativeSetup
{
    internal static string Profile(string home) =>
        Path.GetFullPath(Path.GetFullPath(home) == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) && Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } configured ? configured : Path.Combine(home, ".codex"));
    internal static string SkillPath(string home) => Path.Combine(home, ".agents", "skills", "cocern-workflow", "SKILL.md");
    internal static string SkillText()
    {
        using var stream = typeof(CodexNativeSetup).Assembly.GetManifestResourceStream("CoCern.Workflow.SKILL.md") ?? throw new IOException("The CoCern workflow skill is unavailable.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static string? DevelopmentRoot(string adapter)
    {
        var version = Path.GetDirectoryName(Path.GetFullPath(adapter));
        var versions = version is null ? null : Path.GetDirectoryName(version);
        var root = versions is null ? null : Path.GetDirectoryName(versions);
        return versions is not null && Path.GetFileName(versions) == "versions" && root is not null && File.Exists(Path.Combine(root, "current.json")) ? root : null;
    }

    internal static DashboardCodexReadiness Prepare(string home, string profile, string dotnet, string adapter,
        IReadOnlyDictionary<string, string> environment, TimeProvider clock, string? cacheRoot = null)
    {
        if (!environment.TryGetValue("COCERN_TOKEN_FILE", out var tokenFile)) throw new ClientSettingsException("CoCern's owned local token file is missing (native_token_permissions).");
        try { NodeState.ProtectOwnedFile(tokenFile); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { throw new ClientSettingsException("CoCern could not protect its owned local token file (native_token_permissions). Run the hub as the file's owner, then prepare again. Its contents were preserved."); }
        var development = DevelopmentRoot(adapter);
        var candidate = ClientSettings.StageLocalNativeCandidate("codex", dotnet, adapter, environment, cacheRoot, development, profile)
            ?? throw new ClientSettingsException("CoCern could not prepare its private native connection. Your existing app settings were preserved.");
        using var data = JsonDocument.Parse(File.ReadAllBytes(candidate));
        var hooksPath = Path.Combine(profile, "hooks.json");
        var hooks = MergeHooks(ReadHooks(hooksPath), data.RootElement.GetProperty("hooks"));
        var hookBytes = JsonSerializer.SerializeToUtf8Bytes(hooks, new JsonSerializerOptions { WriteIndented = true });
        var skill = SkillPath(home);
        var text = SkillText();
        var ownership = skill + ".cocern-sha256";
        if (File.Exists(skill) && new FileInfo(skill).Length > 65536) throw new ClientSettingsException("The existing cocern-workflow skill is too large to update safely.");
        if (File.Exists(skill) && File.ReadAllText(skill) != text &&
            (!File.Exists(ownership) || new FileInfo(ownership).Length > 128 || File.ReadAllText(ownership) != NativeCheckout.Hash(File.ReadAllText(skill))))
            throw new ClientSettingsException("The existing cocern-workflow skill has user changes. Move or rename it, then prepare again.");
        var launch = ClientSettings.PlanNativeCodex(Path.Combine(profile, "config.toml"), dotnet, adapter,
            Path.GetDirectoryName(candidate)!, profile, development);
        if (!File.Exists(hooksPath) || !File.ReadAllBytes(hooksPath).AsSpan().SequenceEqual(hookBytes))
            ClientPlan.Write(hooksPath, hookBytes, File.Exists(hooksPath)).Apply(clock);
        if (!File.Exists(skill) || File.ReadAllText(skill) != text)
            ClientPlan.Write(skill, Encoding.UTF8.GetBytes(text), File.Exists(skill)).Apply(clock);
        if (!File.Exists(ownership) || File.ReadAllText(ownership) != NativeCheckout.Hash(text)) File.WriteAllText(ownership, NativeCheckout.Hash(text), Encoding.UTF8);
        launch.Apply(clock);
        return Inspect(home, profile, environment, cacheRoot);
    }

    internal static JsonObject ReadHooks(string path)
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 1024 * 1024) throw new ClientSettingsException("Codex hooks.json is too large to update safely.");
        try
        {
            using var parsed = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 32 });
            if (!NativeJoinHook.Unique(parsed.RootElement)) throw new JsonException();
            return JsonNode.Parse(parsed.RootElement.GetRawText()) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException) { throw new ClientSettingsException("Codex hooks.json could not be read safely. Fix or move it, then prepare again."); }
    }

    internal static JsonObject MergeHooks(JsonObject document, JsonElement desired)
    {
        if (document["hooks"] is not (null or JsonObject)) throw new ClientSettingsException("Codex hooks.json has an unsupported hooks value.");
        var hooks = document["hooks"] as JsonObject ?? new JsonObject();
        if (document["hooks"] is null) document["hooks"] = hooks;
        foreach (var eventName in new[] { "SessionStart", "PreToolUse" })
        {
            if (hooks[eventName] is not (null or JsonArray)) throw new ClientSettingsException("Codex hooks.json has an unsupported event value.");
            var entries = hooks[eventName] as JsonArray ?? new JsonArray();
            var desiredCommand = desired.GetProperty(eventName)[0].GetProperty("hooks")[0].GetProperty("command").GetString()!;
            var profileIndex = desiredCommand.IndexOf(" --profile ", StringComparison.Ordinal);
            var profileArgument = profileIndex < 0 ? null : desiredCommand[profileIndex..].Split(" --development-root ", StringSplitOptions.None)[0];
            for (var i = entries.Count - 1; i >= 0; i--)
                if (entries[i] is JsonObject entry && entry["hooks"] is JsonArray handlers)
                {
                    for (var j = handlers.Count - 1; j >= 0; j--)
                        if (handlers[j] is JsonObject handler && (string?)handler["command"] is { } command && profileArgument is not null &&
                            command.Contains(" --native-hook codex --connection ", StringComparison.Ordinal) && command.Contains(profileArgument, StringComparison.Ordinal)) handlers.RemoveAt(j);
                    if (handlers.Count == 0) entries.RemoveAt(i);
                }
            foreach (var handler in desired.GetProperty(eventName).EnumerateArray())
            {
                var entry = JsonNode.Parse(handler.GetRawText())!.AsObject();
                entries.Add(entry);
            }
            if (hooks[eventName] is null) hooks[eventName] = entries;
        }
        return document;
    }

    internal static DashboardCodexReadiness Inspect(string home, string profile, IReadOnlyDictionary<string, string> environment, string? cacheRoot = null)
    {
        var initial = new DashboardCodexReadiness { State = "setup_required", Summary = "Native setup is needed", NextAction = "Prepare native setup to install the startup hooks and CoCern workflow skill." };
        var skill = SkillPath(home);
        initial = initial with { SkillInstalled = File.Exists(skill) && new FileInfo(skill).Length < 65536 && File.ReadAllText(skill) == SkillText() };
        if (!environment.TryGetValue("COCERN_TOKEN_FILE", out var tokenFile)) return initial;
        var options = AdapterOptions.FromEnvironment(key => environment.GetValueOrDefault(key));
        var folder = LocalWakeConnection.FolderFor(options, tokenFile, cacheRoot);
        var registration = NativeRegistration.Read(Path.Combine(folder, "native-client-codex.candidate.json"), "codex", folder, profile);
        if (registration is null) return initial;
        using var candidate = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "native-client-codex.candidate.json")));
        var hooks = ReadHooks(Path.Combine(profile, "hooks.json"));
        var expected = MergeHooks(new JsonObject(), candidate.RootElement.GetProperty("hooks"));
        var installed = hooks["hooks"] as JsonObject;
        var desired = expected["hooks"]!.AsObject();
        bool Matches(string name) => installed?[name] is JsonArray entries && desired[name] is JsonArray targets && targets.All(target => entries.Any(entry => JsonNode.DeepEquals(entry, target)));
        var nativeLaunch = ClientSettings.IsNativeCodex(Path.Combine(profile, "config.toml"), folder, profile);
        initial = initial with { HooksInstalled = Matches("SessionStart") && Matches("PreToolUse") && nativeLaunch };
        if (!initial.HooksInstalled || !initial.SkillInstalled) return initial;
        return NativeStartupEvidence.Verify(registration, initial with { State = "awaiting_startup", Summary = "Awaiting a trusted Codex startup", NextAction = "Review the CoCern hooks in Codex, then restart or resume a chat in a registered repository. Refresh to verify." });
    }
}
