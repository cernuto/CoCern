# Verification

What has been checked beyond the automated tests, and what has not. Run the tests with `dotnet test CoCern.slnx`.

## Checked live

- **Two chats on one PC** (October 30, 2026). Codex and Claude Code worked through the node and native MCP tools. Messages, acknowledgments, stale-version conflicts, `Busy` claims and the Claude waiter all behaved as documented.
- **Codex wake-ups** (October 29). `codex queue` started real turns in two desktop chats. Four automatic turns finished a hand-off with no supervisor. A watcher restart sent no duplicates.
- **Discovery** (October 30). A separate hub and two nodes on one PC: joining, certificate pinning, `chat_directory`, a machine refused `control_request` (403), and a stopped node turning unreachable within 90 seconds.
- **Dashboard** (October 30). Desktop and phone sizes in a browser: sign-in, sessions, SQL, logs, light and dark themes, and a token change sending the browser back to sign-in.

## Not checked yet

- Discovery or the dashboard on a second physical PC or phone, through a real firewall and LAN certificate.
- `--setup` on a real PC: the Run key, client settings edits and hidden node start are covered by tests only.
- A real Codex chat woken by the node, and a real chat found from its transcript.
- Remote HTTPS, an external OAuth issuer, ChatGPT registration and Secure MCP Tunnel.
- Analytics loading the bundled extension on a real Windows hub. Its checksum and the publish layout were checked on Linux; DuckDB loading was checked there with the Linux extension.
- Overall token savings.
