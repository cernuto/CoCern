# BlazorBlueprint dashboard redesign implementation plan

> **For agentic workers:** Use superpowers:subagent-driven-development with bounded file ownership. Paul's repository rules override TDD, per-task gates and broad test runs. Work in the existing Visual Studio checkout on codex/blueprint-dashboard; preserve the earlier fixes already present.

**Goal:** Rebuild the complete CoCern dashboard with BlazorBlueprint and Paul's attached cyan/blue coordination logo, then smoke-test and polish every route in the built-in browser.

**Architecture:** Keep the WebAssembly app, contracts, live stream and service logic. Introduce BlazorBlueprint.Components 4.1.0, a responsive sidebar shell, coherent light/dark theme, and actual Blueprint controls throughout the views. Use the existing running hub and database, with the sample records already seeded.

**Tech stack:** .NET 10 Blazor WebAssembly; BlazorBlueprint.Components 4.1.0 and its matching transitive primitives; existing SignalR and HTTP services.

**Spec:** Paul's current chat requests and attached codex-clipboard-4e3da226-d1c4-4c40-bc69-cf8aecf6b863.png. The supplied logo is copied to src/CoCern.Dashboard/wwwroot/cocern-logo.png.

## Global constraints

- Always use BOTH Roslyn CodeLens and Visual Studio MCP for C#/.NET; load the Roslyn skill before lookup and check both connections at each agent's start.
- Semantic tools replace C# symbol/usages grep and builds for diagnostics. Confirm newly added Razor members with search_symbols before trusting clean diagnostics.
- Browser work always uses the integrated browser through cua_repl. Parent owns browser, build, debugger, deployment and final integration.
- Use the existing database and Visual Studio app on http://127.0.0.1:5271/dashboard/. No new preview database. Existing sample data: four Sample sessions, three ui-annotation-5d77dad1 tasks and six messages.
- Preserve all nine navigation views plus Analytics/Test effort, session/map selection, traffic inspection, log filters, analytics filters, Standing/Manager actions, Connect, document expansion and reset confirmation/cancellation.
- Preserve automatic sign-in for genuine same-origin loopback browsers, remote authentication, and full coordination reset on next restart preserving credentials and PC enrollment/revocation.
- No TDD, unrelated tests, whole-suite/class runs, per-task builds or gates. Source-only reviews; select any tests through Roslyn first. Parent verifies at one significant integration milestone.
- Never build into running app output. Stop the verified VS app before producing its next binary; use VS MCP to build/relaunch.
- Do not commit, push, delete user data, change app connections, grant Standing or manager powers, or send messages to real chats as part of redesign.
- No AI coauthor attribution or hard-wrapped commit/PR paragraphs.

## Visual direction

- Responsive left sidebar on desktop; accessible navigation disclosure on mobile. Main content stays readable without horizontal page overflow at 390, 768, 1351 and 1440px.
- Use the supplied logo, not the earlier experimental marks. Palette: deep navy #081321, panels #0e1d30, borders #24364c, cyan #65ddee, blue #367cf5, legible muted text. Provide a coherent light appearance too.
- Use Segoe UI/compatible system sans. Clear 28–32px page titles, 14–15px body, restrained headings and 36–44px controls. Avoid giant promotional content, repeated technical disclaimers and decorative station artwork.
- Use actual BbButton, BbInput/BbNativeSelect, BbBadge, cards/tables/alerts and portal-backed overlays where appropriate. Preserve native form semantics, IDs, accessible labels and existing callbacks. Do not merely add a stylesheet to the old UI.
- Use the library's semantic variables. Existing --muted was a text token but Blueprint --muted is a background token; migrate legacy text rules to --muted-foreground.

## Tasks and ownership

### 1. Foundation, shell and theme

Owner files: Program.cs, _Imports.razor, App.razor, Pages/Dashboard.razor and .razor.cs, wwwroot/index.html, preferences.js, dashboard.css, new blueprint-theme.css. Parent has added the central package version and package reference; do not edit other view files.

- [ ] Register services and imports; load prebuilt CSS and required portal host. Align dark mode (.dark) with existing preferences and avoid competing theme stores.
- [ ] Build the responsive sidebar/topbar shell; real Blueprint buttons and notices; move nonessential repeated chrome out of individual view space while retaining every function.
- [ ] Style every legacy structural selector coherently; replace old decorative station/game presentation with compact professional workspace surfaces. Reserve the supplied logo asset for branding.
- [ ] Fix skip link so it keeps the route AND transfers keyboard focus. Keep all prior local-access/reset behavior.

### 2. Live operations views

Owner files: ActivityBoard, SessionsView, SessionList, SessionInspector, SessionGraph, TrafficLane, LogView, WaitingOnYou, WaitingBadge, WaitingNotifications (.razor); session-graph.css. Do not edit shared dashboard.css or shell files; add operations.css for owned overrides if necessary and tell parent to include it.

- [ ] Migrate controls and presentation to Blueprint; preserve live updates, map edges, filtering, selection, pause/resume, metadata privacy and empty states.
- [ ] Make overview/session lists compact and scannable, with truthful status labels, sensible truncation and fully usable keyboard controls.

### 3. Reports, administration and setup

Owner files: AnalyticsView, AnalyticsChart, TestEffortView/Card/Badge, SqlView, DatabaseResetView, ManagerControl(.razor only), StandingView, DecisionEvidence, ConnectView, AddPcView, DocCacheView, SignIn, CloseSessionsControl, CloseSessionControl (.razor); standing.css, documents.css. Do not edit backend service code or shell/shared CSS; add administration.css for owned overrides if needed and tell parent.

- [ ] Migrate controls, forms, panels and tables to Blueprint. Keep all endpoints, bindings, validation, confirmation and pending/uncertain states intact.
- [ ] Preserve destructive-action distinctions and exact reset phrase; ensure mobile forms wrap, long text fits, and alerts/errors remain visible.

### 4. Integration, browser smoke and nit passes

Parent owns asset copy, coordination, dependencies, integration and deployment.

- [ ] Review final diff/source and diagnostics, confirm all new Razor symbols resolve and the library is genuinely used across the app.
- [ ] Build the app only when implementation has accumulated; relaunch through VS against its original database.
- [ ] Integrated-browser smoke: fresh-host local access, all routes, session search/selection/map, traffic detail/pause, log search and empty state, analytics filtering, document expansion, reset form gating and cancellation if scheduled. No real manager/Standing/Connect mutations.
- [ ] Nit pass at mobile/tablet/desktop sizes: overflow, navigation, focus, labels, contrast, long fixture labels, portal overlays, theme switch and restoration. Fix concrete issues, then recheck only the affected interactions.
- [ ] Leave the actual app open for Paul's annotations. Report completed behavior and material unverified paths without claiming a test suite ran.

## Review focus

1. Native form attributes and callbacks must survive component substitution.
2. Library theme variables and portal appearance must agree with the custom shell.
3. Long session/repository labels must wrap or truncate with full accessible names.
4. Empty, stale, loading and disconnected states must remain truthful.
5. Reset/local-access security boundaries must remain unchanged by visual redesign.

## Progress

- Existing UI fixes, automatic local access, reset feature and two security/data-loss test sources are present before this redesign. Source review fixed enrollment LastSeen retention. No tests have run; prior targeted Roslyn test discovery returned no direct tests, while analyze_change_impact identifies the new reset test as a direct caller.
- Package dependency restored successfully. Main VS app currently runs updated pre-redesign source on 5271. The separate 5281 preview is stopped.
