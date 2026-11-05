# Local wake startup retry

The adapter makes at most three local-wake setup attempts before serving MCP, with 250 ms and 750 ms waits. Only a missing configured token file or a setup I/O error is eligible. A successful attempt ends the retry immediately.

Every attempt keeps the same adapter options, hub identity and expected credential. Credential mismatch, access denial, invalid configuration, registered-native mode and node mode remain terminal. A later different token cannot silently move the chat to another principal. Diagnostics retain their existing fixed reason codes and never include paths, credentials or exception text.

This is bounded startup recovery, not another watcher or scheduler. Existing wake loops and their four-second reconciliation interval remain unchanged. A deterministic source-only security test covers stopping on a credential refusal and the attempt bound, with no file access, timers or machine changes. No tests or builds were run for this feature; verification waits for the related pre-deploy selection.
