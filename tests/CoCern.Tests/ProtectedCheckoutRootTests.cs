using System.Diagnostics;
using CoCern.Adapter;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// Protected file-scope eligibility against real junctions, symlinks and drive mappings, and proof that general native inspection did not take on its
/// limits. Everything lives in a temp folder and is removed afterwards. Tests in one class run one at a time, so the drive mappings cannot race.
/// </summary>
public sealed class ProtectedCheckoutRootTests
{
    private const string WindowsOnly = "Junctions, drive letters and UNC paths exist only on Windows.";

    [SkippableFact]
    public void A_junction_to_a_directory_proves_the_targets_root()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        links.Folder("real/checkout/nested");
        var link = links.Junction("via", repo);
        var expected = Links.TopLevel(repo);
        Assert.Equal(expected, ProtectedCheckoutRoot.Prove(link));
        Assert.Equal(expected, ProtectedCheckoutRoot.Prove(Path.Combine(link, "nested")));
        // Independently of what Git says: a Git that left the junction in its report changes nothing.
        Assert.Equal(expected, ProtectedCheckoutRoot.Prove(repo, link));
    }

    [SkippableFact]
    public void A_link_in_the_middle_of_the_path_is_resolved()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        links.Folder("real/checkout/nested");
        var middle = links.Junction("middle", links.Folder("real"));
        var expected = Links.TopLevel(repo);
        Assert.Equal(expected, ProtectedCheckoutRoot.Prove(Path.Combine(middle, "checkout", "nested")));
        Assert.Equal(expected, ProtectedCheckoutRoot.Physical(Path.Combine(middle, "checkout")));
    }

    [SkippableFact]
    public void Every_local_spelling_of_one_folder_proves_one_root()
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
        foreach (var spelling in spellings) Assert.Equal(expected, ProtectedCheckoutRoot.Prove(spelling));
        Assert.Equal(expected, ProtectedCheckoutRoot.Prove(repo, repo.ToUpperInvariant()));
    }

    [SkippableFact]
    public void A_subst_onto_a_local_folder_proves_the_folder_behind_it()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        OnSubst(repo, drive =>
        {
            Assert.Equal(Links.TopLevel(repo), ProtectedCheckoutRoot.Prove(drive + @"\"));
            Assert.Equal(Links.TopLevel(repo), ProtectedCheckoutRoot.Physical(drive + @"\"));
        });
    }

    [SkippableFact]
    public void A_subst_onto_a_share_is_refused()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var share = ReachableShare(repo);
        OnSubst(share, drive =>
        {
            Skip.IfNot(Directory.Exists(drive + @"\"), "This PC cannot reach the share through a subst mapping.");
            AssertNetworkRefusal(drive + @"\");
            Assert.False(ProtectedCheckoutRoot.Contains(repo, drive + @"\"));
        });
    }

    [SkippableFact]
    public void A_mapped_network_drive_is_refused()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var share = ReachableShare(repo);
        OnTemporaryDrive(
            drive => Links.Run("net", "use", drive, share, "/persistent:no").Code == 0,
            drive => Links.Run("net", "use", drive, "/delete", "/y"),
            drive =>
            {
                Skip.IfNot(Directory.Exists(drive + @"\"), "This PC cannot reach the share through a mapped drive.");
                AssertNetworkRefusal(drive + @"\");
            });
    }

    [SkippableFact]
    public void A_unc_or_device_path_is_refused_from_its_text()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        // These hosts do not exist. Each spelling is refused without being opened, so nothing here waits on the network.
        string[] spellings = [@"\\server.invalid\share\checkout", "//server.invalid/share/checkout", @"\\?\UNC\server.invalid\share\checkout", @"\\.\" + repo, @"\\?\Volume{00000000-0000-0000-0000-000000000000}\checkout"];
        foreach (var spelling in spellings)
        {
            AssertNetworkRefusal(spelling);
            Assert.False(ProtectedCheckoutRoot.Contains(repo, spelling));
            Assert.ThrowsAny<IOException>(() => ProtectedCheckoutRoot.Prove(repo, spelling));
        }
    }

    [SkippableFact]
    public void A_reachable_share_and_a_local_link_onto_it_are_refused()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var share = ReachableShare(repo);
        AssertNetworkRefusal(share);
        var link = links.Symlink("share-link", share);
        Skip.If(link is null, "This account cannot create a directory symlink onto a share.");
        AssertRefusal(link!);
        Assert.ThrowsAny<IOException>(() => ProtectedCheckoutRoot.Prove(repo, link));
    }

    [Fact]
    public void A_nonexistent_relative_or_file_path_is_refused()
    {
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var file = Path.Combine(repo, "file.txt");
        File.WriteAllText(file, "not a directory");
        var missing = Path.Combine(links.Root, "never-there");
        AssertRefusal(missing);
        AssertRefusal(file);
        Assert.ThrowsAny<IOException>(() => ProtectedCheckoutRoot.Prove("relative/checkout"));
        Assert.ThrowsAny<IOException>(() => ProtectedCheckoutRoot.Prove(repo, missing));
        Assert.ThrowsAny<IOException>(() => ProtectedCheckoutRoot.Prove(repo, null));
        Assert.ThrowsAny<IOException>(() => ProtectedCheckoutRoot.Prove(links.Folder("not-a-checkout")));
    }

    [SkippableFact]
    public void A_dangling_junction_is_refused()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var gone = links.Folder("gone");
        var dangling = links.Junction("dangling", gone);
        Directory.Delete(gone);
        AssertRefusal(dangling);
        AssertRefusal(Path.Combine(dangling, "below"));
        Assert.ThrowsAny<IOException>(() => ProtectedCheckoutRoot.Prove(repo, dangling));
        Assert.False(ProtectedCheckoutRoot.Contains(repo, dangling));
    }

    [Fact]
    public void A_core_worktree_outside_the_cwd_is_refused()
    {
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        Links.Git(repo, "config", "--local", "core.worktree", links.Folder("outside"));
        Assert.ThrowsAny<IOException>(() => ProtectedCheckoutRoot.Prove(repo));
    }

    [SkippableFact]
    public void A_core_worktree_through_a_junction_outside_the_cwd_is_refused()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        Links.Git(repo, "config", "--local", "core.worktree", links.Junction("outside-link", links.Folder("outside")));
        Assert.ThrowsAny<IOException>(() => ProtectedCheckoutRoot.Prove(repo));
    }

    [SkippableFact]
    public void A_junction_pointing_outside_does_not_count_as_inside_the_root()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var inner = links.Folder("real/checkout/inner");
        var outside = links.Folder("outside");
        File.WriteAllText(Path.Combine(outside, "other.txt"), "outside");
        File.WriteAllText(Path.Combine(inner, "own.txt"), "inside");
        var escape = links.Junction("real/checkout/escape", outside);
        var shortcut = links.Junction("real/checkout/shortcut", inner);
        var intoRoot = links.Junction("from-outside", inner);
        var root = ProtectedCheckoutRoot.Prove(repo);

        Assert.False(ProtectedCheckoutRoot.Contains(root, escape));
        Assert.False(ProtectedCheckoutRoot.Contains(root, Path.Combine(escape, "other.txt")));
        Assert.False(ProtectedCheckoutRoot.Contains(root, outside));
        Assert.True(ProtectedCheckoutRoot.Contains(root, inner));
        Assert.True(ProtectedCheckoutRoot.Contains(root, Path.Combine(shortcut, "own.txt")));
        Assert.True(ProtectedCheckoutRoot.Contains(root, intoRoot)); // Physically inside, whatever its spelling.
        Assert.False(ProtectedCheckoutRoot.Contains(root, Path.Combine(repo, "not-there")));
        Assert.ThrowsAny<IOException>(() => ProtectedCheckoutRoot.Prove(escape, repo));
    }

    [Fact]
    public void A_sibling_sharing_the_roots_name_prefix_is_not_inside_it()
    {
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var sibling = links.Folder("real/checkout-sibling");
        Assert.False(ProtectedCheckoutRoot.Contains(repo, sibling));
        Assert.ThrowsAny<IOException>(() => ProtectedCheckoutRoot.Prove(sibling, repo));
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
        Assert.Equal(expected, ProtectedCheckoutRoot.Prove(direct!));
        Assert.Equal(expected, ProtectedCheckoutRoot.Prove(Path.Combine(middle!, "checkout", "nested")));
        Assert.Equal(expected, ProtectedCheckoutRoot.Prove(repo, direct));
        AssertRefusal(dangling!);
    }

    [Fact]
    public void A_plain_path_proves_itself_and_matches_the_general_answer()
    {
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var nested = links.Folder("real/checkout/nested/deeper");
        var expected = Links.TopLevel(repo);
        Assert.Equal(expected, ProtectedCheckoutRoot.Prove(repo));
        Assert.Equal(expected, ProtectedCheckoutRoot.Prove(nested));
        Assert.Equal(expected, ProtectedCheckoutRoot.Prove(nested, NativeCheckout.Inspect(nested).Root));
        Assert.Equal(expected, ProtectedCheckoutRoot.Physical(expected)); // Resolving twice changes nothing.
    }

    [Fact]
    public async Task A_proven_root_is_the_workspace_of_the_local_binding()
    {
        using var fixture = new NativeJoinBindingTests.ScopeFixture();
        await using var binding = await fixture.Acquire();
        Assert.Equal(binding.Workspace, ProtectedCheckoutRoot.Prove(fixture.Workspace));
    }

    [Fact]
    public void General_inspection_still_accepts_a_plain_checkout()
    {
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var inspected = NativeCheckout.Inspect(repo);
        Assert.Equal(Links.TopLevel(repo), inspected.Root);
        Assert.Equal(Path.Combine(Links.TopLevel(repo), ".git"), inspected.Common);
    }

    // Protected scope refuses this one. General inspection is pinned to what it has always answered, whatever Git reports.
    [Fact]
    public void General_inspection_still_accepts_a_core_worktree_outside_the_cwd()
    {
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var outside = links.Folder("outside");
        Links.Git(repo, "config", "--local", "core.worktree", outside);
        Assert.Equal(Links.TopLevel(repo), NativeCheckout.Inspect(repo).Root);
        Assert.Equal(ProtectedCheckoutRoot.Physical(outside), NativeCheckout.Inspect(repo).Root);
    }

    [SkippableFact]
    public void General_inspection_still_accepts_a_junction_spelling()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        Assert.Equal(Links.TopLevel(repo), NativeCheckout.Inspect(links.Junction("via", repo)).Root);
    }

    [SkippableFact]
    public void General_inspection_still_accepts_a_unc_checkout_that_git_accepts()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var links = new Links();
        var repo = links.Repository("real/checkout");
        var share = ReachableShare(repo);
        var git = Links.TryGit(share, "rev-parse", "--show-toplevel");
        Skip.If(git.Code != 0, "Git refuses this share as dubious without a safe.directory entry, and a test must not change global Git settings.");
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(git.Output.TrimEnd('\r', '\n'))), NativeCheckout.Inspect(share).Root);
        AssertNetworkRefusal(share);
    }

    [SkippableFact]
    public async Task A_unc_native_binding_that_git_accepts_still_opens()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnly);
        using var fixture = new NativeJoinBindingTests.ScopeFixture();
        var share = ReachableShare(fixture.Workspace);
        Skip.If(Links.TryGit(share, "rev-parse", "--show-toplevel").Code != 0, "Git refuses this share as dubious without a safe.directory entry, and a test must not change global Git settings.");
        await using var binding = await fixture.Acquire(share);
        Assert.StartsWith(@"\\", binding.Workspace, StringComparison.Ordinal);
    }

    private static void AssertRefusal(string path)
    {
        Assert.ThrowsAny<IOException>(() => ProtectedCheckoutRoot.Physical(path));
        Assert.ThrowsAny<IOException>(() => ProtectedCheckoutRoot.Prove(path));
    }

    // Names the reason, so that a path refused for some other reason cannot pass as a network refusal.
    private static void AssertNetworkRefusal(string path)
    {
        AssertRefusal(path);
        var error = Assert.ThrowsAny<IOException>(() => ProtectedCheckoutRoot.Physical(path));
        Assert.Contains("network", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReachableShare(string localPath)
    {
        var share = $@"\\localhost\{localPath[0]}$\{localPath[3..]}";
        Skip.IfNot(Directory.Exists(share), "This account cannot reach the loopback administrative share.");
        return share;
    }

    private static void OnSubst(string target, Action<string> test) => OnTemporaryDrive(
        drive => Links.Run("subst", drive, target).Code == 0,
        drive => Links.Run("subst", drive, "/D"),
        test);

    /// <summary>
    /// Runs a test on a drive letter nothing else holds. Removal runs only once this test's own mapping has succeeded, and always then. A mapping that
    /// did not succeed removes nothing, because the letter could have become someone else's.
    /// </summary>
    private static void OnTemporaryDrive(Func<string, bool> map, Action<string> unmap, Action<string> test)
    {
        var drive = FreeDrive();
        Skip.If(drive is null, "No drive letter is free for a temporary mapping.");
        Skip.IfNot(map(drive!), "This PC could not create the temporary drive mapping.");
        try { test(drive!); }
        finally { unmap(drive!); }
    }

    // A remembered, disconnected network drive is missing from GetDrives, but net use still lists it.
    private static string? FreeDrive()
    {
        var held = DriveInfo.GetDrives().Select(info => char.ToUpperInvariant(info.Name[0])).ToHashSet();
        var network = Links.Run("net", "use").Output;
        var substituted = Links.Run("subst").Output;
        foreach (var letter in "QRSTUVWXYZ")
        {
            var drive = letter + ":";
            if (!held.Contains(letter) && !network.Contains(drive, StringComparison.OrdinalIgnoreCase) && !substituted.Contains(drive, StringComparison.OrdinalIgnoreCase)) return drive;
        }
        return null;
    }

    /// <summary>A temp folder that holds real links. Its links go first, so that deleting it can never walk through one.</summary>
    private sealed class Links : IDisposable
    {
        private readonly List<string> created = [];
        internal string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cocern-protected-root-" + Guid.NewGuid().ToString("N"))).FullName;

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
            var output = Git(directory, "rev-parse", "--show-toplevel");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(output.TrimEnd('\r', '\n')));
        }

        internal static string Git(string directory, params string[] arguments)
        {
            var result = TryGit(directory, arguments);
            Assert.True(result.Code == 0, "Fixture Git failed: " + result.Output);
            return result.Output;
        }

        internal static (int Code, string Output) TryGit(string directory, params string[] arguments) =>
            Run(ExecutableSearch.Find("git") ?? throw new InvalidOperationException("Installed Git required"), ["-C", directory, .. arguments]);

        // Standard input is closed so that a command asking for a credential fails at once instead of waiting.
        internal static (int Code, string Output) Run(string file, params string[] arguments)
        {
            var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(name);
            using var process = Process.Start(start)!;
            process.StandardInput.Close();
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
