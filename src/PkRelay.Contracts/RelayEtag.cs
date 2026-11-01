using System.Security.Cryptography;
using System.Text;

namespace PkRelay.Contracts;

/// <summary>
/// The short etag of a read reply's data, the same in the adapter (which hands it to the agent) and the hub (which tells a read that came
/// back unchanged from a full one). A compacted reply has other data, so it always gets its own etag.
/// </summary>
public static class RelayEtag
{
    public static string Of(string dataJson) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(dataJson)))[..16];
}
