# Windows setup and repair

Extract the Windows package to a local folder and run `CoCern.Setup.exe`. Keep all extracted files together. Its executable is a thin launcher; the package includes the shared .NET runtime and does not need an SDK or separate runtime installation. An existing hub's **Connect → Download Windows adapter** link serves its adapter ZIP directly. The maintainer can also supply the generated Windows setup ZIP.

- **Host a hub on this PC** installs the verified package, starts the local hub, enrolls or reuses its owned background node and opens Connect. Choose your existing Git checkout to authorize native chats without editing configuration; configure the apps you want in Connect. The local enrollment code is read privately in memory and is not copied into node settings. The hub listens locally on port 5271; HTTPS enrollment for other PCs uses the existing pinned-certificate flow. Setup does not change firewall rules.
- **Join another hub** asks for the address, certificate fingerprint and join code from that hub's Connect page. It reuses enrollment and the node setup machinery. Codes and credentials are not copied into chat or saved in the installer settings.
- **Check or repair** verifies installed package files, enrollment, owned client settings, workflow skill and node startup. Its read-only check shows planned repairs; execution retains credentials, session bindings, authentication mode, disabled hooks and unrelated settings. A missing connection is restored only when a private native registration proves that profile was previously managed. If its old tool-permission choice is unavailable, restoration uses worker tools and says so in the preview.

New Codex connections use standard MCP without requiring native hook review. The workflow skill is installed independently; a customized skill is retained and does not block the connection. Optional native preparation cannot fail enrollment or prevent the background node from starting. Ordinary setup and Repair do not activate native mode or re-enable disabled hooks.

An existing native connection keeps its native verification requirements. An owned candidate may be refreshed for the actual configured helper while keeping hook commands unchanged; this does not approve trust or create startup evidence. Setup and Repair never adopt another chat, infer native chat IDs or replace enrollment credentials to bypass a failure.

Examples:

```powershell
.\CoCern.Setup.exe --help
.\CoCern.Setup.exe --host --repository workspace --checkout C:\source\my-repository
.\CoCern.Setup.exe --check
.\CoCern.Setup.exe --repair
```

`--install-root PATH` selects a persistent installation directory. `--no-open` starts the hub without opening a browser. The interactive Join prompt keeps its join code out of command-line history; automation can pass `--join --hub <https-origin> --fingerprint <sha256> --join-code <code>` deliberately. The adapter also provides `--repair --check --json` and `--repair --json`, with the existing hub connection options or an owned enrolled `--data-dir`.

Binary updates are installed into immutable package folders. Existing `data` and `node` folders are retained. A corrupt package fails its manifest/hash/runtime checks before the current installation pointer changes. An unrelated hub already listening on the local address is left alone. An older host launched from this installation's own immutable package folders may be restarted to load an update; other installations and processes are retained.

Setup prints its installed executable path under `%LOCALAPPDATA%\CoCern\packages` so you can run it again after removing the extracted download. Run that installed executable to open the same Host, Join and Repair menu.

For a PC outside the LAN, see [Visual Studio dev tunnels](dev-tunnels.md). The normal private tunnel web URL needs additional authorization and uses a different TLS certificate; it is not a drop-in replacement for the pinned LAN URL.

## For maintainers

```powershell
.\scripts\Publish-CoCern.ps1 -Runtime win-x64
```

Use `win-arm64` for native Windows ARM64. Publishing produces a ZIP and a file-integrity manifest under `artifacts/packages`, with separate staging outputs retained for inspection. It disables single-file embedding, ReadyToRun, unsafe trimming and debug/documentation files. Identical runtime/dependency files from the three publish outputs share one folder; a conflicting file hash stops packaging rather than silently selecting a different runtime. The command reports launcher and ZIP sizes. Repository verification remains governed by AGENTS.md and the exact Roslyn-selected test list.
