# W2 disposable-node graceful-stop controller proposal

Status: docs only, no controller implemented or executed, no console attached or signal sent. Recommendation for the preserved B fixture: **NOT READY**. Its hidden launch and known parent chain do not establish an exclusive console. Manager dispatch `1b1bdf8f15e342b89f946b9c5928d1a5` requests preparation only; runtime execution needs a later scheduled window.

Root reports that the normal `--node` path cancels shutdown on `Console.CancelKeyPress`, and that the supervisor awaits/disposes its owned child. This source contract is a prerequisite, not installed-runtime proof. Confirm it against the exact package/source assigned to W2 before execution. Do not substitute CTRL+BREAK, window close, process termination, setup, reenrollment or a replacement product stop API.

## Why console membership is mandatory

Windows cannot target `CTRL_C_EVENT` to an individual process group. With group ID zero, it reaches the caller's attached console; a nonzero group ID is not a safe PID-targeted Ctrl+C workaround. Consequently this proposal permits a signal only inside a positively verified isolated fixture console. It forbids signaling an unverified/shared console. See Microsoft's [GenerateConsoleCtrlEvent contract](https://learn.microsoft.com/en-us/windows/console/generateconsolectrlevent).

A parent/child tree alone does not prove console membership. [GetConsoleProcessList](https://learn.microsoft.com/en-us/windows/console/getconsoleprocesslist) enumerates the caller's current console; it must return a complete bounded list, not a truncated buffer. [AttachConsole](https://learn.microsoft.com/en-us/windows/console/attaching-to-a-console) attaches the calling process, which can belong to only one console. These APIs describe a prospective controller method, not an observation already made on this PC.

## Prospective controller and proof packet

Use a dedicated short-lived controller process, never the Codex app, shared PowerShell session, collector or live node. Review its implementation before execution. It must have no children and make no enrollment, network, journal-write or runtime-configuration calls. Its only permitted runtime effect, after all gates pass, is one scheduled Ctrl+C event to the verified fixture console, then detachment and exit.

1. Manager assigns exact disposable run ID, package, node directory, supervisor/worker identities and finite shutdown deadline. Root and PC2 observers are READY before the action. Freeze unrelated fixture lifecycle work for the window.
2. Capture both fixture PIDs with creation times, executable paths/hashes, redacted fixture-only arguments and exact parent relationship. Pin process handles/creation identities where supported to detect PID reuse. Record the known launcher ancestry and distinguish historical exited parents from current ownership. A matching name, directory prefix or PID alone is insufficient.
3. The old evidence names supervisor 29748 and worker 8484 with parent 29748; these are historical expectations to revalidate, not a current authority or proof. The live node's historical PID 7896 and every unrelated app/shell must be absent from the target console. Do not copy live credentials or raw command/environment contents into the packet.
4. In the dedicated controller only, detach its own inherited console if needed, then attach explicitly to the verified supervisor PID. Never attach to a parent by shortcut or guess a console from a window title. Attachment failure, unavailable console or ambiguous target means NOT READY; do not allocate a replacement console, relaunch the fixture or fall back to a broad signal.
5. After attachment, install the controller's own reviewed Ctrl+C handler/ignore behavior so the helper can observe and detach; attachment resets handler state, so setup order matters. Do not change target handlers or spawn descendants with inherited ignore state. Microsoft's [Ctrl+C handling description](https://learn.microsoft.com/en-us/windows/console/ctrl-c-and-ctrl-break-signals) explains the per-process ignore behavior.
6. Obtain a complete console process list with bounded retries for a growing buffer. Its membership must be exactly the positively identified owned fixture supervisor and worker plus the dedicated controller itself. The controller is the sole measurement exception, explicitly identified in evidence. Any launcher, collector, app, live node, unknown PID or extra child means NOT READY. Do not broaden the allowlist just because another process appears related.
7. Recheck membership, process creation identities, ancestry and package identity immediately before signaling. Missing members, changed identity, PID reuse, failed inspection, inconsistent samples or a newly joined process abort without signaling. Console enumeration and event delivery are not atomic; this method needs a controlled isolated console with no concurrent attach/launch actor. If that isolation cannot be established, report NOT READY rather than claiming race-free targeting.
8. Only within a separately authorized W2 window, issue one supported `GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0)` from that controller. This is a console broadcast whose entire membership has just been verified, not a PID-targeted signal. Record call UTC and success/error code. No retry storm, alternate event, broad terminal keystroke, kill or forced cleanup follows a failed call.
9. Observe graceful supervisor/owned-worker exit and exit status through the deadline, plus fixture journal/credential/pin/machine/current/previous preservation and independent hub receipts as assigned. An API success alone does not prove handler receipt, child ownership behavior or graceful exit. If either process remains, record timeout/incomplete and ask the manager for disposition; never terminate it to manufacture a pass.
10. Detach only the controller and exit in a finally path, including every abort. Preserve the fixture, reports and unresolved state. No restart, next case or live enrollment change is implied by W2 completion.

## Required evidence and readiness disposition

| Evidence | Current state |
| --- | --- |
| Exact W2 package and verified installed Ctrl+C path | Root source report only; revalidation required |
| Fresh supervisor/worker creation identity and ancestry | Historical evidence only; no new runtime probe in this task |
| Complete exclusive console membership | UNOBSERVED; hidden launch is insufficient |
| Reviewed dedicated controller and safe self-handler order | Proposed, unimplemented, UNRUN |
| No concurrent console attachment/launch actors | Not established |
| Paired observers, manager window and shutdown deadline | Not scheduled by this preparation request |
| Signal, graceful exit and preservation outcomes | NOT RUN |

The current recommendation is **NOT READY**, not runnable on the existing evidence. If a later read-only membership preflight shows no usable console or any unrelated member, retain that result and return to the manager. A new isolated launch may require a different future fixture/window; do not alter the held B fixture to make this procedure runnable. No shell/C# executable controller accompanies this document.
