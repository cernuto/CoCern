using System.Diagnostics;
using System.Text.Json;
namespace CoCern.Setup;

internal static class Program
{
    public static Task<int> Main(string[] args) => SetupApplication.RunAsync(args);
}

internal static class SetupApplication
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed record HostSettings(string Kind, Dictionary<string, string> Environment);
    internal static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            if (arguments.Contains("--help")) { PrintHelp(); return 0; }
            if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("CoCern Setup currently supports Windows."); return 1; }
            var options = Parse(arguments);
            if (options.Mode is null)
            {
                Console.WriteLine("CoCern Setup\n1. Host a hub on this PC\n2. Join another hub\n3. Check or repair this installation");
                Console.Write("Choose 1, 2 or 3: ");
                options = options with { Mode = Console.ReadLine()?.Trim() switch { "1" => "host", "2" => "join", "3" => "repair", _ => throw new IOException("No installation was selected.") } };
                if (options.Mode == "join")
                {
                    Console.Write("Hub HTTPS address: "); var hub = Console.ReadLine();
                    Console.Write("Hub certificate fingerprint (from Connect): "); var pin = Console.ReadLine();
                    Console.Write("Join code (from Connect; kept out of saved settings): "); var code = ReadSecret();
                    options = options with { Hub = hub, Fingerprint = pin, JoinCode = code };
                }
                else if (options.Mode == "host")
                {
                    Console.Write("Repository Git checkout folder (Enter to configure later): ");
                    if (Console.ReadLine()?.Trim() is { Length: > 0 } checkout)
                    {
                        checkout = Path.GetFullPath(checkout);
                        var label = System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(Path.TrimEndingDirectorySeparator(checkout)).ToLowerInvariant(), "[^a-z0-9._-]", "-").Trim('-', '.');
                        options = options with { Checkout = checkout, Repository = label.Length is > 0 and <= 100 ? label : "workspace" };
                    }
                }
            }
            var root = Path.GetFullPath(options.Root);
            if (options.Check)
            {
                var current = SetupPackage.Current(root); SetupPackage.Validate(current);
                Console.WriteLine("Installed self-contained package: verified.");
                return await RepairAsync(root, current, check: true);
            }
            var package = SetupPackage.Install(AppContext.BaseDirectory, root);
            Console.WriteLine("CoCern package verified and installed. Existing data and credentials were retained.");
            if (options.Mode == "host") return await HostAsync(root, package, options);
            if (options.Mode == "join")
            {
                if (!Uri.TryCreate(options.Hub, UriKind.Absolute, out var hub) || hub.Scheme != "https" || !string.IsNullOrEmpty(hub.UserInfo) || !string.IsNullOrEmpty(hub.Query) || !string.IsNullOrEmpty(hub.Fragment) || hub.AbsolutePath != "/" || string.IsNullOrWhiteSpace(options.Fingerprint) || string.IsNullOrWhiteSpace(options.JoinCode)) throw new IOException("Join needs the hub's root HTTPS address, certificate fingerprint and join code from Connect.");
                var start = Adapter(package, ["--setup", "--hub", hub.AbsoluteUri, "--fingerprint", options.Fingerprint, "--data-dir", SetupPackage.Contained(root, "node")]);
                start.Environment["COCERN_JOIN_CODE"] = options.JoinCode;
                return await RunAsync(start);
            }
            if (File.Exists(SetupPackage.Contained(root, "host-settings.json"))) await HostAsync(root, package, options with { NoOpen = true });
            return await RepairAsync(root, package, check: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { Console.Error.WriteLine("CoCern Setup: " + error.Message); return 1; }
    }

    internal sealed record Options(string? Mode, string Root, bool Check = false, bool NoOpen = false, string? Hub = null, string? Fingerprint = null, string? JoinCode = null, string Repository = "workspace", string? Checkout = null);
    internal static Options Parse(string[] arguments)
    {
        var options = new Options(null, DefaultRoot());
        for (var i = 0; i < arguments.Length; i++)
            options = arguments[i] switch
            {
                "--host" when options.Mode is null => options with { Mode = "host" },
                "--join" when options.Mode is null => options with { Mode = "join" },
                "--repair" when options.Mode is null => options with { Mode = "repair" },
                "--check" when !options.Check => options with { Check = true, Mode = options.Mode ?? "repair" },
                "--no-open" => options with { NoOpen = true },
                "--install-root" when i + 1 < arguments.Length => options with { Root = Path.GetFullPath(arguments[++i]) },
                "--hub" when i + 1 < arguments.Length => options with { Hub = arguments[++i] },
                "--fingerprint" when i + 1 < arguments.Length => options with { Fingerprint = arguments[++i] },
                "--join-code" when i + 1 < arguments.Length => options with { JoinCode = arguments[++i] },
                "--repository" when i + 1 < arguments.Length => options with { Repository = arguments[++i] },
                "--checkout" when i + 1 < arguments.Length => options with { Checkout = Path.GetFullPath(arguments[++i]) },
                _ => throw new IOException("Unknown setup option. Run CoCern.Setup.exe --help.")
            };
        return options;
    }

    private static string DefaultRoot()
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        for (var directory = new DirectoryInfo(source); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, SetupPackage.ReceiptName)))
            {
                try { if (string.Equals(Path.TrimEndingDirectorySeparator(SetupPackage.Current(directory.FullName)), source, StringComparison.OrdinalIgnoreCase)) return directory.FullName; }
                catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
            }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoCern");
    }

    private static async Task<int> HostAsync(string root, string package, Options options)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(options.Repository, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,99}$")) throw new IOException("Repository ID is invalid.");
        var settingsPath = SetupPackage.Contained(root, "host-settings.json");
        var originalSettings = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
        HostSettings settings;
        if (File.Exists(settingsPath))
        {
            if (new FileInfo(settingsPath).Length > 65536) throw new IOException("Host settings are too large.");
            settings = JsonSerializer.Deserialize<HostSettings>(File.ReadAllBytes(settingsPath), Json) ?? throw new IOException("Host settings could not be read.");
            if (settings.Kind != "cocern-host-settings-v1") throw new IOException("Existing host settings are not owned by CoCern Setup.");
        }
        else
        {
            var data = SetupPackage.Contained(root, "data");
            Directory.CreateDirectory(data);
            settings = new("cocern-host-settings-v1", new()
            {
                ["ASPNETCORE_URLS"] = "http://127.0.0.1:5271", ["Relay__DatabasePath"] = Path.Combine(data, "relay.db"),
                ["Relay__Clients__0__TokenFile"] = Path.Combine(data, "local-client-token"), ["Relay__Clients__0__PrincipalId"] = Environment.UserName,
                ["Relay__Clients__0__IsAdministrator"] = "true", ["Relay__Clients__0__Repositories__0"] = options.Repository,
                ["Relay__Dashboard__ViewerPrincipalIds__0"] = Environment.UserName, ["Relay__Discovery__Enabled"] = "true",
                ["Relay__Discovery__Repositories__0"] = options.Repository, ["Relay__Network__Enabled"] = "true"
            });
        }
        if (options.Checkout is not null) ConfigureCheckout(settings.Environment, options.Repository, options.Checkout);
        settings.Environment["Relay__Connect__AdapterPath"] = SetupPackage.Contained(package, "CoCern.Adapter.dll");
        var encodedSettings = JsonSerializer.Serialize(settings, Json);
        var settingsChanged = encodedSettings != originalSettings;
        if (settingsChanged) File.WriteAllText(settingsPath, encodedSettings);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var url = settings.Environment["ASPNETCORE_URLS"].TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var local) || !local.IsLoopback || local.Scheme != "http" || !string.IsNullOrEmpty(local.UserInfo) || !string.IsNullOrEmpty(local.Query) || !string.IsNullOrEmpty(local.Fragment)) throw new IOException("The host must use a loopback HTTP dashboard address.");
        var alive = false;
        try { using var response = await client.GetAsync(url + "/health"); alive = response.IsSuccessStatusCode; } catch (HttpRequestException) { } catch (TaskCanceledException) { }
        if (alive)
        {
            var owned = new List<Process>();
            foreach (var process in Process.GetProcessesByName("CoCern.Host"))
            {
                try { if (process.MainModule?.FileName is { } image && image.StartsWith(SetupPackage.Contained(root, "packages") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) owned.Add(process); else process.Dispose(); }
                catch (System.ComponentModel.Win32Exception) { process.Dispose(); }
            }
            if (owned.Count == 0) throw new IOException("Another hub is already using this address. Its data and process were left alone; open its dashboard or stop it before starting this installation.");
            var expected = SetupPackage.Contained(package, "CoCern.Host.exe");
            foreach (var process in owned)
                using (process)
                {
                    if (!settingsChanged && string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) continue;
                    var startedAt = process.StartTime;
                    if (process.HasExited) continue;
                    if (process.StartTime != startedAt || process.MainModule?.FileName is not { } image || !image.StartsWith(SetupPackage.Contained(root, "packages") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("The hub identity changed before restart.");
                    process.Kill();
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await process.WaitForExitAsync(stop.Token);
                    alive = false;
                }
            if (alive) Console.WriteLine("The current installed hub is already running; its data and process were preserved.");
        }
        if (!alive)
        {
            var previous = settings.Environment.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
            try
            {
                foreach (var variable in settings.Environment) Environment.SetEnvironmentVariable(variable.Key, variable.Value);
                using var started = Process.Start(new ProcessStartInfo(SetupPackage.Contained(package, "CoCern.Host.exe")) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = package }) ?? throw new IOException("The hub could not start.");
            }
            finally { foreach (var variable in previous) Environment.SetEnvironmentVariable(variable.Key, variable.Value); }
            var ready = false;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                try { using var response = await client.GetAsync(url + "/health"); if (response.IsSuccessStatusCode) { ready = true; break; } } catch (HttpRequestException) { } catch (TaskCanceledException) { }
                await Task.Delay(250);
            }
            if (!ready) throw new IOException("The hub did not become ready. Data and credentials were retained; use Repair to inspect this installation.");
        }
        var localNode = Adapter(package, ["--setup-local-node", "--hub-data", Path.GetDirectoryName(settings.Environment["Relay__DatabasePath"])!, "--data-dir", SetupPackage.Contained(root, "node")]);
        var nodeExit = await RunAsync(localNode);
        if (nodeExit != 0) throw new IOException("The hub is running, but its background node could not be prepared. Data, credentials and app connections were retained; use Repair to inspect it.");
        Console.WriteLine("Installed setup: " + SetupPackage.Contained(package, "CoCern.Setup.exe"));
        Console.WriteLine("Open " + url + "/dashboard/connect to connect and verify your apps.");
        if (!settings.Environment.Keys.Any(key => key.StartsWith("Relay__DocCache__Repositories__", StringComparison.Ordinal) && key.EndsWith("__ClonePath", StringComparison.Ordinal)))
            Console.WriteLine("Repository setup is still needed for native chats. Run CoCern.Setup.exe --host --checkout <your Git folder> to add its authorized mapping without editing configuration.");
        if (!options.NoOpen) using (Process.Start(new ProcessStartInfo(url + "/dashboard/connect") { UseShellExecute = true })) { }
        return 0;
    }

    private static async Task<int> RepairAsync(string root, string package, bool check)
    {
        var arguments = new List<string> { "--repair" };
        if (check) arguments.Add("--check");
        var hostSettings = SetupPackage.Contained(root, "host-settings.json");
        if (File.Exists(hostSettings))
        {
            var settings = JsonSerializer.Deserialize<HostSettings>(File.ReadAllBytes(hostSettings), Json);
            if (settings?.Kind != "cocern-host-settings-v1") throw new IOException("Host settings are not owned by CoCern Setup.");
            arguments.AddRange(["--token-file", settings.Environment["Relay__Clients__0__TokenFile"], "--hub", settings.Environment["ASPNETCORE_URLS"]]);
        }
        else arguments.AddRange(["--data-dir", SetupPackage.Contained(root, "node")]);
        return await RunAsync(Adapter(package, arguments));
    }

    private static ProcessStartInfo Adapter(string package, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(SetupPackage.Contained(package, "CoCern.Adapter.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }
    private static void ConfigureCheckout(Dictionary<string, string> environment, string repository, string checkout)
    {
        if (!Directory.Exists(Path.Combine(checkout, ".git")) && !File.Exists(Path.Combine(checkout, ".git"))) throw new IOException("Choose an existing Git checkout for --checkout.");
        var index = 0;
        while (environment.TryGetValue($"Relay__DocCache__Repositories__{index}__Id", out var existing) && existing != repository) index++;
        var prefix = $"Relay__DocCache__Repositories__{index}__";
        if (environment.TryGetValue(prefix + "ClonePath", out var previous) && !string.Equals(Path.GetFullPath(previous), Path.GetFullPath(checkout), StringComparison.OrdinalIgnoreCase)) throw new IOException("That repository ID already maps to another checkout. Use a different --repository ID to retain the existing mapping.");
        environment[prefix + "Id"] = repository; environment[prefix + "ClonePath"] = checkout; environment[prefix + "Fetch"] = "false";
        try
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-C", checkout, "symbolic-ref", "--quiet", "--short", "HEAD" }) start.ArgumentList.Add(argument);
            using var git = Process.Start(start)!; var stdout = git.StandardOutput.ReadToEndAsync(); var stderr = git.StandardError.ReadToEndAsync();
            if (!git.WaitForExit(3000)) { git.Kill(); throw new IOException("Git checkout discovery timed out."); }
            var branch = stdout.GetAwaiter().GetResult().Trim(); _ = stderr.GetAwaiter().GetResult();
            if (git.ExitCode == 0 && System.Text.RegularExpressions.Regex.IsMatch(branch, "^[a-zA-Z0-9._/-]{1,200}$")) environment[prefix + "Branch"] = branch;
        }
        catch (System.ComponentModel.Win32Exception) { throw new IOException("Git must be installed to configure the selected repository checkout. No SDK is required."); }
        foreach (var list in new[] { "Relay__Clients__0__Repositories__", "Relay__Discovery__Repositories__" })
        {
            var slot = 0;
            while (environment.TryGetValue(list + slot, out var current)) { if (current == repository) break; slot++; }
            environment[list + slot] = repository;
        }
    }
    private static async Task<int> RunAsync(ProcessStartInfo start) { using var process = Process.Start(start) ?? throw new IOException("The adapter could not start."); await process.WaitForExitAsync(); return process.ExitCode; }
    private static string ReadSecret()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; continue; }
            if (!char.IsControl(key.KeyChar) && value.Length < 1024) value.Append(key.KeyChar);
        }
    }
    private static void PrintHelp() => Console.WriteLine("CoCern.Setup.exe [--host | --join | --repair] [--check] [--install-root PATH] [--no-open]\nHost: --repository ID [--checkout GIT_FOLDER]. Join: --hub HTTPS_ORIGIN --fingerprint SHA256 --join-code CODE (or use the interactive prompt). --check inspects an existing installation without changing it. The self-contained Windows package needs no SDK or .NET installation. Hook trust is always reviewed in Codex.");
}
