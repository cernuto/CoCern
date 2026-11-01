# Live monitoring dashboard

PkRelay serves a Blazor WebAssembly dashboard at `/dashboard/`. The activity board, session inspector, SQL inventory, sanitized logs and correlated request/response/push feed receive complete SignalR observations. Watching the dashboard does not open agent sessions, read private inboxes, acknowledge messages or queue wakes.

## Enable a viewer

Sign-in sends the full administrator agent token to the hub. Sign in only on trusted networks, use trusted HTTPS from other devices, and never sign in after clicking through a certificate warning.

The dashboard is an instance-wide operator view, including SQL metadata across repositories. Enroll an existing administrator client and explicitly allow its principal ID using protected host configuration:

```json
{
  "Relay": {
    "Dashboard": {
      "ViewerPrincipalIds": ["your-existing-admin-principal"]
    }
  }
}
```

The environment equivalent is `Relay__Dashboard__ViewerPrincipalIds__0`. The allowlist is empty by default. The Visual Studio development launch profile grants the existing `PaulC` principal this observer role. Sign in with that principal's existing enrolled client credential; the browser clears the field and exchanges it for a dedicated HttpOnly, SameSite Strict cookie with a 30-minute absolute lifetime. Do not put credentials in source, URLs or shared relay notes. Dashboard cookies cannot authorize the agent APIs. Removing the principal, administrator role, observer grant or enrolled credential invalidates subsequent dashboard requests and live publication.

For other PCs and phones, use the host's reachable HTTPS address followed by `/dashboard/`, with a certificate trusted by that device and the configured HTTPS port permitted on the trusted network. Off-host plaintext authentication is rejected, including requests with spoofed forwarding headers. See [host configuration](host.md) and [cross-PC discovery](discovery.md) for network setup. The dashboard does not open firewall ports or enroll a device automatically.

## Read the observations

- Overview groups sessions by reported machine. Unregistered and unmapped states remain explicit. Reachability describes the reported wake path, not whether a model is running. Session selection opens its latest safe metadata.
- Relay traffic records real logical tool requests and responses, matched by correlation ID, and actual coordination pushes. Expand an entry to inspect safe metadata. Inspecting or scrolling holds the visible window; Resume live catches up. This session filters the window to the selected session. No arguments, response bodies, headers, inbox contents or credentials are captured.
- SQL shows provider-neutral table names, row counts, count accuracy, collection time and supported sizes. The current provider reports SQLite. Unknown metrics remain unavailable; capped counts are labeled as lower bounds. There is no query console or database mutation control.
- Standing shows each board's standing lines, grants, pending proposals and recent changes, with who wrote and approved them, and its [standing orders](orders.md) with their runs, failures and log. It is read-only and refreshes with the SQL metrics. See [standing.md](standing.md).
- Logs contain bounded, fixed-template relay summaries. Session-specific log entries may be absent even when traffic exists. Theme and signal motion are local presentation preferences; reduced motion and stale/disconnected states suppress activity cues.

The collector stays idle until an explicit viewer grant exists and a real SignalR viewer connects. While viewed, one shared sampler refreshes observations every two seconds and SQL metrics approximately every 15 seconds. Login attempts are limited to six per minute per normalized remote address bucket. A shared one-second enrollment cache bounds credential-file refreshes; stream grants are checked before each send. Windows are bounded to 200 sessions, 32 tables, 200 log entries and 120 traffic entries, within a 256 KiB snapshot ceiling. Truncation and stale observations are labeled. A new process instance resets snapshot revision ordering; reconnect obtains the current complete snapshot.

After a host update, reload open dashboard tabs to load the matching WASM client. Initial/new-process observations remain in a waiting state until a later server timestamp confirms progress; device clock offsets do not determine live status.

## Build and verify

Publish into a fresh output folder and switch deliberately; never rebuild a running host's output or start a second host against its database. The host publishes the WASM assets with its normal project reference. This verification environment used the prebuilt WASM runtime because the optional native relinker's `emcc` executable was unavailable on PATH:

```powershell
dotnet publish src/PkRelay.Host/PkRelay.Host.csproj --artifacts-path artifacts/dashboard-build -c Release -o artifacts/dashboard-preview --nologo -p:NuGetAudit=false -p:WasmBuildNative=false
```

The Release output retains assembly trimming. Preview credentials, private fixture handles, databases, binaries and screenshots belong under ignored `artifacts/`, never in source control. Verification evidence is recorded in [dashboard verification](dashboard-verification.md).

## Deferred follow-ups

The following are deferred, with no implementation in this pass:

- Server-side per-cookie sign-out revocation. Sign-out clears the current browser cookie; an independently copied cookie is bounded by its absolute expiry and current enrollment/observer grant validation.
- A dashboard Content Security Policy and frame-ancestors policy.
- Centralizing the shared live/ready interpretation in Domain.
- Decorating ICoordinationNotifier for traffic observation rather than the present host notifier hook.
- Removing unused LastActivityAt/priorActivity scaffolding.
- Removing or replacing the authorized snapshot endpoint currently used by integration tests.

The existing limits remain explicit: recent traffic is bounded rather than a lossless audit history, and physical remote-device TLS/firewall onboarding requires separate verification.



