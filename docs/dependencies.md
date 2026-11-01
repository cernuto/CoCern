# Dependencies and resource-aware offers

A next-work offer should be claimable on the first try. Two optional workflow fields help: `dependsOn` keeps a task out of offers until the tasks it needs are done, and `packet.resources` lets the hub offer a task whose resources are free.

Both are off until a task uses them. A board that never sets them behaves as before.

## Dependencies

Add `dependsOn` to a task's `workflow` in `task_update`:

```json
"workflow": { "state": "ready", "dependsOn": ["T-12", "T-14"], "packet": { ... }, "criteria": [ ... ], "blockers": [] }
```

- List up to 16 task IDs on the same board. Each must already exist.
- A dependency counts as done when its workflow state is `completed`.
- A task is offered only when all its dependencies are done. You can still claim it directly.
- The hub refuses a write that names an unknown task (`DependencyUnknown`) or makes a cycle anywhere on the board (`DependencyCycle`).
- Dependencies on another board are not supported. Use a `prerequisite` blocker for those.
- A `dependsOn` written before this check existed that names unknown or duplicate IDs keeps its task out of offers until it is fixed.

When a task's workflow becomes `completed`, the `task_update` reply lists the ready tasks it unblocked:

```json
{ "taskId": "T-12", "version": 7, "unblocked": ["T-15", "T-16"] }
```

The list holds at most 50 IDs; `unblockedTotal` appears when there are more.

Each unblocked task with a `targetSessionId` also raises a `next_work` [attention](attention.md) item to that session, unless it is the one completing. The item closes when anyone claims the task.

## Resources

Add `resources` to the workflow `packet` with the resource IDs the task expects to claim (up to 16, the same limit as `task_claim`):

```json
"packet": { "goal": "...", "target": "...", "authorityRef": "...", "restoration": "...", "stopCondition": "...", "resources": ["rig-1"] }
```

With `includeNextWork: true`, the hub takes the top 5 offerable tasks, resolves their listed resources with your session's machine, checks which ones another session holds right now (at most 32 lookups per offer), and ranks again. Priority still comes first. Within equal priority, a task whose resources are all free comes before one waiting on a busy resource, then task ID as before. Resources it cannot find or did not check count as free.

The offer is still advisory. It reserves nothing, and you still call `task_claim` with the resources.

## Require resources on a board

A board can refuse `ready` until `packet.resources` is present. An empty list counts as present, meaning "needs no resources". This board setting is `requireResources` and is off by default. An administrator turns it on with `standing_put` (`"settings": {"requireResources": true}`); see [standing.md](standing.md).

| Setting | Default | What it does |
| --- | --- | --- |
| Board `requireResources` | false | Refuse a `ready` workflow without `packet.resources` (`InvalidWorkflow`, `required: "packet.resources"`). |

## Limits

| Limit | Value |
| --- | --- |
| Dependencies per task | 16 (`RelayLimits.MaxDependencies`) |
| Resources per packet | 16 (`RelayLimits.MaxClaimResources`) |
| Candidates checked per offer | 5 (`RelayLimits.NextWorkCandidates`) |
| Resource lookups per offer | 32 (`RelayLimits.MaxOfferResourceLookups`) |
