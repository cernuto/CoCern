namespace CoCern.Contracts;

/// <summary>Where a hub listens unless configured otherwise. The hub, its adapters and Connect all start from these.</summary>
public static class HubAddresses
{
    /// <summary>Plain HTTP on this PC's loopback: the dashboard and a local adapter with no address of its own.</summary>
    public const string LocalUrl = "http://127.0.0.1:5271";
    /// <summary>The pinned loopback HTTPS that Connect points this PC's apps at (<c>Relay:Local:HttpsPort</c>).</summary>
    public const int LocalHttpsPort = 5272;
    /// <summary>The HTTPS listener other PCs join (<c>Relay:Network:HttpsPort</c>).</summary>
    public const int NetworkHttpsPort = 5273;
}
