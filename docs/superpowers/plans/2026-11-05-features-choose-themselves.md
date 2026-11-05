# Features choose themselves: the settings move

Part 10 of the auto-connect design. The first slice is built: a refusal says what's missing and how to get it, and `session_open` carries one line on the chat's role and what's worth using. Neither changes what a chat may do. This note covers the larger piece, moving `COCERN_ROLE` and `COCERN_PROFILE` to the hub per session. Nothing here is built or decided.

## Where things stand today

- `COCERN_ROLE` and `COCERN_PROFILE` are read once when the adapter starts (`AdapterOptions.FromEnvironment`). They decide which tools the adapter lists and forwards, nothing more.
- The authority is the credential. The hub checks `RelayCaller.IsAdministrator`, which comes from the token; a session's declared role, or being the recorded manager, grants nothing. An enrolled PC's node credential is never an administrator's, whatever `COCERN_ROLE` says.
- Setup and the dashboard write `COCERN_ROLE` into the client's settings (`ConnectCommand`, `DashboardConnect`, `connect_app` with `admin`). This is the part 9 setup choice that the move would replace, so the two should be sequenced together.

## What it would take

- **A per-session setting on the hub:** a recorded role or profile on the session, written by something other than the chat, surviving a reconnect and a hub restart, with a way to change it and an audit trail.
- **A read for the adapter:** it would learn the session's setting after `session_open` and rebuild its tool list and `IsAllowed` from it. It lists tools at connect, before any session exists, so it needs a defined list for that gap.
- **The startup line and refusals:** `ChatGuidance` would read the setting. The adapter's narrowing of the startup line could then go away.
- **Moving the writers:** setup, the dashboard and `connect_app` stop writing `COCERN_ROLE`, and existing client settings that carry it need a migration or a rule for which one wins.
- **Native chats:** their surface is fixed at registration and they never see a `session_open` reply, so they need their own route for both the line and the setting.

## What is unknown

- **How a new tool reaches a running chat.** A client may keep the tool list it loaded at connect. The development launcher already advertises `tools.listChanged` and sends `notifications/tools/list_changed` when it swaps its child, and in that test a running Claude chat kept the tool descriptions and server instructions it had loaded. That test changed guidance on existing tools. Nobody has tried adding a tool, so whether Claude Code then shows it to the model is unconfirmed. Codex is untested. The ordinary adapter doesn't advertise the capability today.
- **What to do if a client won't refresh.** If it doesn't, a chat gains a tool only by reconnecting or restarting, and the switches don't go away for that client.
- **Whether a chat can reconnect itself** without losing its turn, or whether only Paul can do it.

The first step is that experiment: a throwaway server that adds a tool mid-chat and sends the notification, tried in Claude Code and Codex, recording whether the model can call the new tool.

## What the owner must decide

- **Does a per-session setting carry authority, or only choose what's listed?** Today the token decides. If the setting can grant administrator tools, the hub's check changes from token to token plus session, and a worker's PC credential could gain power it can't have today. If it only chooses what's listed, a worker's credential gets the same refusals as now and the gain is smaller.
- **Who can change it, and how is Paul's yes recorded?** The safety rule says power comes only from Paul, and part 9 says the hub can't prove he was the one who said yes.
- **Does it replace the part 9 setup choice outright,** or do both exist for a while?
- **What should a client that can't refresh its list do:** reconnect, keep the settings in the app, or something else?
