# Debug Adapter Deployment Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task by task, with one fresh review at the end.

**Goal:** Starting the Visual Studio host debug profile deploys changed adapter binaries into an immutable version directory and refreshes the local Codex adapter connection automatically.

**Architecture:** The debug host depends on the adapter build and enables a development-only startup service. The service hashes and copies the adapter output to artifacts/development-adapter/versions/<hash>, publishes an atomic restart manifest, and configures the existing local Codex entry to use a development launcher in a version directory. The launcher remains attached to Codex and opens a fresh MCP SDK client connection to the selected adapter backend on each debug-start generation, draining calls before replacing the backend and notifying Codex that its tool list changed. This avoids relying on an unavailable desktop control socket or overwriting loaded binaries.

**Tech Stack:** .NET 10, existing MCP SDK 2.3, ASP.NET Core hosted services, existing conservative TOML reader, Visual Studio MCP builds.

**Spec:** The user's request in this chat and the stated development-launcher behavior.

## Global Constraints

- Use both Roslyn CodeLens and Visual Studio MCP for C# inspection and IDE/build work; never build into a running application's output folder.
- Preserve existing credentials, role, unrelated MCP entries, and user configuration. Update only an existing CoCern entry tied to this hub's token file; back up configuration before an atomic replacement.
- Enable through the local Development debug profile only. Normal published hubs and joined PCs retain their existing startup behavior.
- Restart every debug-start generation, but create a new version directory only when output content changes. Never overwrite a published version or replay in-flight tool calls.
- Native implementation in the current Visual Studio workspace; a feature branch preserves isolation without switching the IDE to another checkout. No commit or push is part of this request.

## Review Focus

- Concurrent starts and incomplete deployment must not expose partial binaries or an invalid manifest.
- Invalid manifests, path traversal, or symlinks must not launch files outside the version root.
- A failed new backend must leave the old backend usable, and a restart must wait for an in-flight call.
- Configuration editing must preserve token values and unrelated text and refuse foreign or unsupported CoCern entries.
- Debugging stops and ordinary builds must leave the launcher outside bin/Debug; starting again must select the current build.

### Task 1: Immutable deployment and local configuration

**Files:** Create development deployment helpers and tests; extend ClientSettings through a separate development setup path.

**Interfaces:** AdapterDeployment.Publish(sourceDirectory, deploymentRoot) returns the immutable version and writes current.json with a fresh generation. ClientSettings.PlanDevelopmentCodex(configPath, dotnetPath, launcherDll, deploymentRoot, tokenFile) returns a backed-up atomic plan for the existing owned entry.

- [x] Add behavioral tests for changed/unchanged binaries, fresh generations, incomplete input, hostile paths, owned/foreign entries, and credential preservation; observe failing tests.
- [x] Implement deployment and configuration behavior and run focused tests through freshly built VS binaries.

### Task 2: Development launcher and MCP reconnection

**Files:** Create the adapter development launcher and connection manager; extend the adapter entry point with explicit development modes.

**Interfaces:** The launcher reads current.json, connects to the selected backend through the existing MCP SDK, forwards tools/list and tools/call, and reloads at safe request boundaries when Generation changes.

- [x] Add process/SDK tests proving backend replacement, exact result forwarding, failed replacement fallback, and completion of an in-flight call without replay.
- [x] Implement the launcher, test it with the real adapter and preserve its structured hub-unavailable response, and verify shutdown disposes only its own child.

### Task 3: Visual Studio debug startup and end-to-end verification

**Files:** Host startup service, host project dependency, launchSettings.json, README/reference documentation.

- [x] Enable the service only in the Development debug profile and make the adapter an explicit build dependency; the host loads its own copied deployment helpers, while MCP clients run immutable version copies.
- [x] On application start, deploy the current output, migrate only the owned Codex entry, and publish the restart generation. Perform any one-time direct adapter migration only for the verified Codex-owned debug adapter.
- [x] Build through Visual Studio MCP; confirm refreshed Roslyn symbols and diagnostics; run focused deployment, connection, and existing Connect tests.
- [x] Start debugging through Visual Studio MCP, verify the running backend's version directory, stop/start and verify a fresh backend without another directory for unchanged content, then verify a changed build selects a new version.
- [x] Obtain one fresh review, address actionable findings, and report exact verification and any limitation.
