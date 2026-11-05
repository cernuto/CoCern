using CoCern.Adapter.Node;
using Xunit;

namespace CoCern.Tests;

public sealed class EditClaimRetirementTests
{
    [Fact]
    public async Task Retired_replay_cannot_allocate_fresh_requests_after_restart_and_unsettled_intents_stay()
    {
        if (!OperatingSystem.IsWindows()) return;
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cocern-ledger-retirement-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(parent);
        try
        {
            var directory = Path.Combine(parent, "private");
            var ledger = new EditClaimLedger(directory);
            var held = new EditClaimIntent("old-edit", [new("file.cs", "old-acquire", 7, true, "held")], "held");
            Assert.True(ledger.Save(held));
            Assert.False(ledger.RetireReleased(held.Id));
            Assert.True(ledger.Save(held with { Outcome = "released", Targets = [held.Targets[0] with { Outcome = "released" }] }));
            Assert.True(ledger.RetireReleased(held.Id));
            var unknown = new EditClaimIntent("unknown-edit", [new("other.cs", "uncertain-acquire", null, true, "unknown")], "unknown");
            Assert.True(ledger.Save(unknown));
            Assert.False(ledger.RetireReleased(unknown.Id));

            var reopened = new EditClaimLedger(directory);
            Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<EditClaimIntent>>(reopened.Snapshot()));
            var transport = new NeverCalled();
            Assert.False((await new EditClaimGuard(reopened, transport).AcquireAsync(held.Id, ["file.cs"])).Abstain);
            Assert.Equal(0, transport.Calls);
            Assert.False(reopened.Save(held with { Targets = [held.Targets[0] with { RequestId = "new-acquire" }] }));
            Assert.Equal("uncertain-acquire", reopened.Read(unknown.Id)!.Targets[0].RequestId);
            File.Delete(Path.Combine(directory, "edit-claims.json"));
            Assert.Null(reopened.Snapshot());
            Assert.False(reopened.Save(held)); // Losing the file cannot silently reset durable replay history.
            Directory.Move(directory, directory + "-lost");
            Assert.Null(reopened.Snapshot());
            Assert.False(reopened.Save(held)); // The parent initialization fence also survives losing the whole ledger directory.
            Assert.False((await new EditClaimGuard(reopened, transport).AcquireAsync(held.Id, ["file.cs"])).Abstain);
            Assert.Equal(0, transport.Calls);
        }
        finally
        {
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!parent.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe fixture cleanup path.");
            Directory.Delete(parent, recursive: true);
        }
    }

    private sealed class NeverCalled : IEditClaimTransport
    {
        public int Calls;
        public Task<EditClaimAcquireResult> AcquireAsync(string resourceId, string requestId) { Calls++; throw new InvalidOperationException(); }
        public Task<bool> ConfirmAsync(string resourceId, long generation) { Calls++; throw new InvalidOperationException(); }
        public Task<bool?> ReleaseAsync(string resourceId, long generation, string requestId) { Calls++; throw new InvalidOperationException(); }
    }
}
