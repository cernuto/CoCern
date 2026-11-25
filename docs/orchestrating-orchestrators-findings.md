# Ideas for CoCern from “orchestrating orchestrators”

Reading notes on Bas Nijholt’s *I used to orchestrate agents. Now I orchestrate orchestrators*, November 25, 2026. Four ideas for the task dashboard, then four that reach beyond it.

**Recommendation.** Start by showing the latest outcome, who acts next and the supporting report on each task row. Nijholt describes the cost of switching among long-running tasks as having to remember where each one stands. CoCern already records much of what a returning operator needs across its task, action and blocker records. Bringing those facts together on the existing Tasks page would make it easier to pick up work after an interruption. [Nijholt’s article](https://www.nijho.lt/post/orchestrating-orchestrators/) · [CoCern dashboard guide](../README.md#the-dashboard)

## What the code has today

Checked against `main` at `068a7c9` on November 25, 2026, by reading source. Nothing was run.

- **The Tasks page is a three-column table**, not cards: task ID with version, workflow state, and owner. Its feed row, `DashboardBoardRow`, carries only the task ID, version, owner, lease state, an unresolved-actions flag, workflow state and an archived flag.
- **The board stores more than the page shows.** Each task keeps a title, reports, messages, the time and author of its last update, its actions with their reports, and a workflow with blockers and criteria. The first cut is therefore a wider feed row and a new row layout. Workers report nothing new.
- **A blocker has an ID, a kind, a summary and a recheck condition.** It has no options and no recommendation.
- **A task has no parent field.** A task working under an approved goal carries `goalAuthorityRef`, which ties it to that goal and its owner. Nothing links any other delegated task to the task it came from.
- **Evidence is a free-text reference.** A criterion has an `evidenceRef` and an action has a report. No field holds a commit or a pull request.
- **Worker text is shown as plain text.** The waiting list never turns it into a link or markup, and the same care applies to anything new shown from a report.

## 1. Show what changed on each task

MindRoom’s compact cards show the latest activity, and a finished turn folds down to its conclusion. A CoCern task row could use the same idea to surface the task’s title, the latest reported milestone, its time and the next actor when known. Expanding it would reveal the reports and evidence behind the summary. The first version could use existing task updates and action reports, keeping the reporting burden on workers the same. [Compact cards and folded replies](https://www.nijho.lt/post/orchestrating-orchestrators/#hiding-what-you-dont-need-to-see) · [CoCern task workflow](reference.md#workflow)

For example, a task titled *Fix refresh timeout* could read “Claude found a reconnect issue in review. Codex is addressing it.” Expanding the row would show the review report itself.

## 2. Show delegated work under the Manager’s task

Nijholt can open a parent thread and see which subagents are working on what. In CoCern, an expandable list of assignments would answer a common Manager question at a glance. If Claude has returned a review and Codex is still investigating, show both assignments, their workers and the time of each worker’s last report. A quiet parent chat can then be understood from the work happening underneath it. [Subagent visibility in MindRoom](https://www.nijho.lt/post/orchestrating-orchestrators/#orchestrating-orchestrators)

Record an explicit parent link when an assignment is delegated. CoCern’s existing `dependsOn` field controls when a task becomes eligible for work. Two tasks can depend on the same prerequisite and still belong to different Managers. Tasks under an approved goal can already be grouped by their `goalAuthorityRef`, so the new link is needed only for delegation outside a goal. [Task dependencies and claims](reference.md#workflow)

## 3. Put the actual choice on “Waiting on you”

The article shows an agent bringing a disagreement to its operator with alternatives and a recommendation. A CoCern human-decision row could show the question, the options, the worker’s recommendation and what the answer unblocks. This needs an additive list of labeled options, one marked as recommended, on a `human_decision` blocker. Standing proposals would continue through the existing flow that presents the full terms and records the operator’s answer. [Decisions in MindRoom](https://www.nijho.lt/post/orchestrating-orchestrators/#tasks-that-run-for-a-day) · [CoCern decision handling](chat-decisions.md)

Keep the options as plain text. Screenshots from a worker would be a new kind of untrusted content on the dashboard and deserve their own decision.

A further step is answering from the row. Nijholt answers with one click. Today the row links to the chat, and Paul answers there. A click by the signed-in viewer could deliver the chosen option to the owning chat as a message, recorded the way a Standing answer is. The chat would still clear its own blocker with recheck evidence.

Preserve the meaning of CoCern’s waiting count. A completed review is an outcome to show on its task. If the reviewer identifies a choice only Paul can make, an explicit human blocker puts that task in **Waiting on you**. Pending Standing proposals also belong there. This keeps the count useful when several workers finish at once. [Waiting on you](reference.md#waiting-on-you)

## 4. Link each review to the revision it covered

Nijholt links pull requests to threads and uses fresh reviewers after fixes. CoCern could put the PR link, reviewed commit, reviewer and conclusion together on the task. If a subsequent fix lands, the row could say “Claude reviewed `abc123`. One newer commit.” The Manager could open the intervening change and decide whether a further source review is warranted. [MindRoom’s review workflow](https://www.nijho.lt/post/orchestrating-orchestrators/#orchestrating-orchestrators) · [CoCern repository review rules](../AGENTS.md)

This needs a structured commit and PR field on a review report. Counting newer commits looks feasible for any repository the hub already reads for the document cache, since it has a local clone to ask.

## Beyond the dashboard

These four came from the same article and need more than a page change.

- **5. Tell a working chat from a stalled one.** Nijholt’s room counts a thread with a running sub-agent or background job as Working. The hub records no model turn, so a busy chat and a stalled one look the same. The Claude hooks CoCern already installs could report turn start and turn end. Whether Codex has an equivalent signal is not established. This status must stay separate from the waiting count: a chat that finished is not a decision for Paul. [Sections in MindRoom](https://www.nijho.lt/post/orchestrating-orchestrators/)
- **6. Ask a peer on another client before asking Paul.** Nijholt’s agents consult a different vendor’s model on an ambiguity, and only what stays unresolved reaches him. CoCern already joins Claude and Codex chats, and `peers` lists each chat’s client. A standing line or a workflow-skill step could require that consultation before a `human_decision` blocker is raised, with the peer’s answer attached. This may need no hub code.
- **7. Keep a review away from its author’s client.** His rule is that no model reviews its own code, and a fresh reviewer reads the full diff after each fix round. Orders can already `offer` a task to a named session. A review offer could exclude the author’s client and remember who reviewed the previous round.
- **8. Turn an outside error into a task.** His error plugin posts failures into a room, where an agent triages and opens a fix. CoCern’s orders trigger only on workflow and claim events. An authenticated inbound event could create a task and offer it to a chat. What the opt-in `event_wait` tool already covers was not checked.

## Not taken

- **Interactive HTML canvases.** They conflict with showing worker text as plain text. Labeled options in idea 3 are the safe subset.
- **Dropping the vendors’ own harnesses.** Bridging those apps is CoCern’s premise. His measurement that a minimal agent used 58–76% fewer tokens does support the on-demand tools work already shipped.
- **His test-first skill.** It conflicts with the repository’s no-TDD rule.

## Suggested order

1. The first cut below.
2. Options and a recommendation on human-decision rows, then answering from the row.
3. Reviews tied to their commit.
4. Delegated work under the Manager’s task.
5. The four ideas beyond the dashboard, as separate decisions.

## First cut and success criterion

Widen `DashboardBoardRow` with the task’s title, the time and author of its last update, its latest report line and its top blocker kind. Show them on the existing Tasks page, with an expandable row for the reports behind the summary. Show report text as plain text; a link built from worker text is a separate decision.

Start with a task that has already passed between two workers. After an hour away, can the operator identify which task needs a decision, explain why, and open its supporting evidence in one click?
