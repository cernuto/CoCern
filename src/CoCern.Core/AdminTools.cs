using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;

namespace CoCern.Core;

/// <summary>
/// <c>hub_diagnose</c>, <c>add_pc</c> and <c>connect_app</c> as the hub calls them. The hub's host implements this over what its dashboard
/// uses. The router has already checked that the caller is an administrator.
/// </summary>
public interface IAdminTools
{
    Task<RelayReply> RunAsync(RelayCaller caller, string tool, JsonObject args, CancellationToken cancellationToken);
}

public static class AdminTools
{
    /// <summary>What these tools answer in a hub built without a host to serve them.</summary>
    public static readonly IAdminTools Off = new None();

    private sealed class None : IAdminTools
    {
        public Task<RelayReply> RunAsync(RelayCaller caller, string tool, JsonObject args, CancellationToken cancellationToken) =>
            Task.FromResult(Replies.Error(RelayErrorCodes.Unavailable, new { message = "This hub has no administrator tools." }));
    }
}
