using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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
    internal static string SkillText() => WorkflowGuidance.SkillDocument;

    // Explicit maintenance of an already-native enrolled client. It prepares new definitions, not
    // trust, startup, sessions or wake evidence, and never installs or updates the optional skill.
    internal static bool RefreshEnrolledNative(string folder, string profile, string dotnet, string adapter, TimeProvider clock, bool check)
    {
        ClientSettingsException Refused() => new("The existing native Codex connection could not be verified for this enrollment, profile and helper. No native update was applied. Restore its owned package and hook definitions before retrying.");
        folder = Path.GetFullPath(folder); profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
        var config = Path.Combine(profile, "config.toml");
        var originalConfig = File.Exists(config) ? File.ReadAllBytes(config) : null;
        if (originalConfig is null) throw Refused();
        if (NodeState.NativeCredentialIdentity(folder) is null || !RepairCommand.OwnsCodexNode(profile, folder, dotnet, adapter) ||
            ClientSettings.ReadNativeCodexImage(config, folder, profile) is not { } previous) throw Refused();
        var path = Path.Combine(folder, "native-client-codex.candidate.json");
        if (!File.Exists(path) || !NodeState.IsPrivateFile(path) || new FileInfo(path).Length > 65536) throw Refused();
        var originalCandidate = File.ReadAllBytes(path);
        using var data = JsonDocument.Parse(originalCandidate);
        var version = ChatLocators.Text(data.RootElement, "helperVersion");
        if (!Guid.TryParseExact(version, "D", out _) || NativeRegistration.Read(path, "codex", folder, profile, version) is null ||
            HelperVersion(previous.Image) != version || HelperVersion(adapter) != typeof(NativeRegistration).Assembly.ManifestModule.ModuleVersionId.ToString("D")) throw Refused();
        var suffix = " --native-hook codex --connection " + QuoteHookArgument(folder) + " --profile " + QuoteHookArgument(profile);
        var priorCommand = HookCommandLine(previous.Dotnet, previous.Image) + suffix;
        var legacyCommand = SetupEnvironment.AdapterCommandLine(previous.Dotnet, previous.Image) +
            " --native-hook codex --connection " + LogonStart.Quote(folder) + " --profile " + LogonStart.Quote(profile);
        var nextCommand = HookCommandLine(dotnet, adapter) + suffix;
        var candidate = JsonNode.Parse(data.RootElement.GetRawText())!.AsObject();
        var hooksPath = Path.Combine(profile, "hooks.json");
        var originalHooks = File.Exists(hooksPath) ? File.ReadAllBytes(hooksPath) : null;
        if (originalHooks is null) throw Refused();
        var installed = ReadHooks(hooksPath);
        if (candidate["hooks"] is not JsonObject desired || desired.Count != 2 || installed["hooks"] is not JsonObject hooks) throw Refused();
        foreach (var eventName in new[] { "SessionStart", "PreToolUse" })
        {
            if (desired[eventName] is not JsonArray { Count: 1 } targetEntries || targetEntries[0] is not JsonObject targetEntry ||
                targetEntry["hooks"] is not JsonArray { Count: 1 } targetHandlers || targetHandlers[0] is not JsonObject target ||
                target["command"] is not JsonValue targetValue || !targetValue.TryGetValue<string>(out var command) || command != priorCommand && command != legacyCommand ||
                hooks[eventName] is not JsonArray entries) throw Refused();
            var found = 0;
            foreach (var entryNode in entries)
            {
                if (entryNode is not JsonObject entry || entry["hooks"] is not JsonArray handlers) throw Refused();
                foreach (var handlerNode in handlers)
                {
                    if (handlerNode is not JsonObject handler || handler["command"] is not JsonValue value || !value.TryGetValue<string>(out var installedCommand) || installedCommand != command) continue;
                    if (!SameHookDefinition(handler, target, false) || !SameHookDefinition(entry, targetEntry, true)) throw Refused();
                    handler["command"] = nextCommand;
                    found++;
                }
            }
            if (found != 1) throw Refused();
            target["command"] = nextCommand;
        }
        candidate["helperVersion"] = typeof(NativeRegistration).Assembly.ManifestModule.ModuleVersionId.ToString("D");
        candidate.Remove("definition");
        candidate["definition"] = NativeCheckout.Hash(NativeJoinHook.Canonical(JsonSerializer.SerializeToElement(candidate)));
        var candidateBytes = JsonSerializer.SerializeToUtf8Bytes(candidate);
        var hookBytes = JsonSerializer.SerializeToUtf8Bytes(installed, new JsonSerializerOptions { WriteIndented = true });
        var launch = ClientSettings.PlanNativeCodex(config, dotnet, adapter, folder, profile, null);
        var candidateChanged = !JsonNode.DeepEquals(JsonNode.Parse(data.RootElement.GetRawText()), candidate);
        var hooksChanged = !JsonNode.DeepEquals(ReadHooks(hooksPath), installed);
        if (!check && (candidateChanged || hooksChanged || !launch.IsUnchanged))
        {
            FileStream OpenExclusive(string file)
            {
                try { return new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { throw new ClientSettingsException("The native helper update requires exclusive access to its existing config, hooks and private candidate. No native update was applied. Retry when these files are available."); }
            }
            bool Matches(FileStream file, byte[] bytes)
            {
                if (file.Length != bytes.Length) return false;
                file.Position = 0; var current = new byte[bytes.Length]; file.ReadExactly(current);
                return current.AsSpan().SequenceEqual(bytes);
            }
            void Write(FileStream file, ReadOnlySpan<byte> bytes)
            {
                file.Position = 0; file.Write(bytes); file.SetLength(bytes.Length); file.Flush(flushToDisk: true);
            }
            // Acquire every handle before mutation. Reverse disposal makes the candidate and hooks
            // readable before exposing the changed MCP launch to a hot reload.
            using var configFile = OpenExclusive(config);
            using var hooksFile = OpenExclusive(hooksPath);
            using var candidateFile = OpenExclusive(path);
            if (!Matches(configFile, originalConfig) || !Matches(hooksFile, originalHooks) || !Matches(candidateFile, originalCandidate) ||
                !NodeState.IsPrivateFile(path)) throw Refused();
            var backup = $".cocern-backup-{clock.GetLocalNow():yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            if (candidateChanged) NodeState.WritePrivately(path + backup, originalCandidate);
            if (hooksChanged) NodeState.WritePrivately(hooksPath + backup, originalHooks);
            if (!launch.IsUnchanged) NodeState.WritePrivately(config + backup, originalConfig);
            try
            {
                if (candidateChanged) Write(candidateFile, candidateBytes);
                if (hooksChanged) Write(hooksFile, hookBytes);
                if (!launch.IsUnchanged) Write(configFile, launch.PlannedContent);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                var restored = true;
                void Restore(FileStream file, byte[] bytes)
                {
                    try
                    {
                        if (!Matches(file, bytes)) Write(file, bytes);
                    }
                    catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.SecurityException) { restored = false; }
                }
                // Restore the matching candidate and hook definitions before reloading the old MCP image.
                if (candidateChanged) Restore(candidateFile, originalCandidate);
                if (hooksChanged) Restore(hooksFile, originalHooks);
                if (restored && !launch.IsUnchanged) Restore(configFile, originalConfig);
                throw new ClientSettingsException(restored ? "The native helper update could not be written. The previous native configuration was restored; retry after correcting file access."
                    : "The native helper update was interrupted and automatic restoration was incomplete. Review config.toml, hooks.json and the private native candidate against their CoCern backups before retrying. Native verification must remain blocked until they agree.");
            }
        }
        return candidateChanged || hooksChanged || !launch.IsUnchanged;
    }

    private static bool SameHookDefinition(JsonObject installed, JsonObject expected, bool entry)
    {
        var actual = (JsonObject)installed.DeepClone(); var target = (JsonObject)expected.DeepClone();
        // Disabled choices can live on the event entry, command handler or root. Never replace them.
        foreach (var name in new[] { "disabled", "enabled" })
        {
            if (actual[name] is not null && actual[name]!.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) return false;
            actual.Remove(name); target.Remove(name);
        }
        if (entry) { actual.Remove("hooks"); target.Remove("hooks"); }
        return JsonNode.DeepEquals(actual, target);
    }

    private static string? HelperVersion(string image)
    {
        try
        {
            using var stream = File.OpenRead(image); using var executable = new PEReader(stream);
            if (!executable.HasMetadata) return null;
            var metadata = executable.GetMetadataReader();
            return metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("D");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException) { return null; }
    }

    // Ordinary setup keeps the standard MCP entry usable. An inactive candidate is only a private
    // preparation artifact: installing it never authorizes native hooks or changes a launch mode.
    internal static DashboardCodexReadiness PrepareStandard(string home, string profile, string dotnet, string adapter,
        IReadOnlyDictionary<string, string> environment, TimeProvider clock, string? cacheRoot = null)
    {
        try { InstallWorkflowSkill(home, clock); }
        catch (Exception error) when (OptionalSetupError(error)) { /* The standard connection does not depend on the skill. */ }
        var nativeLaunch = ClientSettings.HasNativeCodexLaunch(Path.Combine(profile, "config.toml"));
        if (nativeLaunch)
        {
            try { RefreshCurrentNativeCandidate(profile, environment, cacheRoot); }
            catch (Exception error) when (OptionalSetupError(error)) { /* Native evidence remains fenced until separately verified. */ }
        }
        else
        {
            try
            {
                var folder = ConnectionFolder(environment, cacheRoot);
                // Keep an existing candidate's version and definition stable, including after a user
                // deliberately returns to standard mode. A helper update needs separate native verification.
                if (folder is null || !File.Exists(Path.Combine(folder, "native-client-codex.candidate.json")))
                {
                    if (environment.ContainsKey("COCERN_TOKEN_FILE"))
                        ClientSettings.StageLocalNativeCandidate("codex", dotnet, adapter, environment, cacheRoot, DevelopmentRoot(adapter), profile);
                    else if (folder is not null)
                        ClientSettings.StageNativeCandidate(folder, "codex", HookCommandLine(dotnet, adapter), DevelopmentRoot(adapter), profile);
                }
            }
            catch (Exception error) when (OptionalSetupError(error)) { /* Private staging is optional. */ }
        }
        return InspectStandard(home, profile, environment, cacheRoot);
    }

    internal static bool NeedsCurrentNativeCandidateRefresh(string profile, IReadOnlyDictionary<string, string> environment, string? cacheRoot = null)
    {
        try { return CurrentNativeCandidateRefresh(profile, environment, cacheRoot) is not null; }
        catch (Exception error) when (OptionalSetupError(error)) { return false; }
    }

    private static void RefreshCurrentNativeCandidate(string profile, IReadOnlyDictionary<string, string> environment, string? cacheRoot)
    {
        if (CurrentNativeCandidateRefresh(profile, environment, cacheRoot) is not { } update) return;
        NodeState.WritePrivately(update.Path, JsonSerializer.SerializeToUtf8Bytes(update.Candidate));
    }

    private static (string Path, JsonObject Candidate)? CurrentNativeCandidateRefresh(string profile, IReadOnlyDictionary<string, string> environment, string? cacheRoot)
    {
        var folder = ConnectionFolder(environment, cacheRoot);
        if (folder is null || !ClientSettings.UsesCurrentNativeCodexImage(Path.Combine(profile, "config.toml"), folder, profile)) return null;
        var path = Path.Combine(folder, "native-client-codex.candidate.json");
        if (!File.Exists(path) || !NodeState.IsPrivateFile(path) || new FileInfo(path).Length > 65536) return null;
        using var data = JsonDocument.Parse(File.ReadAllBytes(path));
        var version = ChatLocators.Text(data.RootElement, "helperVersion");
        var current = typeof(NativeRegistration).Assembly.ManifestModule.ModuleVersionId.ToString("D");
        if (version == current || !Guid.TryParseExact(version, "D", out _) || NativeRegistration.Read(path, "codex", folder, profile, version) is not { } registration) return null;
        // Only installer metadata changes. The authenticated candidate's exact hook definitions,
        // scope and profile survive; no startup, trust approval, call stamp or activity is produced.
        var candidate = JsonNode.Parse(data.RootElement.GetRawText())!.AsObject();
        candidate.Remove("definition");
        if (ChatLocators.Text(data.RootElement, "definition") != registration.Definition ||
            NativeCheckout.Hash(NativeJoinHook.Canonical(JsonSerializer.SerializeToElement(candidate))) != registration.Definition) return null;
        candidate["helperVersion"] = current;
        candidate["definition"] = NativeCheckout.Hash(NativeJoinHook.Canonical(JsonSerializer.SerializeToElement(candidate)));
        return (path, candidate);
    }

    internal static DashboardCodexReadiness InspectStandard(string home, string profile,
        IReadOnlyDictionary<string, string> environment, string? cacheRoot = null, string? checkout = null)
    {
        if (ClientSettings.HasNativeCodexLaunch(Path.Combine(profile, "config.toml")))
        {
            try { return Inspect(home, profile, environment, cacheRoot, checkout); }
            catch (Exception error) when (OptionalSetupError(error))
            {
                return new() { State = "setup_required", Summary = "Native setup needs attention", SkillInstalled = WorkflowSkillInstalled(home),
                    NextAction = "Review the existing native connection and its diagnostics. Its launch mode and hooks were preserved." };
            }
        }
        if (!ClientSettings.IsStandardCodexLaunchAvailable(Path.Combine(profile, "config.toml")))
            return new() { State = "setup_required", Summary = "Standard connection needs an update", SkillInstalled = WorkflowSkillInstalled(home),
                NextAction = "Use Connect or Repair to update the owned adapter launch, then open or resume a chat." };
        return new() { State = "standard", Summary = "Standard connection configured", SkillInstalled = WorkflowSkillInstalled(home),
            NextAction = "Open or resume a chat to use CoCern." };
    }

    private static bool OptionalSetupError(Exception error) => error is ClientSettingsException or IOException or UnauthorizedAccessException or
        ArgumentException or JsonException or InvalidOperationException or System.Security.SecurityException or AdapterConfigurationException;

    private static bool WorkflowSkillInstalled(string home)
    {
        try { var skill = SkillPath(home); return File.Exists(skill) && new FileInfo(skill).Length < 65536 && File.ReadAllText(skill) == SkillText(); }
        catch (Exception error) when (OptionalSetupError(error)) { return false; }
    }

    internal static bool CanRepairWorkflowSkill(string home)
    {
        try
        {
            var skill = SkillPath(home); var ownership = skill + ".cocern-sha256";
            if (!File.Exists(skill)) return !Directory.Exists(skill);
            if (new FileInfo(skill).Length > 65536) return false;
            var text = File.ReadAllText(skill);
            return text != SkillText() && File.Exists(ownership) && new FileInfo(ownership).Length <= 128 && File.ReadAllText(ownership) == NativeCheckout.Hash(text);
        }
        catch (Exception error) when (OptionalSetupError(error)) { return false; }
    }

    private static void InstallWorkflowSkill(string home, TimeProvider clock)
    {
        var skill = SkillPath(home); var text = SkillText(); var ownership = skill + ".cocern-sha256";
        if (File.Exists(skill) && new FileInfo(skill).Length > 65536) throw new ClientSettingsException("The existing cocern-workflow skill is too large to update safely.");
        if (File.Exists(skill) && File.ReadAllText(skill) != text &&
            (!File.Exists(ownership) || new FileInfo(ownership).Length > 128 || File.ReadAllText(ownership) != NativeCheckout.Hash(File.ReadAllText(skill))))
            throw new ClientSettingsException("The existing cocern-workflow skill has user changes. Its contents were preserved.");
        if (!File.Exists(skill) || File.ReadAllText(skill) != text)
            ClientPlan.Write(skill, Encoding.UTF8.GetBytes(text), File.Exists(skill)).Apply(clock);
        if (!File.Exists(ownership) || File.ReadAllText(ownership) != NativeCheckout.Hash(text)) File.WriteAllText(ownership, NativeCheckout.Hash(text), Encoding.UTF8);
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
        var development = DevelopmentRoot(adapter);
        string? candidate;
        if (environment.TryGetValue("COCERN_TOKEN_FILE", out var tokenFile))
        {
            try { NodeState.ProtectOwnedFile(tokenFile); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            { throw new ClientSettingsException("CoCern could not protect its owned local token file (native_token_permissions). Run the hub as the file's owner, then prepare again. Its contents were preserved."); }
            candidate = ClientSettings.StageLocalNativeCandidate("codex", dotnet, adapter, environment, cacheRoot, development, profile);
        }
        else
        {
            var folder = ConnectionFolder(environment, cacheRoot);
            candidate = folder is null ? null : ClientSettings.StageNativeCandidate(folder, "codex", HookCommandLine(dotnet, adapter), development, profile);
        }
        candidate = candidate
            ?? throw new ClientSettingsException("CoCern could not prepare its private native connection. Your existing app settings were preserved.");
        using var data = JsonDocument.Parse(File.ReadAllBytes(candidate));
        var hooksPath = Path.Combine(profile, "hooks.json");
        var hooks = MergeHooks(ReadHooks(hooksPath), data.RootElement.GetProperty("hooks"), profile);
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

    internal static string HookCommandLine(string dotnet, string adapter)
    {
        if (!OperatingSystem.IsWindows()) return SetupEnvironment.AdapterCommandLine(dotnet, adapter);
        // Codex runs Windows hooks in the chat's PowerShell, where a quoted path needs the call operator.
        var (command, arguments) = SetupEnvironment.AdapterLaunch(dotnet, adapter);
        return "& " + string.Join(' ', arguments.Prepend(command).Select(QuoteHookArgument));
    }

    internal static string QuoteHookArgument(string argument) => OperatingSystem.IsWindows()
        ? "'" + argument.Replace("'", "''", StringComparison.Ordinal) + "'"
        : LogonStart.Quote(argument);

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

    internal static JsonObject MergeHooks(JsonObject document, JsonElement desired, string? profile = null)
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
            var legacyProfileArgument = profile is null ? null : " --profile " + LogonStart.Quote(profile);
            for (var i = entries.Count - 1; i >= 0; i--)
                if (entries[i] is JsonObject entry && entry["hooks"] is JsonArray handlers)
                {
                    for (var j = handlers.Count - 1; j >= 0; j--)
                        if (handlers[j] is JsonObject handler && (string?)handler["command"] is { } command && profileArgument is not null &&
                            command.Contains(" --native-hook codex --connection ", StringComparison.Ordinal) &&
                            (command.Contains(profileArgument, StringComparison.Ordinal) || legacyProfileArgument is not null && command.Contains(legacyProfileArgument, StringComparison.Ordinal))) handlers.RemoveAt(j);
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

    internal static DashboardCodexReadiness Inspect(string home, string profile, IReadOnlyDictionary<string, string> environment, string? cacheRoot = null, string? checkout = null)
    {
        var initial = new DashboardCodexReadiness { State = "setup_required", Summary = "Native setup is needed", NextAction = "Prepare native setup to install the startup hooks and CoCern workflow skill." };
        var skill = SkillPath(home);
        initial = initial with { SkillInstalled = File.Exists(skill) && new FileInfo(skill).Length < 65536 && File.ReadAllText(skill) == SkillText() };
        var folder = ConnectionFolder(environment, cacheRoot);
        if (folder is null) return initial;
        var registration = NativeRegistration.Read(Path.Combine(folder, "native-client-codex.candidate.json"), "codex", folder, profile);
        if (registration is null)
        {
            var candidatePath = Path.Combine(folder, "native-client-codex.candidate.json");
            if (!File.Exists(candidatePath)) return initial with { Detail = "native_candidate_missing",
                NextAction = "The native candidate is missing. Keep using the standard connection; prepare an owned candidate before requesting native verification." };
            // An old but otherwise valid candidate is distinct from malformed or foreign metadata.
            // It proves no client permission and is never itself an activation receipt.
            try
            {
                if (NodeState.IsPrivateFile(candidatePath) && new FileInfo(candidatePath).Length <= 65536)
                {
                    using var old = JsonDocument.Parse(File.ReadAllBytes(candidatePath));
                    var version = ChatLocators.Text(old.RootElement, "helperVersion");
                    if (Guid.TryParseExact(version, "D", out _) && version != typeof(NativeRegistration).Assembly.ManifestModule.ModuleVersionId.ToString("D") &&
                        NativeRegistration.Read(candidatePath, "codex", folder, profile, version) is not null)
                        return initial with { State = "setup_required", Summary = "Native candidate uses an older helper", Detail = "native_helper_outdated",
                            NextAction = "Use the owned native helper update for this profile, then obtain fresh installed-client permission evidence before activation." };
                }
            }
            catch (Exception error) when (OptionalSetupError(error)) { }
            return initial with { State = "error", Summary = "The configured native connection could not be verified", Detail = "native_private_connection_required",
                NextAction = "Check this connection's diagnostics and installed adapter version. Keep its existing enrollment and hook trust; repair the owned package if needed, then refresh Connect." };
        }
        using var candidate = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "native-client-codex.candidate.json")));
        var hooks = ReadHooks(Path.Combine(profile, "hooks.json"));
        var expected = MergeHooks(new JsonObject(), candidate.RootElement.GetProperty("hooks"), profile);
        var installed = hooks["hooks"] as JsonObject;
        var desired = expected["hooks"]!.AsObject();
        bool Matches(string name) => installed?[name] is JsonArray entries && desired[name] is JsonArray targets && targets.All(target => entries.Any(entry => JsonNode.DeepEquals(entry, target)));
        var nativeLaunch = ClientSettings.IsNativeCodex(Path.Combine(profile, "config.toml"), folder, profile);
        initial = initial with { HooksInstalled = Matches("SessionStart") && Matches("PreToolUse") && nativeLaunch };
        if (initial.HooksInstalled && initial.SkillInstalled)
            initial = initial with { State = "awaiting_call", Summary = "Ready for the first CoCern call", NextAction = "Review the CoCern hooks in Codex if needed, then use a CoCern tool in this chat. It joins automatically on its first call; refresh to verify." };
        return NativeStartupEvidence.Verify(registration, initial, checkout, AdapterOptions.ParseRepository(environment.GetValueOrDefault("COCERN_REPOSITORY")));
    }

    internal static string? ConnectionFolder(IReadOnlyDictionary<string, string> environment, string? cacheRoot = null)
    {
        if (environment.TryGetValue("COCERN_TOKEN_FILE", out var tokenFile))
        {
            var options = AdapterOptions.FromEnvironment(key => environment.GetValueOrDefault(key));
            return LocalWakeConnection.FolderFor(options, tokenFile, cacheRoot);
        }
        if (environment.TryGetValue("COCERN_NODE_DIR", out var folder) && Path.IsPathFullyQualified(folder) &&
            NodeState.NativeCredentialIdentity(folder) is not null && NativeJoinHook.ReadConnection(folder) is not null)
            return Path.GetFullPath(folder);
        return null;
    }
}
