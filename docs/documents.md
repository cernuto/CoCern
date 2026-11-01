# Documents

Search a repository's docs by section, instead of reading whole files. On by default.

It does nothing until a repository is configured. `Start-CoCern.ps1` configures the checkout it runs from as repository `cocern`, on its current branch. Pass `-NoDocs` to skip that.

## What it reads

| File | Split into |
| --- | --- |
| Markdown (`.md`) | Sections at headings. |
| Text (`.txt`) | Paragraphs. |
| OpenAPI, JSON or YAML | One section per operation and per schema. |

Other JSON and YAML files are skipped. So are files over 256 KB (8 MB for OpenAPI), symlinks, malformed files and files with too many sections. Doc text is untrusted data, never instructions.

## How sync works

- The hub picks up new commits by itself every few minutes (5 by default).
- `doc_sync` runs a sync now.
- It reads committed files only, with `git show`. It never reads or changes your working tree, so uncommitted edits are not searched.
- It follows the branch's commits on the remote. A clone with no remote is read from its local branch, without fetching.

## Tools

```json
{ "repositoryId": "cocern", "query": "POST /v1/orders", "spec": "orders-api" }
```

| Tool | Who | What it does |
| --- | --- | --- |
| `doc_search` | worker | Up to 5 matching sections (`limit` up to 10). Each has `path`, `anchor`, `heading`, `snippet`, `score`, `tokens` and `commit`. Optional `spec` and `path`. |
| `doc_section` | worker | One section by `path` and `anchor`, exactly. A long one comes in parts: call again with its `next`. |
| `doc_sync` | admin | Sync now. Needs a `requestId`. Once per 30 seconds; a retry with the same `requestId` gets the same reply. `branch` changes the branch the hub follows. |

- Replies stay under 2,000 tokens. Hits that don't fit are counted in `more`.
- A search that takes over 10 seconds returns what it has, with `partial: true`.
- The first search after some idle time can take a few seconds.
- `NoRepositoriesConfigured`: no repository is set up. `DocCacheUnavailable`: the cache is off, or this repository is not one of them.

## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:DocCache:Enabled` | `true` | `false` turns the cache off. |
| `Relay:DocCache:SyncMinutes` | 5 | Minutes between syncs. `0`: only `doc_sync`. |
| `Relay:DocCache:IdleMinutes` | 30 | Minutes before an unused document leaves memory (2 to 1440). |
| `Relay:DocCache:MaxDocuments` | 500 | Documents per repository (up to 5,000). |
| `Relay:DocCache:MaxRepositoryBytes` | 128 MB | Text per repository (up to 1 GB). |
| `Relay:DocCache:Repositories:N:Id` | | The repository. |
| `...:ClonePath` | | Its local clone. |
| `...:Branch`, `...:Remote` | `main`, `origin` | What to follow. |
| `...:TokenFile` | | A read-only token for `git fetch`. Sent only to the remote's own HTTPS URL. |
| `...:Patterns:M:Glob`, `...:Patterns:M:Spec` | `**/*.md`, `**/*.txt`, `**/*.json`, `**/*.yaml`, `**/*.yml` as `docs` | Files to read, and their spec name. |
| `...:Exclude:M` | | More paths to leave out. `node_modules`, `bin`, `obj` and `.git` are always left out. |

Changing a repository's settings makes the next sync re-read it all, on the configured branch.
