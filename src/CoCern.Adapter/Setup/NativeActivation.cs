using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using CoCern.Adapter.Node;

namespace CoCern.Adapter.Setup;

// Planning only. No caller may turn a public claim or hook input into permission evidence.
internal static class NativeActivation
{
    internal enum Choice { ActivateOwnedNative, EnableOwnedFileClaims, DisableOwnedFileClaims }

    internal sealed class PermissionEvidence
    {
        private PermissionEvidence(string definition, string profile, string folder, string helper, DateTimeOffset expires)
            => (Definition, Profile, Folder, Helper, Expires) = (definition, profile, folder, helper, expires);
        private string Definition { get; }
        private string Profile { get; }
        private string Folder { get; }
        private string Helper { get; }
        private DateTimeOffset Expires { get; }

        internal bool Matches(NativeRegistration registration, DateTimeOffset now) => registration.StillCurrent() &&
            Definition == registration.Definition && Profile == registration.Profile && Folder == registration.Folder &&
            Helper == typeof(NativeRegistration).Assembly.ManifestModule.ModuleVersionId.ToString("D") &&
            now <= Expires && Expires <= now.AddMinutes(10);
        internal bool SameAs(PermissionEvidence other) => Definition == other.Definition && Profile == other.Profile &&
            Folder == other.Folder && Helper == other.Helper && Expires == other.Expires;

        internal static PermissionEvidence? Read(string folder, string profile, string provider, DateTimeOffset now)
        {
            try
            {
                var registration = Registration(folder, profile, provider);
                if (registration is null || NodeState.ReadEnrollment(registration.Folder) is not { } enrollment ||
                    NodeState.NativeCredentialIdentity(registration.Folder) is not { } scope) return null;
                var path = EvidencePath(registration.Folder, provider);
                if (!NodeState.IsPrivateFile(path) || new FileInfo(path).Length > 8192) return null;
                var saved = JsonSerializer.Deserialize<SignedAttestation>(File.ReadAllBytes(path));
                if (saved?.Body is not { } body || saved.Mac is not { Length: 32 } || !CryptographicOperations.FixedTimeEquals(saved.Mac, Sign(body, enrollment.Credential)) ||
                    body.Provider != provider || body.Profile != registration.Profile || body.Folder != registration.Folder || body.Definition != registration.Definition ||
                    body.Helper != typeof(NativeRegistration).Assembly.ManifestModule.ModuleVersionId.ToString("D") || body.Scope != scope ||
                    body.Issued > now || body.Issued < now.AddMinutes(-5) || body.Expires <= now || body.Expires > body.Issued.AddMinutes(5) || !registration.StillCurrent()) return null;
                return new PermissionEvidence(body.Definition, body.Profile, body.Folder, body.Helper, body.Expires);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.Security.SecurityException) { return null; }
        }
    }

    // This is an operator attestation, not a provider-authenticated permission observation.
    // The fixed private file is authenticated with the enrolled credential and never accepted as public JSON input.
    private sealed record Attestation(string Provider, string Profile, string Folder, string Definition, string Helper, string Scope, DateTimeOffset Issued, DateTimeOffset Expires);
    private sealed record SignedAttestation(Attestation Body, byte[] Mac);
    private static string EvidencePath(string folder, string provider) => Path.Combine(folder, "native-permission-" + provider + ".json");
    private static byte[] Sign(Attestation body, string credential) => HMACSHA256.HashData(Encoding.UTF8.GetBytes(credential), JsonSerializer.SerializeToUtf8Bytes(body));

    internal static PermissionEvidence? CollectPermissionEvidence(string folder, string profile, string provider, DateTimeOffset now) =>
        PermissionEvidence.Read(folder, profile, provider, now);

    internal static string ConfirmPermissionsInteractively(string folder, string profile, string provider, string dotnet, string adapter, TimeProvider clock)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected) return "interactive_console_required";
        var registration = Registration(folder, profile, provider);
        if (registration is null || folder.Any(char.IsControl) || adapter.Any(char.IsControl) || !CurrentHelper(adapter) ||
            !CandidateMatches(registration, dotnet, adapter) || provider != "codex" ||
            !ClientSettings.ExactOwnedLaunch(Path.Combine(registration.Profile, "config.toml"), registration.Folder, registration.Profile, provider, dotnet, adapter, native: false) ||
            NodeState.ReadEnrollment(registration.Folder) is not { } enrollment ||
            NodeState.NativeCredentialIdentity(registration.Folder) is not { } scope) return "owned_native_candidate_required";
        var helper = typeof(NativeRegistration).Assembly.ManifestModule.ModuleVersionId.ToString("D");
        Console.WriteLine("Optional operator confirmation; this is not a client-generated attestation.");
        Console.WriteLine($"Provider: {provider}\nProfile: {registration.Profile}\nHelper: {Path.GetFullPath(adapter)} ({helper})\nCandidate definition: {registration.Definition}\nEnrolled scope: {registration.Folder}; hub: {enrollment.Hub}");
        Console.WriteLine("First check this exact installed client and profile: allow proceeds, ask prompts, cancel does not proceed, and deny does not proceed. Check the actual client outcomes before confirming.");
        Console.WriteLine("Type exactly: I VERIFIED ALLOW ASK CANCEL DENY");
        if (Console.ReadLine() != "I VERIFIED ALLOW ASK CANCEL DENY") return "permission_confirmation_cancelled";
        if (!registration.StillCurrent() || NodeState.NativeCredentialIdentity(registration.Folder) != scope ||
            !CandidateMatches(registration, dotnet, adapter) ||
            !ClientSettings.ExactOwnedLaunch(Path.Combine(registration.Profile, "config.toml"), registration.Folder, registration.Profile, provider, dotnet, adapter, native: false) ||
            NodeState.ReadEnrollment(registration.Folder) is not { } current || current.Credential != enrollment.Credential) return "owned_native_candidate_changed";
        var now = clock.GetUtcNow();
        var body = new Attestation(provider, registration.Profile, registration.Folder, registration.Definition, helper, scope, now, now.AddMinutes(5));
        NodeState.WritePrivately(EvidencePath(registration.Folder, provider), JsonSerializer.SerializeToUtf8Bytes(new SignedAttestation(body, Sign(body, current.Credential))));
        return "operator_permission_confirmed";
    }
    internal const string PermissionRequired = "permission_verification_required";
    internal sealed record Dependency(string Path, byte[] Digest);
    internal sealed record PlanResult(string? Refusal, IReadOnlyList<ClientPlan> Plans, IReadOnlyList<byte[]?> Snapshots, IReadOnlyList<Dependency> Dependencies);
    private static PlanResult Planned(NativeRegistration registration, string dotnet, string adapter, bool includeEvidence, params ClientPlan[] plans)
    {
        var config = registration.Provider == "codex" ? Path.Combine(registration.Profile, "config.toml") : Path.Combine(Directory.GetParent(registration.Profile)!.FullName, ".claude.json");
        var launch = SetupEnvironment.AdapterLaunch(dotnet, adapter);
        var paths = new[] { config, Path.Combine(registration.Folder, "native-client-" + registration.Provider + ".candidate.json"),
            Path.Combine(registration.Folder, "node.json"), Path.Combine(registration.Folder, "credential"), adapter, launch.Command }
            .Concat(includeEvidence ? [EvidencePath(registration.Folder, registration.Provider)] : []).ToArray();
        if (paths.Any(path => !Path.IsPathFullyQualified(path))) return Refuse("absolute_helper_required");
        var dependencies = paths.Select(Path.GetFullPath).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(path => new Dependency(path, HashFile(path))).ToArray();
        if (!registration.StillCurrent()) return Refuse("owned_native_candidate_required");
        return new(null, plans, plans.Select(plan => File.Exists(plan.Path) ? File.ReadAllBytes(plan.Path) : null).ToArray(), dependencies);
    }

    private static byte[] HashFile(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ClientSettingsException("An ownership input is missing or is a link.");
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    internal static PlanResult PlanPromotion(string folder, string profile, string dotnet, string adapter,
        Choice choice, PermissionEvidence? evidence, DateTimeOffset now)
    {
        if (choice != Choice.ActivateOwnedNative) return Refuse("activation_choice_required");
        var registration = Registration(folder, profile, "codex");
        if (registration is null || !ClientSettings.ExactOwnedLaunch(Path.Combine(profile, "config.toml"), folder, profile, "codex", dotnet, adapter, native: false)) return Refuse("owned_native_candidate_required");
        if (!CurrentHelper(adapter)) return Refuse("current_helper_required");
        if (!CandidateMatches(registration, dotnet, adapter)) return Refuse("candidate_helper_mismatch");
        if (!File.Exists(Path.Combine(registration.Profile, "hooks.json"))) return Refuse("existing_hooks_file_required");
        if (HooksDisabled(CodexNativeSetup.ReadHooks(Path.Combine(registration.Profile, "hooks.json")))) return Refuse("hooks_disabled");
        var savedEvidence = CollectPermissionEvidence(registration.Folder, registration.Profile, registration.Provider, now);
        if (evidence is null || savedEvidence is null || !evidence.SameAs(savedEvidence) || !evidence.Matches(registration, now)) return Refuse(PermissionRequired);
        var candidatePath = Path.Combine(registration.Folder, "native-client-codex.candidate.json");
        using var candidate = JsonDocument.Parse(File.ReadAllBytes(candidatePath));
        var hooks = candidate.RootElement.GetProperty("hooks");
        var additions = new List<(string Event, JsonObject Entry)>();
        foreach (var name in new[] { "SessionStart", "PreToolUse" })
            foreach (var item in hooks.GetProperty(name).EnumerateArray())
                additions.Add((name, JsonNode.Parse(item.GetRawText())!.AsObject()));
        var hookPlan = PlanEntries(Path.Combine(registration.Profile, "hooks.json"), additions, remove: false);
        var launch = ClientSettings.PlanNativeCodex(Path.Combine(registration.Profile, "config.toml"), dotnet, adapter,
            registration.Folder, registration.Profile, null);
        return Planned(registration, dotnet, adapter, true, hookPlan, launch);
    }

    internal static PlanResult PlanFileClaims(string folder, string profile, string dotnet, string adapter, Choice choice, string provider = "codex")
    {
        if (choice is not (Choice.EnableOwnedFileClaims or Choice.DisableOwnedFileClaims)) return Refuse("file_claim_choice_required");
        var registration = Registration(folder, profile, provider);
        if (registration is null) return Refuse("current_owned_native_required");
        var hooksPath = provider == "codex" ? Path.Combine(registration.Profile, "hooks.json") : Path.Combine(registration.Profile, "settings.json");
        if (provider == "codex")
        {
            if (!ClientSettings.ExactOwnedLaunch(Path.Combine(profile, "config.toml"), folder, profile, provider, dotnet, adapter, native: true) ||
                !ClientSettings.UsesCurrentNativeCodexImage(Path.Combine(profile, "config.toml"), folder, profile)) return Refuse("current_owned_native_required");
        }
        else
        {
            var home = Directory.GetParent(registration.Profile)?.FullName;
            if (home is null || !string.Equals(Path.GetFileName(registration.Profile), ".claude", StringComparison.OrdinalIgnoreCase)) return Refuse("current_owned_native_required");
            if (!ClientSettings.ExactOwnedLaunch(Path.Combine(home, ".claude.json"), folder, profile, provider, dotnet, adapter, native: true)) return Refuse("current_owned_native_required");
        }
        // Restrict this plan to the exact running helper and the non-development invocation.
        if (!CurrentHelper(adapter)) return Refuse("current_helper_required");
        if (!CandidateMatches(registration, dotnet, adapter)) return Refuse("candidate_helper_mismatch");
        if (choice == Choice.EnableOwnedFileClaims && HooksDisabled(CodexNativeSetup.ReadHooks(hooksPath))) return Refuse("hooks_disabled");
        if (choice == Choice.EnableOwnedFileClaims && !NativeHooksPresent(registration, hooksPath)) return Refuse("native_hooks_required");
        var launch = SetupEnvironment.AdapterLaunch(dotnet, adapter);
        var command = provider == "codex" ? CodexNativeSetup.HookCommandLine(dotnet, adapter) : string.Join(' ', launch.Arguments.Prepend(launch.Command).Select(PosixQuote));
        string Quote(string value) => provider == "codex" ? CodexNativeSetup.QuoteHookArgument(value) : PosixQuote(value);
        var connection = Quote(registration.Folder);
        var selectedProfile = Quote(registration.Profile);
        var prefix = provider == "codex" && OperatingSystem.IsWindows() ? "$env:COCERN_FILE_CLAIMS='true'; " : "COCERN_FILE_CLAIMS=true ";
        var pre = prefix + command + " " + Quote("--native-hook") + " " + Quote(provider) + " " + Quote("--connection") + " " + connection + " " + Quote("--profile") + " " + selectedProfile;
        var post = prefix + command + " " + Quote("--file-claim-hook") + " " + Quote(provider) + " " + Quote("--connection") + " " + connection + " " + Quote("--profile") + " " + selectedProfile;
        // Codex's established PowerShell command form leaves fixed option names unquoted.
        if (provider == "codex")
        {
            pre = prefix + command + " --native-hook codex --connection " + connection + " --profile " + selectedProfile;
            post = prefix + command + " --file-claim-hook codex --connection " + connection + " --profile " + selectedProfile;
        }
        JsonObject Entry(string invocation, string? matcher) => new()
        {
            ["matcher"] = matcher,
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = invocation, ["timeout"] = 20 })
        };
        var preEntry = Entry(pre, provider == "codex" ? @"^(apply_patch|functions\.apply_patch)$" : "^(Edit|Write|MultiEdit)$");
        var postEntry = Entry(post, null); postEntry.Remove("matcher");
        (string Event, JsonObject Entry)[] changes = [("PreToolUse", preEntry), ("PostToolUse", postEntry), ("PostToolUseFailure", (JsonObject)postEntry.DeepClone())];
        if (HasUnrecognizedFileClaimHandler(hooksPath, changes)) return Refuse("stale_file_claim_hooks_review_required");
        return Planned(registration, dotnet, adapter, false, PlanEntries(hooksPath, changes, choice == Choice.DisableOwnedFileClaims));
    }

    private static string PosixQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    // The hooks file is profile-specific. Conservatively refuse any claim-hook marker in this
    // file that is not one of the exact current entries, including edited/disabled definitions.
    private static bool HasUnrecognizedFileClaimHandler(string path, IReadOnlyList<(string Event, JsonObject Entry)> desired)
    {
        var document = CodexNativeSetup.ReadHooks(path);
        if (document["hooks"] is null) return false;
        if (document["hooks"] is not JsonObject hooks) throw new ClientSettingsException("Hooks are not an object.");
        foreach (var (name, value) in hooks)
        {
            if (value is not JsonArray groups) throw new ClientSettingsException("Hook event is not an array.");
            foreach (var group in groups.OfType<JsonObject>())
            {
                if (group["hooks"] is not JsonArray handlers) continue;
                foreach (var handler in handlers.OfType<JsonObject>())
                {
                    if (handler["command"] is not JsonValue text || !text.TryGetValue<string>(out var command)) continue;
                    if (!command.Contains("--file-claim-hook", StringComparison.Ordinal) &&
                        !(command.Contains("COCERN_FILE_CLAIMS", StringComparison.Ordinal) && command.Contains("--native-hook", StringComparison.Ordinal))) continue;
                    if (!desired.Any(entry => entry.Event == name && JsonNode.DeepEquals(group, entry.Entry))) return true;
                }
            }
        }
        return false;
    }

    private static bool CurrentHelper(string adapter) => Path.IsPathFullyQualified(adapter) &&
        string.Equals(Path.GetFullPath(adapter), Path.GetFullPath(typeof(NativeRegistration).Assembly.Location),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool NativeHooksPresent(NativeRegistration registration, string hooksPath)
    {
        var installed = CodexNativeSetup.ReadHooks(hooksPath);
        if (HooksDisabled(installed) ||
            installed["hooks"] is not JsonObject hooks) return false;
        using var candidate = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(registration.Folder, "native-client-" + registration.Provider + ".candidate.json")));
        var desired = candidate.RootElement.GetProperty("hooks");
        return new[] { "SessionStart", "PreToolUse" }.All(name => hooks[name] is JsonArray entries &&
            desired.GetProperty(name).EnumerateArray().All(item => entries.Any(entry => JsonNode.DeepEquals(entry, JsonNode.Parse(item.GetRawText())))));
    }

    private static bool HooksDisabled(JsonObject document)
    {
        foreach (var (key, active) in new[] { ("disabled", false), ("enabled", true), ("disableAllHooks", false) })
            if (document[key] is { } value && (value is not JsonValue flag || !flag.TryGetValue<bool>(out var given) || given != active)) return true;
        return false;
    }

    private static bool CandidateMatches(NativeRegistration registration, string dotnet, string adapter)
    {
        var provider = registration.Provider;
        var launch = SetupEnvironment.AdapterLaunch(dotnet, adapter);
        // Existing Claude candidates use Windows argv quoting in a POSIX shell. Only accept
        // paths for which that legacy form has no shell expansion or quote ambiguity.
        if (provider == "claude" && launch.Arguments.Prepend(launch.Command).Append(registration.Folder).Append(registration.Profile).Any(UnsafeLegacyClaudeArgument)) return false;
        var command = provider == "codex" ? CodexNativeSetup.HookCommandLine(dotnet, adapter) : SetupEnvironment.AdapterCommandLine(dotnet, adapter);
        string Quote(string value) => provider == "codex" ? CodexNativeSetup.QuoteHookArgument(value) : LogonStart.Quote(value);
        command += " --native-hook " + provider + " --connection " + Quote(registration.Folder) + " --profile " + Quote(registration.Profile);
        using var candidate = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(registration.Folder, "native-client-" + provider + ".candidate.json")));
        var hooks = candidate.RootElement.GetProperty("hooks");
        var names = provider == "codex" ? new[] { "SessionStart", "PreToolUse" } : new[] { "SessionStart", "PreToolUse", "Stop" };
        if (hooks.ValueKind != JsonValueKind.Object || hooks.EnumerateObject().Count() != names.Length) return false;
        foreach (var name in names)
        {
            if (!hooks.TryGetProperty(name, out var groups) || groups.ValueKind != JsonValueKind.Array || groups.GetArrayLength() != 1) return false;
            var group = groups[0];
            if (group.ValueKind != JsonValueKind.Object || group.EnumerateObject().Count() != (name == "PreToolUse" ? 2 : 1) ||
                name == "PreToolUse" && ChatLocators.Text(group, "matcher") != "^mcp__cocern__.*$" ||
                !group.TryGetProperty("hooks", out var handlers) || handlers.ValueKind != JsonValueKind.Array || handlers.GetArrayLength() != 1) return false;
            var handler = handlers[0];
            if (handler.ValueKind != JsonValueKind.Object || handler.EnumerateObject().Count() != (name == "Stop" ? 4 : 3) ||
                ChatLocators.Text(handler, "type") != "command" || ChatLocators.Text(handler, "command") != command ||
                !handler.TryGetProperty("timeout", out var timeout) || !timeout.TryGetInt32(out var seconds) || seconds != (name == "Stop" ? ClientSettings.StopHookTimeoutSeconds : 20) ||
                name == "Stop" && (!handler.TryGetProperty("asyncRewake", out var asyncRewake) || asyncRewake.ValueKind != JsonValueKind.True)) return false;
        }
        return registration.StillCurrent();
    }

    private static bool UnsafeLegacyClaudeArgument(string value) => value.Any(c => char.IsControl(c) || c is '$' or '`' or '"') || value.Contains("\\\\", StringComparison.Ordinal);

    private static NativeRegistration? Registration(string folder, string profile, string provider)
    {
        if (provider is not ("codex" or "claude") || !Path.IsPathFullyQualified(folder) || !Path.IsPathFullyQualified(profile) || NodeState.NativeCredentialIdentity(folder) is null) return null;
        folder = Path.GetFullPath(folder); profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
        return NativeRegistration.Read(Path.Combine(folder, "native-client-" + provider + ".candidate.json"), provider, folder, profile);
    }

    internal static bool RegistrationCurrent(string folder, string profile, string provider, PermissionEvidence? evidence, DateTimeOffset now, bool requireEvidence)
    {
        var registration = Registration(folder, profile, provider);
        if (registration is null) return false;
        if (!requireEvidence) return true;
        var saved = CollectPermissionEvidence(folder, profile, provider, now);
        return evidence is not null && saved is not null && evidence.SameAs(saved) && evidence.Matches(registration, now);
    }

    private static PlanResult Refuse(string reason) => new(reason, [], [], []);

    private static ClientPlan PlanEntries(string path, IReadOnlyList<(string Event, JsonObject Entry)> changes, bool remove)
    {
        var exists = File.Exists(path);
        var document = CodexNativeSetup.ReadHooks(path);
        var before = document.DeepClone();
        if (document["hooks"] is not (null or JsonObject)) throw new ClientSettingsException("Hooks are not an object.");
        var hooks = document["hooks"] as JsonObject ?? new JsonObject();
        foreach (var (eventName, entry) in changes)
        {
            if (hooks[eventName] is not (null or JsonArray)) throw new ClientSettingsException("Hook event is not an array.");
            var entries = hooks[eventName] as JsonArray ?? new JsonArray();
            // Exact whole-entry identity only: disabled or edited handlers are never rewritten or removed.
            var matches = entries.Where(item => JsonNode.DeepEquals(item, entry)).ToArray();
            if (remove) foreach (var item in matches) entries.Remove(item);
            else if (matches.Length == 0)
            {
                var desired = (string?)entry["hooks"]?[0]?["command"];
                if (entries.OfType<JsonObject>().Any(group => group["hooks"] is JsonArray handlers &&
                    handlers.OfType<JsonObject>().Any(handler => (string?)handler["command"] == desired)))
                    throw new ClientSettingsException("An owned hook has different or disabled settings. Review it before activation; it was preserved.");
                entries.Add(entry.DeepClone());
            }
            if (hooks[eventName] is null && entries.Count > 0) hooks[eventName] = entries;
        }
        if (document["hooks"] is null && hooks.Count > 0) document["hooks"] = hooks;
        if (JsonNode.DeepEquals(before, document)) return ClientPlan.Unchanged(path);
        return ClientPlan.Write(path, JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true }), exists);
    }
}
