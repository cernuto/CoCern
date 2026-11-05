using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoCern.Adapter.Node;

/// <summary>Advisory pre-edit gate. A successful check abstains; the client's own permission decision still applies.</summary>
internal static class NativeEditHook
{
    internal static bool Enabled => string.Equals(Environment.GetEnvironmentVariable("COCERN_FILE_CLAIMS"), "true", StringComparison.OrdinalIgnoreCase);
    internal static bool IsEdit(string? name) => name is "Edit" or "Write" or "MultiEdit" or "apply_patch" or "functions.apply_patch";

    internal static async Task<int> RunAsync(NativeJoin join, NativeHookContext context, JsonElement input, TextWriter error, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (ChatLocators.Text(input, "tool_use_id") is not { Length: > 0 and <= 256 } invocation
                || invocation.Any(char.IsControl) || !input.TryGetProperty("tool_input", out var args)) return await Deny(error);
            IReadOnlyList<string> targets;
            if (ChatLocators.Text(input, "tool_name") is "apply_patch" or "functions.apply_patch")
            {
                var patch = args.ValueKind == JsonValueKind.String ? args.GetString()
                    : args.ValueKind == JsonValueKind.Object ? ChatLocators.Text(args, "patch") ?? ChatLocators.Text(args, "input") : null;
                if (patch is null || !EditClaimPaths.TryCollectPatchPaths(patch, out targets)) return await Deny(error);
            }
            else
            {
                if (args.ValueKind != JsonValueKind.Object || ChatLocators.Text(args, "file_path") is not { Length: > 0 } path) return await Deny(error);
                targets = [path];
            }
            var claims = await join.EditClaimsAsync(context, budget.Token);
            var intent = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(invocation)));
            var decision = await claims.CheckAsync(intent, context.Cwd, targets, budget.Token);
            return decision.Abstain ? 0 : await Deny(error, decision.Holder);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException)
        { return await Deny(error); }
    }

    private static async Task<int> Deny(TextWriter error, string? holder = null)
    {
        await error.WriteLineAsync(holder is { Length: > 0 and <= 256 } && !holder.Any(char.IsControl)
            ? $"CoCern blocked the edit: session {holder} holds a file claim. Coordinate with that session before retrying."
            : "CoCern could not confirm this chat's file claims. The edit is blocked; inspect the claim holder or retry after coordination.");
        return 2;
    }
}
