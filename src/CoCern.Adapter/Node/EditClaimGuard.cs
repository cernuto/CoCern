namespace CoCern.Adapter.Node;

internal sealed record EditClaimAcquireResult(string Outcome, long? Generation = null, bool PreOwned = false, string? Holder = null);
internal interface IEditClaimTransport
{
    Task<EditClaimAcquireResult> AcquireAsync(string resourceId, string requestId);
    // Must confirm THIS holder/generation and an adequate current lease. A cached
    // acquire reply or a generic held flag is not an implementation of this seam.
    Task<bool> ConfirmAsync(string resourceId, long generation);
    Task<bool?> ReleaseAsync(string resourceId, long generation, string requestId);
}

// Abstain preserves the provider's permission gate; it is never an allow directive.
internal sealed record EditClaimDecision(bool Abstain, string Reason, string? Holder = null);

// Internal canonical-target consumer only. Native auth, activity evidence and
// post-permission revalidation are external prerequisites, not implemented hooks.
internal sealed class EditClaimGuard(EditClaimLedger ledger, IEditClaimTransport transport)
{
    public async Task<EditClaimDecision> AcquireAsync(string intentId, IReadOnlyList<string> resources, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        if (!Bounded(intentId, 128) || resources.Count is < 1 or > 128 || resources.Any(x => !Bounded(x, 512))) return Denied("invalid-intent");
        var ordered = resources.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var intent = ledger.Read(intentId);
        if (intent is null)
        {
            intent = new(intentId, ordered.Select(x => new EditClaimLease(x, Guid.NewGuid().ToString("N"), null, true, "acquire-pending")).ToArray(), "acquire-pending");
            if (!ledger.Save(intent)) return Denied("ledger-unavailable");
        }
        else if (!intent.Targets.Select(x => x.ResourceId).SequenceEqual(ordered, StringComparer.Ordinal)) return Denied("intent-conflict");
        if (intent.Outcome is "conflict" or "release-unknown" or "compensating")
        {
            await Compensate(intent, token);
            return Denied("prior-conflict");
        }
        for (var index = 0; index < intent.Targets.Count; index++)
        {
            var lease = intent.Targets[index];
            try
            {
                token.ThrowIfCancellationRequested();
                if (lease.Outcome is "released" or "release-pending" or "release-unknown" or "busy") return Denied("unsettled-intent");
                if (lease.Generation is null)
                {
                    // The request is already durable. A timeout/lost reply retains
                    // that same identity for replay; it never grants edit permission.
                    var acquired = await transport.AcquireAsync(lease.ResourceId, lease.RequestId).WaitAsync(token);
                    if (acquired.Outcome == "busy")
                    {
                        intent = Replace(intent, index, lease with { Outcome = "busy" }, "conflict");
                        if (ledger.Save(intent)) await Compensate(intent, token);
                        return new(false, "busy", acquired.Holder);
                    }
                    if (acquired.Outcome != "held" || acquired.Generation is not > 0)
                    {
                        intent = Replace(intent, index, lease with { Outcome = "unknown" }, "unknown");
                        ledger.Save(intent);
                        return Denied("acquire-unknown");
                    }
                    lease = lease with { Generation = acquired.Generation, NewlyAcquired = !acquired.PreOwned, Outcome = "grant-unconfirmed" };
                    intent = Replace(intent, index, lease, "acquire-pending");
                    if (!ledger.Save(intent)) return Denied("ledger-unavailable");
                }
                if (!await transport.ConfirmAsync(lease.ResourceId, lease.Generation!.Value).WaitAsync(token))
                {
                    intent = Replace(intent, index, lease with { Outcome = "ownership-unknown" }, "unknown");
                    ledger.Save(intent);
                    return Denied("ownership-unknown");
                }
                intent = Replace(intent, index, lease with { Outcome = "held" }, "acquire-pending");
                if (!ledger.Save(intent)) return Denied("ledger-unavailable");
            }
            catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or TimeoutException)
            {
                intent = Replace(intent, index, intent.Targets[index] with { Outcome = "unknown" }, "unknown");
                ledger.Save(intent);
                return Denied("acquire-unknown");
            }
        }
        // Reconfirm the complete set together, rather than trusting a first grant
        // whose lease may have expired while later targets were being acquired.
        try
        {
            foreach (var lease in intent.Targets)
                if (!await transport.ConfirmAsync(lease.ResourceId, lease.Generation!.Value).WaitAsync(token))
                { ledger.Save(intent with { Outcome = "unknown" }); return Denied("ownership-unknown"); }
        }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or TimeoutException)
        { ledger.Save(intent with { Outcome = "unknown" }); return Denied("ownership-unknown"); }
        return ledger.Save(intent with { Outcome = "held" }) ? new(true, "abstain") : Denied("ledger-unavailable");
    }

    private async Task Compensate(EditClaimIntent intent, CancellationToken token)
    {
        var unknown = false;
        for (var index = 0; index < intent.Targets.Count; index++)
        {
            var lease = intent.Targets[index];
            if (!lease.NewlyAcquired || lease.Generation is null || lease.Outcome is not ("held" or "release-pending" or "release-unknown")) continue;
            intent = Replace(intent, index, lease with { Outcome = "release-pending" }, "compensating");
            if (!ledger.Save(intent)) return;
            bool? released;
            try
            {
                // Deterministic compensation ID derives from the persisted acquire
                // identity, so uncertain releases replay the exact same mutation.
                released = await transport.ReleaseAsync(lease.ResourceId, lease.Generation.Value, lease.RequestId + "-release").WaitAsync(token);
            }
            catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or TimeoutException) { released = null; }
            unknown |= released != true;
            intent = Replace(intent, index, lease with { Outcome = released == true ? "released" : "release-unknown" }, unknown ? "release-unknown" : "compensating");
            if (!ledger.Save(intent)) return;
        }
        ledger.Save(intent with { Outcome = unknown ? "release-unknown" : "conflict" });
    }
    private static EditClaimIntent Replace(EditClaimIntent intent, int index, EditClaimLease lease, string outcome)
    {
        var targets = intent.Targets.ToArray(); targets[index] = lease;
        return intent with { Targets = targets, Outcome = outcome };
    }
    private static bool Bounded(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum && !value.Any(char.IsControl) && !value.Any(char.IsSurrogate);
    private static EditClaimDecision Denied(string reason) => new(false, reason);
}
