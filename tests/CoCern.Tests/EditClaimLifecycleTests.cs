using System.Text.Json;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Contracts;
using Xunit;

namespace CoCern.Tests;

public sealed class EditClaimLifecycleTests
{
    private const string File1 = "file.cs";

    [Fact]
    public async Task A_claim_renews_only_for_this_chats_own_change_still_dirty_in_the_file()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.Hold("edit-1", "acquire-1", newlyAcquired: true);
        Assert.True(await fixture.Lifecycle.PrepareAsync("edit-1", [File1], default));

        // Activity on a file dirty from elsewhere, with no POST for this claim, renews nothing.
        fixture.Physical.Dirt = EditClaimDirt.Dirty;
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(61);
        await fixture.Lifecycle.SweepAsync(default);
        Assert.Empty(fixture.Transport.Renewals);

        // A successful POST that changed nothing is no evidence.
        await fixture.Lifecycle.ObserveAsync("edit-1", true, null, default);
        await fixture.Lifecycle.SweepAsync(default);
        Assert.Empty(fixture.Transport.Renewals);

        // A real own change on a dirty file does renew, and a replayed POST adds nothing.
        fixture.Hold("edit-2", "acquire-2", newlyAcquired: false);
        Assert.True(await fixture.Lifecycle.PrepareAsync("edit-2", [File1], default));
        fixture.Physical.Content = "own change";
        await fixture.Lifecycle.ObserveAsync("edit-2", true, null, default);
        await fixture.Lifecycle.ObserveAsync("edit-2", true, null, default);
        await fixture.Lifecycle.SweepAsync(default);
        Assert.Equal(new[] { "acquire-1-renew-0" }, fixture.Transport.Renewals);
        Assert.Empty(fixture.Transport.RetiredRenewals);

        // The next renewal gives back the fence of the one it moved past, and only that one.
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(61);
        await fixture.Lifecycle.SweepAsync(default);
        Assert.Equal(new[] { "acquire-1-renew-0", "acquire-1-renew-1" }, fixture.Transport.Renewals);
        Assert.Equal(new[] { "acquire-1-renew-0" }, fixture.Transport.RetiredRenewals);

        // A retirement that fails is not forgotten: renewals go on, and once it works every one still owed goes.
        fixture.Transport.RetireFails = true;
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(61);
        await fixture.Lifecycle.SweepAsync(default);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(61);
        await fixture.Lifecycle.SweepAsync(default);
        Assert.Equal(4, fixture.Transport.Renewals.Count);
        Assert.Single(fixture.Transport.RetiredRenewals);
        fixture.Transport.RetireFails = false;
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(61);
        await fixture.Lifecycle.SweepAsync(default);
        Assert.Equal(5, fixture.Transport.Renewals.Count);
        Assert.Equal(new[] { "acquire-1-renew-0", "acquire-1-renew-1", "acquire-1-renew-2", "acquire-1-renew-3" }, fixture.Transport.RetiredRenewals);

        // Someone else's later content is not this chat's change, even though Git still calls the file dirty.
        fixture.Physical.Content = "someone else's change";
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(61);
        await fixture.Lifecycle.SweepAsync(default);
        Assert.Equal(5, fixture.Transport.Renewals.Count);

        // A settled claim keeps its receipts until its last answered renewal's fence is confirmed gone, and doesn't release twice.
        fixture.Physical.Dirt = EditClaimDirt.Clean;
        fixture.Transport.ReleaseReply = new AdapterReply(true, "Ok", JsonSerializer.SerializeToElement(new { generation = 7L, released = true }), 7);
        fixture.Transport.RetireFails = true;
        await fixture.Lifecycle.SweepAsync(default);
        Assert.Single(fixture.Transport.Releases);
        Assert.Equal(4, fixture.Transport.RetiredRenewals.Count);
        Assert.Equal("held", fixture.Ledger.Read("edit-1")!.Outcome);
        fixture.Transport.RetireFails = false;
        await fixture.Lifecycle.SweepAsync(default);
        Assert.Single(fixture.Transport.Releases);
        Assert.Equal("acquire-1-renew-4", fixture.Transport.RetiredRenewals[^1]);
        Assert.NotEqual("held", fixture.Ledger.Read("edit-1")?.Outcome);
    }

    [Fact]
    public async Task A_file_with_uncommitted_changes_or_unproven_state_is_not_released()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.Hold("edit-1", "acquire-1", newlyAcquired: true);
        Assert.True(await fixture.Lifecycle.PrepareAsync("edit-1", [File1], default));
        fixture.Physical.Content = "own change";
        fixture.Physical.Dirt = EditClaimDirt.Dirty;
        await fixture.Lifecycle.ObserveAsync("edit-1", true, null, default);

        await fixture.Lifecycle.SweepAsync(default);
        fixture.Physical.Dirt = EditClaimDirt.Unknown;
        await fixture.Lifecycle.SweepAsync(default);
        Assert.Empty(fixture.Transport.Releases);

        // Clean, but a duplicate intent's edit is still open on the same claim.
        fixture.Physical.Dirt = EditClaimDirt.Clean;
        fixture.Hold("edit-2", "acquire-2", newlyAcquired: false);
        Assert.True(await fixture.Lifecycle.PrepareAsync("edit-2", [File1], default));
        await fixture.Lifecycle.SweepAsync(default);
        Assert.Empty(fixture.Transport.Releases);

        // A lost reply stays uncertain: the receipts stay, and the retry is the same invocation only because Git still says clean.
        await fixture.Lifecycle.ObserveAsync("edit-2", false, null, default);
        fixture.Transport.ReleaseReply = new AdapterReply(false, "hub_timeout");
        await fixture.Lifecycle.SweepAsync(default);
        Assert.Equal(new[] { "acquire-1-clean-release" }, fixture.Transport.Releases);
        Assert.Equal("held", fixture.Ledger.Read("edit-1")!.Outcome);

        fixture.Physical.Dirt = EditClaimDirt.Dirty;
        await fixture.Lifecycle.SweepAsync(default);
        Assert.Single(fixture.Transport.Releases);
        Assert.Empty(fixture.Transport.Renewals);

        fixture.Physical.Dirt = EditClaimDirt.Clean;
        fixture.Transport.ReleaseReply = new AdapterReply(true, "Ok", JsonSerializer.SerializeToElement(new { generation = 7L, released = true }), 7);
        await fixture.Lifecycle.SweepAsync(default);
        Assert.Equal(new[] { "acquire-1-clean-release", "acquire-1-clean-release" }, fixture.Transport.Releases);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<EditClaimIntent>>(fixture.Ledger.Snapshot()));
    }

    [Fact]
    public async Task A_release_never_lands_on_an_edit_its_PRE_has_just_confirmed()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.Hold("edit-1", "acquire-1", newlyAcquired: true);
        Assert.True(await fixture.Lifecycle.PrepareAsync("edit-1", [File1], default));
        await fixture.Lifecycle.ObserveAsync("edit-1", false, null, default);

        // A PRE finishes after the sweep has loaded its state and looked at Git: the decision inside the gate must still see its attempt.
        fixture.Physical.BeforeStatus = () =>
        {
            fixture.Physical.BeforeStatus = null;
            fixture.Hold("edit-2", "acquire-2", newlyAcquired: false);
            Assert.True(fixture.Lifecycle.PrepareAsync("edit-2", [File1], default).GetAwaiter().GetResult());
        };
        await fixture.Lifecycle.SweepAsync(default);
        Assert.Empty(fixture.Transport.Releases);

        // With the gate held elsewhere the sweep skips, as uncertainty.
        await fixture.Lifecycle.ObserveAsync("edit-2", false, null, default);
        await using (var held = await fixture.Store.DecisionGateAsync(TimeSpan.FromSeconds(5), default))
        {
            Assert.NotNull(held);
            await fixture.Lifecycle.SweepAsync(default);
            Assert.Empty(fixture.Transport.Releases);
        }

        // A release with no answer may still reach the hub, so a PRE on that generation refuses, and neither time nor a read still showing this holder ends that.
        await fixture.Lifecycle.SweepAsync(default);
        Assert.Single(fixture.Transport.Releases);
        fixture.Hold("edit-3", "acquire-3", newlyAcquired: false);
        Assert.False(await fixture.Lifecycle.PrepareAsync("edit-3", [File1], default));

        fixture.Clock.Now = fixture.Clock.Now.AddHours(1);
        fixture.Transport.ReadReply = new AdapterReply(true, "Ok", JsonSerializer.SerializeToElement(new { held = true, generation = 7L, leaseUntil = fixture.Clock.Now.AddSeconds(120).ToString("O") }), 7);
        await fixture.Lifecycle.SweepAsync(default);
        fixture.Hold("edit-4", "acquire-4", newlyAcquired: false);
        Assert.False(await fixture.Lifecycle.PrepareAsync("edit-4", [File1], default));

        // Held false proves the release is over only with a generation and lease that show it, so an answer missing or garbling either changes nothing.
        object[] unproven = [new { held = false }, new { held = false, generation = 7L }, new { held = false, generation = 7L, leaseUntil = "" }, new { held = false, generation = "7", leaseUntil = "2000-01-01T00:00:00+00:00" }];
        foreach (var answer in unproven)
        {
            fixture.Clock.Now = fixture.Clock.Now.AddHours(1);
            fixture.Transport.ReadReply = new AdapterReply(true, "Ok", JsonSerializer.SerializeToElement(answer), 7);
            await fixture.Lifecycle.SweepAsync(default);
            Assert.Equal("release-unknown", (await fixture.Store.ReadAsync(default))!.Claims.Single().ReleaseOutcome);
        }

        fixture.Clock.Now = fixture.Clock.Now.AddHours(1);
        fixture.Transport.ReadReply = new AdapterReply(true, "Ok", JsonSerializer.SerializeToElement(new { held = false, generation = 7L, leaseUntil = "2000-01-01T00:00:00+00:00" }), 7);
        await fixture.Lifecycle.SweepAsync(default);
        // The same sweep ends with its finished claim's row removed, so a row left behind would mean it never settled.
        Assert.Empty((await fixture.Store.ReadAsync(default))!.Claims);
    }

    [Fact]
    public async Task A_blocker_refusal_of_a_replay_says_nothing_about_the_unanswered_send_before_it()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.Hold("edit-1", "acquire-1", newlyAcquired: true);
        Assert.True(await fixture.Lifecycle.PrepareAsync("edit-1", [File1], default));
        await fixture.Lifecycle.ObserveAsync("edit-1", false, null, default);

        await fixture.Lifecycle.SweepAsync(default);
        fixture.Transport.ReleaseReply = new AdapterReply(false, RelayErrorCodes.UnresolvedAction);
        await fixture.Lifecycle.SweepAsync(default);
        Assert.Equal(2, fixture.Transport.Releases.Count);

        fixture.Hold("edit-2", "acquire-2", newlyAcquired: false);
        Assert.False(await fixture.Lifecycle.PrepareAsync("edit-2", [File1], default));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cocern-claim-lifecycle-" + Guid.NewGuid().ToString("N")));
        internal Clock Clock { get; } = new();
        internal Checkout Physical { get; } = new();
        internal Hub Transport { get; }
        internal EditClaimLedger Ledger { get; }
        internal EditClaimLifecycleStore Store { get; }
        internal EditClaimLifecycle Lifecycle { get; }

        internal Fixture()
        {
            Directory.CreateDirectory(parent);
            var directory = Path.Combine(parent, "private");
            Transport = new(Clock);
            Ledger = new(directory);
            Store = new(directory);
            Lifecycle = new(Ledger, Store, Physical, Transport, Clock);
        }

        internal void Hold(string intent, string request, bool newlyAcquired) =>
            Assert.True(Ledger.Save(new(intent, [new(File1, request, 7, newlyAcquired, "held")], "held")));

        public void Dispose()
        {
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!parent.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe fixture cleanup path.");
            Directory.Delete(parent, recursive: true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Checkout : IEditClaimPhysicalState
    {
        internal EditClaimDirt Dirt { get; set; } = EditClaimDirt.Clean;
        internal string Content { get; set; } = "committed";
        public string? Fingerprint(string relativePath) => Content;
        internal Action? BeforeStatus { get; set; }
        public EditClaimDirt Status(string relativePath)
        {
            BeforeStatus?.Invoke();
            return Dirt;
        }
        public string? RelativeKey(string reportedPath) => reportedPath;
    }

    private sealed class Hub(Clock clock) : IEditClaimLifecycleTransport
    {
        internal List<string> Renewals { get; } = [];
        internal List<string> Releases { get; } = [];
        internal List<string> RetiredRenewals { get; } = [];
        internal bool RetireFails { get; set; }
        internal AdapterReply ReleaseReply { get; set; } = new(false, "hub_timeout");
        internal AdapterReply ReadReply { get; set; } = new(false, "hub_timeout");

        public Task<AdapterReply> RenewAsync(string relativePath, long generation, string invocation)
        {
            Renewals.Add(invocation);
            return Task.FromResult(new AdapterReply(true, "Ok", JsonSerializer.SerializeToElement(new { generation, leaseUntil = clock.Now.AddSeconds(120).ToString("O") }), generation));
        }

        public Task<AdapterReply> CleanReleaseAsync(string relativePath, long generation, string invocation)
        {
            Releases.Add(invocation);
            return Task.FromResult(ReleaseReply);
        }

        public Task<AdapterReply> ReadAsync(string relativePath, long generation) => Task.FromResult(ReadReply);

        public Task<bool> RetireRenewalAsync(string relativePath, long generation, string invocation)
        {
            if (!RetireFails) RetiredRenewals.Add(invocation);
            return Task.FromResult(!RetireFails);
        }
    }
}
