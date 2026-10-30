# Cross-PC discovery

## Network

By default the hub listens only on loopback (`http://127.0.0.1:5271`). To let other PCs reach it, set `Relay:Network:Enabled` to `true`. The hub then keeps the loopback HTTP endpoint and also serves HTTPS on every network interface.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Relay:Network:Enabled` | `false` | Turns the HTTPS network listener on. |
| `Relay:Network:HttpsPort` | `5273` | The HTTPS port. |
| `Relay:Network:CertificatePath` | `data/hub-certificate.pfx` | The hub's self-signed certificate, relative to the content root. It is created on first start and reused afterwards. The file holds the private key, so it is readable only by the user running the hub. |

The hub writes its certificate fingerprint (uppercase hex SHA-256) to the log at startup. Other PCs pin that fingerprint. Never delete or replace the certificate file unless you intend every PC to pin the new fingerprint.

### Firewall

Setup never changes firewall rules. To allow other PCs to reach the hub, run this once yourself from an elevated PowerShell on the hub PC. It allows inbound TCP on port 5273 only:

```powershell
New-NetFirewallRule -DisplayName "PkRelay hub (HTTPS 5273)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5273 -Profile Private
```

The rule is limited to the Private network profile; drop `-Profile Private` if your network is classed as Public or Domain and you still want it reachable. Remove it with `Remove-NetFirewallRule -DisplayName "PkRelay hub (HTTPS 5273)"`. If you change `Relay:Network:HttpsPort`, use that port in the rule instead.
