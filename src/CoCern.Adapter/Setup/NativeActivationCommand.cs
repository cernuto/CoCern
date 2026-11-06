namespace CoCern.Adapter.Setup;

// Explicit command surface. Program must dispatch this exact switch; ordinary setup never calls it.
internal static class NativeActivationCommand
{
    internal static int Run(string[] args, string dotnet, string adapter, TimeProvider clock, TextWriter output, TextWriter error)
    {
        if (args.Length != 8 || args[0] != "--native-activation" || args[1] is not ("preview" or "confirm" or "apply" or "recover") ||
            args[2] is not ("codex" or "claude") || args[3] is not ("promotion" or "enable" or "disable") ||
            args[4] != "--connection" || args[6] != "--profile" || !Path.IsPathFullyQualified(args[5]) || !Path.IsPathFullyQualified(args[7]) ||
            args[3] == "promotion" && args[2] != "codex" || args[1] == "confirm" && args[3] != "promotion")
        {
            error.WriteLine("Usage: --native-activation <preview|confirm|apply|recover> <codex|claude> <promotion|enable|disable> --connection <folder> --profile <folder>");
            return 2;
        }
        try
        {
            var folder = Path.GetFullPath(args[5]); var profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[7]));
            var provider = args[2]; var action = args[3];
            if (args[1] == "confirm")
            {
                var result = NativeActivation.ConfirmPermissionsInteractively(folder, profile, provider, dotnet, adapter, clock);
                output.WriteLine(result);
                return result == "operator_permission_confirmed" ? 0 : 1;
            }
            if (args[1] == "recover")
            {
                NativeSettingsTransaction.Recover(folder, profile, action == "promotion" ? "promotion" : "file-claims-" + provider);
                output.WriteLine("The owned settings transaction was restored from its private journal.");
                return 0;
            }
            var choice = action == "enable" ? NativeActivation.Choice.EnableOwnedFileClaims : NativeActivation.Choice.DisableOwnedFileClaims;
            var evidence = action == "promotion" ? NativeActivation.CollectPermissionEvidence(folder, profile, provider, clock.GetUtcNow()) : null;
            var preview = action == "promotion"
                ? ClientSettings.PlanNativePromotion(folder, profile, dotnet, adapter, NativeActivation.Choice.ActivateOwnedNative, evidence, clock.GetUtcNow())
                : NativeActivation.PlanFileClaims(folder, profile, dotnet, adapter, choice, provider);
            if (preview.Refusal is { } refusal)
            {
                output.WriteLine("Preview refused: " + refusal);
                if (refusal == "stale_file_claim_hooks_review_required")
                    output.WriteLine("Review the existing file-claim handlers in this profile, including their helper paths and disabled settings. They were preserved; resolve them explicitly before enabling or disabling file claims.");
                return 1;
            }
            output.WriteLine("Operator-confirmed evidence is distinct from client startup verification. No live acceptance is implied.");
            output.WriteLine(action == "promotion" ? "Plan: install the owned SessionStart and PreToolUse handlers and switch the owned Codex connection to its native launcher." :
                action == "enable" ? "Plan: add owned file-claim PRE, POST and POST failure handlers for this profile." : "Plan: remove the exact owned file-claim PRE, POST and POST failure handlers for this profile.");
            foreach (var plan in preview.Plans) output.WriteLine((plan.IsUnchanged ? "Unchanged: " : "Would update: ") + plan.Path);
            if (args[1] == "preview") return 0;
            if (action == "promotion") NativeSettingsTransaction.ApplyPromotion(folder, profile, dotnet, adapter, evidence, preview, clock);
            else NativeSettingsTransaction.ApplyFileClaims(folder, profile, dotnet, adapter, provider, choice, preview);
            output.WriteLine("The explicitly selected owned settings plan was applied. Client acceptance remains unverified.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or ClientSettingsException or System.Security.SecurityException)
        { error.WriteLine("The native settings operation could not be completed safely. Inspect its private recovery journal if one exists."); return 1; }
    }
}
