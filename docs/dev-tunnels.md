# Visual Studio dev tunnels

A remote PC such as SC3 can reach a CoCern hub running under Visual Studio through private port forwarding. Use a build that keeps network-listener traffic separate from hub-local dashboard access and checkout identity.

The ordinary `https://…devtunnels.ms` web URL is not supported as a drop-in CoCern origin. Microsoft terminates its TLS using a service certificate, while the installer pins CoCern's own hub certificate. Private web URLs also require tunnel authorization that the current CoCern HTTP clients do not supply. See Microsoft's [tunnel security](https://learn.microsoft.com/azure/developer/dev-tunnels/security) and [Visual Studio tunnel documentation](https://learn.microsoft.com/aspnet/core/test/dev-tunnels?view=aspnetcore-10.0).

The browser can still download the ZIP from a private tunnel's `/join/adapter.zip` link after normal tunnel sign-in. This downloads the package; the installed adapter uses the private TCP connection below to join and communicate with the hub.

## Private port forwarding

1. Run CoCern with its network HTTPS listener enabled. The repository's Visual Studio profile enables it; its default port is **5273**.
2. Create a persistent private tunnel and share only the network listener port. Retain the tunnel ID. Visual Studio and CoCern must keep running for that tunnel to work.
3. On SC3, sign in with an account allowed to use that tunnel and connect with the Dev Tunnels CLI:

```powershell
devtunnel user login
devtunnel connect <tunnel-id>
```

4. Confirm the CLI reports a local forwarding port for the shared **5273** listener. In CoCern's Join installer, use `https://127.0.0.1:5273/` when that is the reported local port, together with the original CoCern hub fingerprint and join code. Keep the forwarding process running.

The CLI carries the original TCP connection to the hub, so CoCern's existing TLS pin and enrollment remain in use. Tunnel access is authorized by the CLI login rather than an application HTTP header. Microsoft documents this connection method in its [CLI reference](https://learn.microsoft.com/azure/developer/dev-tunnels/cli-commands#connect-to-a-dev-tunnel).

Share the network listener, not the privileged local HTTP **5271** or local HTTPS **5272** listener. Forwarding a privileged local listener is equivalent to granting local access. On the network listener, even a forwarded loopback socket must sign in normally for the dashboard, and repository identity uses the authorized Git origin rather than the remote PC's path digest.

This describes the supported transport arrangement; it does not claim a particular tunnel or SC3 installation has been provisioned or tested. A Visual Studio tunnel selecting only the launch-profile web port needs its network listener port configured before it can be used this way.
