# Collaboration workflow

PkRelay reduces repeated board reads and combines milestone reporting with an advisory next-work offer through the existing `task_get` and `task_update` tools. The HTTP, remote MCP and stdio surfaces use the same domain behavior. There is no new endpoint, MCP tool, scheduler or model-wakeup promise. Existing callers can continue using free-form task facts without structured workflow.

## Durable board checkpoints

Call `task_get` with `repositoryId`, no `taskId`, and `sinceRevision: 0` for an initial delta. The result contains bounded `items`, `snapshotRevision`, `nextCursor`, `truncated` and `total`. Ordinary board lists expose `boardRevision`. Keep checkpoints separately for each repository and board. A revision identifies committed task changes, including ownership and lease metadata; it is separate from each task's expected version. Rejected aggregate mutations and exact mutation retries do not advance it. A failed composed acquisition can include committed acquisition and rollback metadata changes, which remain visible. Existing tasks on older boards are visible from revision zero without changing their task versions.

Continue a delta with the same `sinceRevision`, the returned `snapshotRevision`, and the numeric `nextCursor` as `cursor`. Keep the same bounded `limit` (1–50). Save `snapshotRevision` as the next durable checkpoint only after consuming all pages. Then start the next delta with that checkpoint as `sinceRevision` and omit the old cursor/snapshot fields. SQLite restart preserves the board revision and permits continuation of an unchanged pinned snapshot.

If any task changes between pages, `DeltaRestartRequired` explicitly rejects the old snapshot. Discard that incomplete delta and restart from the last fully consumed checkpoint. This prevents a concurrent change from being silently skipped. Deltas describe current changed tasks; the relay does not copy chat transcripts or retain a task-change history. Read `task_get` with a selected `taskId` when full detail is needed.

## Structured reported readiness and completion

An optional `task_update.workflow` supplies the full workflow record. It is stored separately from free-form `patch` facts, so a fact named `status` cannot set structured readiness. Read the full record using `task_get` with `taskId` and `section: "workflow"`; board lists carry a compact summary.

```json
{
  "state": "ready",
  "packet": {
    "goal": "Verify the requested change and report the result",
    "target": "Repository and source revision to inspect",
    "authorityRef": "Reference to the applicable human instruction",
    "restoration": "Remove the fixture owned by this task",
    "stopCondition": "Stop if the source or tool target differs"
  },
  "criteria": [
    { "id": "verification", "description": "Required verification passes", "result": "not_run" }
  ],
  "blockers": [],
  "priority": 10
}
```

The closure packet requires nonempty `goal`, `target`, `authorityRef`, `restoration` and `stopCondition` strings. `restoration` describes the expectation; actual action outcomes and restoration evidence remain in the existing action reports. `authorityRef` preserves the source of existing human authority and grants no new permission. Provider approval remains separate.

Each of 1–32 named criteria has a unique `id`, a `description`, and a reported `result` of `not_run`, `passed` or `failed`. A passed criterion requires a nonempty `evidenceRef`. Report `state: "completed"` only when every criterion is passed with evidence, there are no blockers, and no external action or restoration remains unresolved. This is reported completion, not independent runtime verification or human acceptance.

`state: "blocked"` requires at least one blocker. Each blocker has a unique `id`, `kind`, `summary` and `recheckCondition`. Kinds are `coordinator_hold`, `provider_rejection`, `tool_unavailable`, `resource_busy`, `missing_fixture`, `prerequisite` and `other`. Other states require an empty blocker list. A blocked task never becomes ready merely because time, a claim lease or a worker observation expires. Explicitly replace the workflow under the current task version, clear the resolved blockers and include `recheckEvidenceRef` when changing blocked to ready. An already applicable human grant may resolve an unnecessary coordinator hold; it cannot resolve a provider rejection by itself.

If a previous owner's task lease has expired, an administrator may make a workflow-only recovery update under the expected version, provided no action or restoration remains unresolved. That update cannot also change facts, notes, messages or action reports. Active ownership and unresolved actions still block recovery; an ordinary worker must acquire the appropriate task claim before changing another owner's work.

Workflow JSON is bounded to 12,288 serialized UTF-8 bytes. Packet strings, criterion descriptions/evidence references, blocker summaries/recheck conditions and `recheckEvidenceRef` are at most 2,048 characters; criterion/blocker IDs are at most 128 characters. There are at most 16 blockers. Optional `targetSessionId` is at most 256 characters and names an intended session, never a private token. Optional integer `priority` ranges from -1,000 to 1,000. Optional `dependsOn` and `packet.resources` are described in [dependencies.md](dependencies.md).

## Report tokens

Add an optional `usage` object, `{ "inputTokens": n, "outputTokens": n }`, to `task_update` or `action_report` with the tokens used since your last report. It goes to the [activity log](activity.md) only and is never stored in the task.

## Report and request an advisory offer

Add `includeNextWork: true` to an authenticated `task_update` using the normal private session handle, stable mutation `requestId` and `expectedVersion`. The milestone commits normally and its result includes an independent `nextWork` object with `ok`, `code` and, when available, `offer`. Inspect both the milestone result and the optional convenience outcome.

`nextWork.code` is `Offered`, `NoEligibleWork`, `Paused` or `Unavailable`. Only valid explicitly ready workflows without blockers, unresolved external actions/restoration or an active conflicting owner are eligible. The current task is excluded; targeting respects `targetSessionId`, and a task with unfinished `dependsOn` tasks is not offered. Candidates sort by descending priority, then tasks whose listed resources are all free, then stable task identity. See [dependencies.md](dependencies.md). A global pause suppresses offers. Legacy tasks without workflow remain usable through ordinary reads and claims but do not enter this ready-work offer queue.

An offer contains `taskId`, `expectedVersion`, `snapshotRevision` and `claimRequired: true`. It includes the compact `workflow` when it fits; otherwise `detailFetchRequired: true` instructs the client to read `task_get` with the offered ID and `section: "workflow"`. Offers use a small reply budget and preserve the 32 KiB actual serialized host envelope bound. An optional offer failure cannot reverse a committed milestone or hide mandatory action, claim or restoration information.

An offer is a snapshot, acquires no ownership, reserves no resource and authorizes no execution. Multiple workers may receive the same unaccepted offer. Use the existing `task_claim` with its expected version and required resource claims, inspect the complete grant, and retain all existing human and provider permissions before doing direct tool work. A structured task that is blocked, completed or targeted to another session returns `WorkflowNotReady` on claim. Reconcile a lost race from current authoritative state. An uncertain mutation must reuse its original request identity; it must not become a second report.

Session tokens remain private on every transport. Repository enrollment and the exact session handle are checked by the application, including when two sessions belong to the same authenticated principal. Queue summaries and offers contain no private session token. See [host.md](host.md) for REST/MCP envelopes and [adapter.md](adapter.md) for the stdio result shape and recovery rules.
