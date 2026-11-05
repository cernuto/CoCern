using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using CoCern.Adapter.Setup;
using Xunit;

namespace CoCern.Tests;

/// <summary>Assertions shared by the adapter and node tests. Bring in with <c>using static</c>.</summary>
internal static class AdapterTestKit
{
    /// <summary>The file can be read only by this user: no inherited access on Windows, mode 600 elsewhere.</summary>
    internal static void AssertPrivate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new FileInfo(path).GetAccessControl();
            var rule = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
            Assert.False(rule.IsInherited);
        }
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}

/// <summary>A logon run key held in memory, so no test touches the real Windows setting.</summary>
internal sealed class FakeRunKey : IRunKey
{
    public Dictionary<string, string> Values { get; } = [];
    public string? Get(string name) => Values.GetValueOrDefault(name);
    public void Set(string name, string value) => Values[name] = value;
}

/// <summary>A handler for a hub that isn't there: every request gets 404.</summary>
internal sealed class NoHub : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
}
