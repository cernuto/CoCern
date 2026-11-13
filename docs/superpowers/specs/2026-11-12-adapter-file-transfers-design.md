# Files between PCs through CoCern

## Intent and agreed behavior

Paul wants sessions to arrange PC-to-PC file transfers through CoCern. File contents stay outside AI context: adapters perform local file operations and stream the bytes through the hub. Paul chose explicit receiving-session acceptance and destination selection over automatic delivery to an inbox folder.

This is a new product capability, not a change to the current deployment. The following defaults and implementation choices are proposed by the designer, not decisions already made by Paul. Product implementation waits for approval of this document.

## User flow

1. A session requests a file from another session through the existing task/message tools. That request alone reads no file and grants no file access.
2. The source session calls `file_send` with its local source path, the exact recipient session ID and an optional task ID. Its adapter first obtains a caller/profile-checked `preparing` transfer ID from the hub, before any source file I/O. Its local job then hashes and uploads the source. Replies contain only the ID, state, expiry and metadata as it becomes available; unknown size/hash stay null while preparing.
3. The recipient gets an ordinary bounded transfer notification. Nothing is written on its PC automatically.
4. The recipient calls `file_receive` with the transfer ID and its chosen local destination path. Its adapter first passes the hub’s receive admission/profile check, then checks its own file permission, downloads to a temporary sibling file, verifies the size/hash, and atomically publishes the destination without replacement.
5. Both sessions can inspect compact progress or cancel. Completion identifies the recipient's recorded receipt. Neither model reads file chunks, base64, archive entries or binary error bodies.

Files are opaque ordinary files, including empty files. Directories must be packaged by the sender using its separately authorized local tools. Receipt never executes a file or extracts an archive.

## Local file authority

CoCern roles, task claims, session labels, a sender-supplied destination and an enrolled machine ID do not grant filesystem access. New file tools must pass the existing native/classic caller authentication, per-chat tool settings and local adapter tool admission before any local I/O. Recheck caller generation, cancellation and local access before publishing a received file.

The adapter cannot assume a client's shell sandbox also protects MCP server file operations. Add a local operator-controlled transfer-access configuration with separate allowed read and write roots. Transfers start disabled until that access is configured locally. A session cannot enable transfer access or enlarge these roots through a tool or message. Native checkout scope and existing file-claim generation fences remain intact; they are not replaced by an allow-root entry. Classic adapters use their authenticated session and explicit local roots, without fabricating native checkout proof.

Allowed roots may deliberately include artifact folders outside the session’s checkout, such as Downloads; this is an additional local operator grant, never authority inferred from a role or checkout. Inside a protected working copy, the existing verified native scope and applicable file-claim fences must also pass. Access to another participant’s protected working copy is denied even when a broad allow-root would otherwise include it. A classic session without the required native scope can use approved artifact folders but cannot bypass those protected-working-copy fences. The implementation must reuse the existing protected-root/claim admission; an allow-root is not an exception to it.

The receiver still chooses the exact destination within its allowed write roots. Existing parent directories are required; no implicit directory creation. Refuse device paths, alternate data streams, UNC/network paths and reparse-point/symlink traversal in the first Windows release. Resolve paths through physical handles and keep parent/source handles as required so a path switch cannot redirect a read or final rename. A pre-existing destination is a conflict, even when its content looks identical, except for recovery of this exact operation's recorded successful publication. There is no overwrite option in the first release.

Local read/write roots are configured through local setup/configuration, not a remote hub endpoint. The implementation plan must make the local setup command and configuration location concrete. Approval of this design authorizes implementing that mechanism, not silently granting directories on existing installations.

## Hub authorization and storage

Use the existing authenticated hub connection and certificate pinning. Transfer ownership is bound to repository, source session/principal and the exact recipient session/principal. Resolve a recipient from the current directory; a label is never an authorization target. First release is same-repository, same-hub only. A moved/rebound native session must present its current verified binding; a closed/replaced recipient is not silently substituted. Existing credentials and registration/claim rules are preserved.

Only the source can upload; only the named recipient can accept/download/acknowledge receipt. Both participants can read metadata and request cancellation. Administrative credentials do not implicitly confer access to file bytes. Revalidate authorization on each chunk and final transition. Session closure, revocation or tool-profile denial prevents further operations.

Store small durable transfer records through the existing aggregate/store conventions. Records include ID/version, participant identities, optional task ID, sanitized display name, byte count, SHA-256, committed upload offset, timestamps, state and receipt. They contain no file bytes, local destination paths, session tokens or client credentials. Source and destination paths stay in private local adapter operation journals; errors/logs must not echo file contents or credentials.

Store bytes in an ACL-protected spool beside the hub database, outside SQLite. Server-generated IDs determine spool paths; client filenames and paths never form server filesystem paths. Stream through bounded buffers. Reserve declared bytes against quotas before uploading, handle disk-full without claiming success, and only make a completely verified upload downloadable. The spool survives hub restarts; orphan/expired cleanup reconciles against durable metadata.

Proposed configurable defaults: 2 GiB per file, 10 GiB reserved spool capacity, two active transfers per session and four concurrent streams per hub. Offered/partial transfers expire after 24 hours. Completed bytes are deleted after one hour; terminal metadata and replay receipts remain for seven days. Cleanup is bounded and cancellation-aware. Limits are checked using declared and actual bytes, including chunk boundaries.

## Metadata tools and streaming endpoints

| MCP tool | Local action | Bounded reply |
| --- | --- | --- |
| `file_send(sourcePath, recipientSessionId, taskId?, requestId)` | Hash the authorized source and upload it. | Transfer ID, version, name, size, hash, state, expiry. |
| `file_receive(transferId, destinationPath, requestId)` | Explicitly accept and save locally. | Transfer ID, version, state, received bytes and local receipt. |
| `file_transfer_status(transferId)` | No file read; ask for progress. | Participant-visible metadata, offsets, progress time and terminal reason. |
| `file_transfer_cancel(transferId, expectedVersion, requestId)` | Stop the identified transfer when cancellation is still available. | Version and truthful cancellation/commit state. |

File send/receive are adapter-local operations. A raw hub tool call cannot turn a supplied path into a hub file read/write: it returns `AdapterRequired`. Advertise the same documented contracts consistently while keeping the host refusal explicit. Apply both static profiles and dynamic per-chat settings before adapter I/O; ordinary worker/native sessions can use the tools once locally enabled, while restricted profiles retain their restrictions. The tiny chat profile gains status only; file I/O requires a profile that exposes send/receive. No administrator credential is required merely to send one's authorized local file.

Long transfers run as local adapter jobs. The initiating call returns promptly with an ID; status/notifications report progress at bounded milestones, not every chunk or per-second model polling. Jobs may continue between model turns while that adapter is alive. On adapter restart, reconciliation reports interrupted work and an explicit retry resumes it; the node never chooses a destination or starts a write from a message. A private local journal and request ID exist before remote or filesystem side effects. Creation admits the actual `file_send` tool, not merely a generic status read; receive acceptance admits `file_receive`. This prevents reading or writing local files before the hub has applied that tool’s current per-chat profile. The preparation record counts against the session’s active-transfer limit; byte quota is reserved only when its descriptor is declared.

Proposed authenticated transport, under `/api/file-transfers`:

| Method and route | Purpose |
| --- | --- |
| `POST /` | Admit `file_send` and create an idempotent sender-bound `preparing` record before local file I/O; participants, task ID and opaque operation fingerprint only. |
| `POST /{id}/descriptor` | Set the name/size/hash after authorized hashing, reserve spool quota, and transition to uploading. |
| `GET /{id}` | Read participant-scoped metadata/progress. |
| `PUT /{id}/content` | Append a bounded upload chunk at the exact committed offset. |
| `POST /{id}/offer` | Verify the complete upload and publish the recipient notification. |
| `POST /{id}/accept` | Acquire the recipient's versioned receive lease. |
| `GET /{id}/content` | Stream bytes to that recipient, supporting a validated byte range and fixed content hash. |
| `POST /{id}/commit` | Fence final publication and acknowledge the exact receive operation. |
| `POST /{id}/cancel` | Request cancellation under the current version. |

These are streamed HTTP bodies, separate from JSON/MCP reply limits, message history, retained context and SQLite activity payloads. Credentials/proofs travel in authenticated headers, never URL query strings or AI replies. Responses use the existing error conventions: bad arguments, denied/hidden transfer, state/version conflict, source changed, destination exists, local path denied, quota/size exceeded, hash mismatch, expired and unavailable. No directory-list or arbitrary remote-path API is introduced. List/discovery uses the existing inbox/task references; broad transfer listing is unnecessary initially.

## Integrity, retries, cancellation and recovery

Hash the source before upload and stream from the same stable physical file handle. Refuse changes and incompatible file sharing. Retries reopen only with a matching complete source hash; they cannot mix versions into one transfer. Upload chunks carry an offset and digest; a repeated committed chunk is acknowledged only when identical. An uncertain chunk never blindly appends twice. Flush bytes before advancing the durable offset; recovery truncates any uncommitted tail.

The recipient journal binds its request ID to transfer ID, content hash and the exact destination. It tracks the temporary file and completed receipt. Same request/same inputs resumes or returns that receipt; changed inputs conflict. Verify any retained prefix before range resumption. A crash after final rename is reconciled against this journal and the destination's identity/hash, not reported as a new arbitrary destination collision or silently written again.

Use explicit states: preparing, uploading, offered, receiving, committing, completed, cancellation requested, cancelled, failed, expired and indeterminate. Final publication needs a versioned, recipient-bound commit grant after full hash verification. Once that grant is issued, cancellation reports commit in progress rather than falsely claiming no file can appear. Only the same receive operation may reconcile an interrupted commit. Never reuse a transfer ID for changed bytes or reassign it to a replacement session. Expiry/revocation stops further network access. A commit grant lasts at most ten minutes and never extends the transfer’s original 24-hour lifetime. At that deadline, a still-unacknowledged commit becomes terminal `indeterminate` with `localPublication: unknown`, releases its reserved quota and deletes its spool bytes. It is never relabeled cancelled or completed by inference. Keep its metadata and replay fence for seven days; after that return expired/unknown, never recreate the same ID. A local journal may report an already verified, owned local receipt with the remote indeterminate state, but grants no new download, rename or overwrite after expiry. A closed/revoked recipient cannot recover through a replacement identity. These rules bound disk retention even if the receiver never returns.

Before the commit grant, cancellation stops network I/O and removes owned partial files when safe. Successful completion means the receiver adapter reported a verified local publication, not that the file was opened, installed or executed. A disconnected receiver remains pending; its absence is not success. The hub stores no credentials for impersonating a recipient to finish a transfer.

## Scope and integration

Implement in the existing .NET 11/C# solution: shared metadata/contracts, an authorized hub transfer service and spool, an adapter streaming/job component, and admission in both `CoordinationTools.Create` and `NativeJoin.CallAsync`. Extend the existing native invocation and classic session ownership/replay machinery; do not create replacement sessions or bypass profile checks to perform local I/O. Keep ordinary message and status reply shapes compact.

No background remote file execution, folder synchronization, arbitrary node commands, automatic receive folder, public share links, cross-repository sharing, archive extraction or overwrite UI is included. Existing node update downloads remain separate. A new dashboard page is deferred; transfer notifications and status tools are sufficient for the first release.

Review authorization, physical path handling, disk recovery, lost acknowledgments and the existing native generation/closing fence. Add only a few tests for real authorization and data-loss/replay boundaries. Use Roslyn to select exact related methods and save verification for the implementation milestone, following repository rules. End-to-end proof uses disposable files and two explicit receiving contexts; an actual cross-PC proof needs an available source and recipient with locally approved roots. No build, test, transfer or deployment is part of this design-only change.
