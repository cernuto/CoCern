using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Host;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// Real junctions and symlinks, created in a temp folder and removed afterwards. Git for Windows 2.55 already prints the physical path for a junction,
/// a wrong case and a linked core.worktree, so the spellings through real Git prove the result rather than the old gap. The stand-in for Git, the
/// core.worktree redirect and the network cases are the ones that show what <see cref="NativeCheckout"/> itself now enforces.
/// </summary>
public sealed class NativeCheckoutPhysicalRootTests
{
    private const string WindowsOnly = "Junctions, drive letters and UNC paths exist only on Windows.";
    private const string Chat = "11111111-2222-4333-8444-555555555555";

    [SkippableFact]
    public void A_cwd_through_a_junction_has_the_targets_root()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var nested = links.Folder("real/checkout/nested");
        var link = links.Junction("via", repo);
        var expected = Links.TopLevel(repo);
        Assert.Equal(expected, NativeCheckout.Inspect(link).Root);
        Assert.Equal(expected, NativeCheckout.Inspect(Path.Combine(link, Path.GetFileName(nested))).Root);
    }

    [SkippableFact]
    public void A_junction_in_the_middle_of_the_path_is_resolved()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        links.Folder("real/checkout/nested");
        var middle = links.Junction("middle", links.Folder("real"));
        var expected = Links.TopLevel(repo);
        Assert.Equal(expected, NativeCheckout.Inspect(Path.Combine(middle, "checkout")).Root);
        Assert.Equal(expected, NativeCheckout.Inspect(Path.Combine(middle, "checkout", "nested")).Root);
        Assert.Equal(expected, NativeCheckout.Physical(Path.Combine(middle, "checkout")));
    }

    [SkippableFact]
    public void Every_spelling_of_one_folder_is_one_identity()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var link = links.Junction("via", repo);
        var expected = Links.TopLevel(repo);
        string[] spellings =
        [
            repo, link, repo.ToUpperInvariant(), link.ToUpperInvariant(), repo + Path.DirectorySeparatorChar, char.ToLowerInvariant(repo[0]) + repo[1..], @"\\?\" + repo, @"\\?\" + link
        ];
        var identities = spellings.Select(spelling => NativeCheckout.Inspect(spelling)).ToArray();
        Assert.All(identities, identity => Assert.Equal(expected, identity.Root));
        Assert.DoesNotContain(identities, identity => identity.Root.StartsWith(@"\\", StringComparison.Ordinal));
        Assert.Single(identities.Select(identity => identity.Preflight(true).GetRawText()).Distinct());
    }

    [SkippableFact]
    public void A_git_that_reports_an_unresolved_path_still_yields_the_physical_root()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var link = links.Junction("via", repo);
        var expected = Links.TopLevel(repo);
        Assert.Equal(expected, NativeCheckout.Inspect(repo, _ => link).Root);
        Assert.Equal(expected, NativeCheckout.Inspect(link, _ => link).Root);
        Assert.Equal(expected, NativeCheckout.Inspect(repo, _ => repo.ToUpperInvariant()).Root);
        Assert.ThrowsAny<IOException>(() => NativeCheckout.Inspect(repo, _ => null));
    }

    [SkippableFact]
    public void A_junction_pointing_outside_does_not_verify_inside_the_root()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var inner = links.Folder("real/checkout/inner");
        var outside = links.Folder("outside");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside");
        File.WriteAllText(Path.Combine(inner, "own.txt"), "inside");
        var escape = links.Junction("real/checkout/escape", outside);
        var internalLink = links.Junction("real/checkout/shortcut", inner);
        var intoRoot = links.Junction("from-outside", inner);
        var checkout = NativeCheckout.Inspect(repo);

        Assert.False(checkout.PhysicallyContains(escape));
        Assert.False(checkout.PhysicallyContains(Path.Combine(escape, "secret.txt")));
        Assert.False(checkout.PhysicallyContains(outside));
        Assert.True(checkout.PhysicallyContains(repo));
        Assert.True(checkout.PhysicallyContains(inner));
        Assert.True(checkout.PhysicallyContains(Path.Combine(inner, "own.txt")));
        Assert.True(checkout.PhysicallyContains(Path.Combine(internalLink, "own.txt")));
        Assert.True(checkout.PhysicallyContains(intoRoot)); // Physically inside, whatever its spelling.
        Assert.False(checkout.PhysicallyContains(Path.Combine(repo, "not-there")));

        // The event's directory is what the claim is judged by: one reached through the escape cannot claim this root.
        Assert.ThrowsAny<IOException>(() => NativeCheckout.Inspect(escape, _ => repo));
        var other = links.Repository("other");
        var toOther = links.Junction("real/checkout/to-other", other);
        Assert.Equal(Links.TopLevel(other), NativeCheckout.Inspect(toOther).Root);
    }

    [SkippableFact]
    public void A_git_worktree_setting_through_a_junction_cannot_claim_an_outside_root()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var outside = links.Junction("outside-link", links.Folder("outside"));
        Links.Git(repo, "config", "--local", "core.worktree", outside);
        Assert.ThrowsAny<IOException>(() => NativeCheckout.Inspect(repo));
    }

    [Fact]
    public void A_git_worktree_setting_cannot_claim_a_root_the_event_is_not_in()
    {
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        Links.Git(repo, "config", "--local", "core.worktree", links.Folder("outside"));
        Assert.ThrowsAny<IOException>(() => NativeCheckout.Inspect(repo));
    }

    [Fact]
    public void A_sibling_sharing_the_roots_name_prefix_is_not_inside_it()
    {
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var sibling = links.Folder("real/checkout-sibling");
        Assert.False(NativeCheckout.Inspect(repo).PhysicallyContains(sibling));
        Assert.ThrowsAny<IOException>(() => NativeCheckout.Inspect(sibling, _ => repo));
    }

    [SkippableFact]
    public void A_dangling_junction_fails_closed()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var gone = links.Folder("gone");
        var dangling = links.Junction("dangling", gone);
        Directory.Delete(gone);
        var checkout = NativeCheckout.Inspect(repo);

        Assert.ThrowsAny<IOException>(() => NativeCheckout.Inspect(dangling));
        Assert.ThrowsAny<IOException>(() => NativeCheckout.Physical(dangling));
        Assert.ThrowsAny<IOException>(() => NativeCheckout.Physical(Path.Combine(dangling, "below")));
        Assert.ThrowsAny<IOException>(() => NativeCheckout.Inspect(repo, _ => dangling)); // A lexical fallback would have answered here.
        Assert.False(checkout.PhysicallyContains(dangling));
    }

    [Fact]
    public void A_nonexistent_relative_or_file_path_fails_closed()
    {
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var file = Path.Combine(repo, "file.txt");
        File.WriteAllText(file, "not a directory");
        var missing = Path.Combine(links.Root, "never-there");

        Assert.ThrowsAny<IOException>(() => NativeCheckout.Inspect(missing));
        Assert.ThrowsAny<IOException>(() => NativeCheckout.Physical(missing));
        Assert.ThrowsAny<IOException>(() => NativeCheckout.Inspect(repo, _ => missing));
        Assert.ThrowsAny<IOException>(() => NativeCheckout.Inspect("relative/checkout"));
        Assert.ThrowsAny<IOException>(() => NativeCheckout.Physical(file));
        Assert.ThrowsAny<IOException>(() => NativeCheckout.Inspect(links.Folder("not-a-checkout"), _ => null));
    }

    [Fact]
    public void A_plain_path_keeps_its_meaning()
    {
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var nested = links.Folder("real/checkout/nested/deeper");
        var expected = Links.TopLevel(repo);
        var checkout = NativeCheckout.Inspect(repo);
        Assert.Equal(expected, checkout.Root);
        Assert.Equal(expected, NativeCheckout.Inspect(nested).Root);
        Assert.Equal(NativeCheckout.Hash(expected), JsonDocument.Parse(checkout.Preflight(true).GetRawText()).RootElement.GetProperty("nativePreflight").GetProperty("checkout").GetString());
        Assert.Equal(Path.Combine(expected, ".git"), checkout.Common);
        Assert.Equal(checkout.Root, NativeCheckout.Physical(checkout.Root)); // Resolving twice changes nothing.
    }

    [SkippableFact]
    public void Directory_symlinks_resolve_like_junctions()
    {
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        links.Folder("real/checkout/nested");
        var direct = links.Symlink("via-link", repo);
        Skip.If(direct is null, "This account cannot create directory symlinks. Junctions need no privilege and are covered separately.");
        var middle = links.Symlink("middle-link", links.Folder("real"));
        var dangling = links.Symlink("dangling-link", Path.Combine(links.Root, "never-there"));
        var expected = Links.TopLevel(repo);

        Assert.Equal(expected, NativeCheckout.Inspect(direct!).Root);
        Assert.Equal(expected, NativeCheckout.Inspect(Path.Combine(middle!, "checkout", "nested")).Root);
        Assert.Equal(expected, NativeCheckout.Inspect(repo, _ => direct).Root);
        Assert.ThrowsAny<IOException>(() => NativeCheckout.Physical(dangling!));
    }

    [SkippableFact]
    public void A_network_path_fails_closed_before_it_is_opened()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        // These hosts do not exist. Each spelling is refused from its text alone, so no test here waits on the network.
        string[] spellings = [@"\\server.invalid\share\checkout", "//server.invalid/share/checkout", @"\\?\UNC\server.invalid\share\checkout", @"\\.\" + repo, @"\\?\Volume{00000000-0000-0000-0000-000000000000}\checkout"];
        foreach (var spelling in spellings)
        {
            Assert.ThrowsAny<IOException>(() => NativeCheckout.Physical(spelling));
            Assert.ThrowsAny<IOException>(() => NativeCheckout.Inspect(spelling));
        }
        Assert.False(NativeCheckout.Inspect(repo).PhysicallyContains(spellings[0]));
    }

    [SkippableFact]
    public void A_local_link_onto_a_network_path_fails_closed_like_a_mapped_drive()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var share = $@"\\localhost\{repo[0]}$\{repo[3..]}";
        Skip.IfNot(Directory.Exists(share), "This account cannot reach the loopback administrative share.");
        Assert.ThrowsAny<IOException>(() => NativeCheckout.Physical(share));
        var link = links.Symlink("share-link", share);
        Skip.If(link is null, "This account cannot create a directory symlink onto a share.");
        Skip.IfNot(Directory.Exists(link!), "Local-to-remote symlink evaluation is off on this PC.");
        Assert.ThrowsAny<IOException>(() => NativeCheckout.Physical(link!));
        Assert.ThrowsAny<IOException>(() => NativeCheckout.Inspect(link!));
        Assert.ThrowsAny<IOException>(() => NativeCheckout.Inspect(repo, _ => link));
    }

    // Maps a free drive letter for the duration of this test and removes it in the finally block.
    [SkippableFact]
    public void A_subst_drive_resolves_to_the_local_folder_behind_it()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var used = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
        var letter = "QRSTUVWXYZ".FirstOrDefault(candidate => !used.Contains(candidate));
        Skip.If(letter == default, "No free drive letter for a subst mapping.");
        var mapped = letter + ":";
        Skip.IfNot(Links.Run("subst", mapped, repo).Code == 0, "subst is unavailable on this PC.");
        try
        {
            Assert.Equal(Links.TopLevel(repo), NativeCheckout.Inspect(mapped + @"\").Root);
            Assert.Equal(Links.TopLevel(repo), NativeCheckout.Physical(mapped + @"\"));
        }
        finally { Links.Run("subst", mapped, "/D"); }
    }

    [Fact]
    public async Task A_binding_made_before_roots_were_physical_keeps_its_key()
    {
        using var fixture = new NativeJoinBindingTests.ScopeFixture();
        string key, workspace;
        await using (var existing = await fixture.Acquire()) { key = existing.BindingKey; workspace = existing.Workspace; }
        var root = NativeCheckout.Inspect(fixture.Workspace).Root;
        Assert.Equal(workspace, root);
        await using var same = await fixture.Acquire(root);
        Assert.Equal(key, same.BindingKey);
    }

    [SkippableFact]
    public async Task A_junction_spelling_reaches_the_binding_of_its_physical_folder()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var fixture = new NativeJoinBindingTests.ScopeFixture();
        using var links = new Links();
        string key, workspace;
        await using (var existing = await fixture.Acquire()) { key = existing.BindingKey; workspace = existing.Workspace; }
        var root = NativeCheckout.Inspect(links.Junction("via", fixture.Workspace)).Root;
        Assert.Equal(workspace, root);
        await using var same = await fixture.Acquire(root);
        Assert.Equal(key, same.BindingKey);
    }

    [SkippableFact]
    public async Task The_hub_still_matches_a_checkout_opened_through_a_junction()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var link = links.Junction("via", repo);
        var transport = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        transport.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        var settings = new DocCacheSettings(true, 0, 2, 32, [new DocRepository("primary", Links.TopLevel(repo), "main", "origin", [], [], null)], 1024);
        var registry = new DocRepositoryRegistry(settings, new GitCli(), NullLogger<DocRepositoryRegistry>.Instance, transport);
        var caller = new RelayCaller("test", false, ["primary"]);
        var checkout = NativeCheckout.Inspect(link);
        Assert.Equal("primary", await registry.ResolveNativeAsync(caller, NativeCheckout.Hash(checkout.Root), null, null));
        Assert.Equal("primary", await registry.ResolveNativeAsync(caller, null, NativeCheckout.Hash(checkout.Common!), null));
    }

    [SkippableFact]
    public async Task A_pending_intent_under_an_old_spelling_is_served_and_removed()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var fixture = new NativeJoinBindingTests.ScopeFixture();
        using var links = new Links();
        var stale = OldSpellingIntent(fixture, links.Junction("via", fixture.Workspace));
        var opens = 0;
        using var http = new HttpClient(new Respond(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (body.RootElement.TryGetProperty("nativePreflight", out _)) return Wire(new { repositoryId = "repo" });
            if (body.RootElement.TryGetProperty("nativeJoin", out _)) { Interlocked.Increment(ref opens); return Wire(new { sessionId = "s-recovered", sessionToken = "recovered-token" }); }
            return Wire(new { });
        }));
        await ResumeUntilGone(fixture, http, stale);

        Assert.Equal(1, Volatile.Read(ref opens));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(stale)!, "*.json"));
        Assert.Equal(Chat, Assert.Single(NativeJoinBinding.DispatchSnapshot(fixture.Folder)).Chat!.ThreadId);
        await using var binding = await NativeJoinBinding.AcquireAsync(fixture.Folder, "codex", Chat, Environment.MachineName, "repo", fixture.Workspace, default, existingOnly: true);
        Assert.Equal(NativeCheckout.Physical(fixture.Workspace), binding.Workspace);
    }

    [SkippableFact]
    public async Task A_refused_intent_under_an_old_spelling_does_not_stay_to_retry_forever()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var fixture = new NativeJoinBindingTests.ScopeFixture();
        using var links = new Links();
        var stale = OldSpellingIntent(fixture, links.Junction("via", fixture.Workspace));
        using var http = new HttpClient(new Respond((_, _) => Task.FromResult(Wire(new { }, false, RelayErrorCodes.SessionDenied))));
        await ResumeUntilGone(fixture, http, stale);
        Assert.Empty(NativeJoinBinding.DispatchSnapshot(fixture.Folder));
    }

    // The same fields, in the same order, as NativeJoin's private intent record, so the file name is the one an older adapter wrote.
    private static string OldSpellingIntent(NativeJoinBindingTests.ScopeFixture fixture, string spelling)
    {
        var scope = LocalWakeConnection.NativeCredentialIdentity(fixture.Folder)!;
        var directory = Directory.CreateDirectory(Path.Combine(fixture.Folder, "native-pending-" + scope)).FullName;
        var content = JsonSerializer.Serialize(new { Provider = "codex", NativeId = Chat, Cwd = spelling, Repository = (string?)null });
        var path = Path.Combine(directory, NativeCheckout.Hash(content) + ".json");
        NodeState.WritePrivately(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    private static async Task ResumeUntilGone(NativeJoinBindingTests.ScopeFixture fixture, HttpClient http, string pending)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var runtime = new NativeJoin(fixture.Folder, fixture.Options, new RelayForwarder(http, fixture.Options)) { ResumeInterval = TimeSpan.FromMilliseconds(25) };
        var resumed = runtime.ResumeAsync(stop.Token);
        while (File.Exists(pending)) await Task.Delay(25, stop.Token);
        stop.Cancel();
        await resumed;
    }

    private static HttpResponseMessage Wire(object data, bool ok = true, string code = "Ok") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { ok, code, dataJson = JsonSerializer.Serialize(data), version = 1 }), Encoding.UTF8, "application/json")
    };

    private sealed class Respond(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    /// <summary>A temp folder that holds real links. Its links go first, so that deleting it can never walk through one.</summary>
    private sealed class Links : IDisposable
    {
        private readonly List<string> created = [];
        internal string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cocern-physical-root-" + Guid.NewGuid().ToString("N"))).FullName;

        internal string Folder(string relative) => Directory.CreateDirectory(Path.Combine(Root, relative)).FullName;

        internal string Repository(string relative)
        {
            var path = Folder(relative);
            Git(path, "init", "-q");
            return path;
        }

        internal string Junction(string relative, string target)
        {
            var link = Path.GetFullPath(Path.Combine(Root, relative));
            var result = Run("cmd.exe", "/c", "mklink", "/J", link, target);
            Assert.True(result.Code == 0, "The fixture junction could not be created: " + result.Output);
            created.Add(link);
            return link;
        }

        /// <summary>Null when this account may not create one.</summary>
        internal string? Symlink(string relative, string target)
        {
            var link = Path.GetFullPath(Path.Combine(Root, relative));
            try { Directory.CreateSymbolicLink(link, target); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
            created.Add(link);
            return link;
        }

        /// <summary>What Git itself says, independently of the code under test: Git for Windows prints the final path.</summary>
        internal static string TopLevel(string directory)
        {
            var result = Git(directory, "rev-parse", "--show-toplevel");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(result.TrimEnd('\r', '\n')));
        }

        internal static string Git(string directory, params string[] arguments)
        {
            var result = Run(ExecutableSearch.Find("git") ?? throw new InvalidOperationException("Installed Git required"), ["-C", directory, .. arguments]);
            Assert.True(result.Code == 0, "Fixture Git failed: " + result.Output);
            return result.Output;
        }

        internal static (int Code, string Output) Run(string file, params string[] arguments)
        {
            var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(name);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000)) { process.Kill(entireProcessTree: true); throw new InvalidOperationException("Fixture command timeout"); }
            Task.WhenAll(output, error).GetAwaiter().GetResult();
            return (process.ExitCode, output.Result + error.Result);
        }

        public void Dispose()
        {
            foreach (var link in created)
            {
                try { Directory.Delete(link); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            try
            {
                // Git stores loose objects read-only on Windows. Hidden .git folders count, and links are skipped.
                foreach (var file in Directory.EnumerateFiles(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
