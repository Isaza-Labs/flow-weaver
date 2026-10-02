# Skill: git — read, edit, commit, and push files in registered repositories

FlowWeaver can act on registered Git repositories. Common
use cases: pull a device config from a repo and push it via SSH; commit
a generated config back to a repo so a peer reviewer sees the diff; let
a workflow run dispatched by a `git` push update inventory data.

## Mental model — read this first

Three concepts the user will conflate, keep them straight:

| Concept | Where it lives | What it does |
|---|---|---|
| **Registered repo** (`GitRepository` row) | database | Pointer to a remote URL + credential. Has a stable `repository_id` (UUID). |
| **Local checkout** | `Git:Root` on the backend host | Clone the platform manages. Read/write goes here. The agent never sees a path; everything is by `repository_id`. |
| **Webhook** (`GitWebhook` row) | database | Inbound HTTP receiver that pulls and/or fires a workflow when the remote pushes. |

Critical rules:

- A repo must be **registered** before any tool works. Registration is a
  one-time setup the user does in **Build → Git repos**. The agent can
  only register a repo it creates: `git_create_remote_repository` creates
  a NEW GitHub repo with an HTTPS-PAT credential and auto-registers it.
- The `repository_id` you pass to every other tool is the UUID from
  `git_list_repositories` — NOT the URL, NOT the name.
- Auth is delegated to the **Credentials** catalog. HTTPS uses a
  `git_token`-typed credential (PAT in the password field). SSH uses a
  credential with `auth_method=key`. SSH keys with passphrases are NOT
  supported. The user picks a credential when registering the repo.

## Two surfaces — chat tools vs the `git` workflow node

There are **two** ways to act on a repo, and conflating them is the most
common design mistake:

| Surface | Where it runs | Use when |
|---|---|---|
| **`git_*` chat tools** (below) | the chat turn, driven by you | ad-hoc / interactive: the user asks you to read, edit, or create a repo right now in the conversation. |
| **`git` workflow node** (§ The `git` workflow node) | inside a workflow **run**, on the worker | the read/write must be part of an automated, repeatable, or scheduled pipeline — e.g. "every run, pull the config and commit a compliance report back". |

Same engine underneath (`IGitService`) — same auth, same semantics. So
**a workflow that needs to read or write Git does NOT bounce back to the
chat**: drop a `git` node into the DAG. Telling the user "the workflow
can't touch Git, I'll do the push myself" is wrong — that's exactly what
the `git` node exists for. (The `python_snippet` sandbox blocks
`open`/`requests`, but that's the snippet runtime, not the workflow: the
`git` node does the I/O for you.)

## Available tools

All tools accept a `repository_id` GUID as their first argument unless
noted otherwise.

| Tool | Tier | Purpose |
|---|---|---|
| `git_list_repositories` | autonomous | Discovery: returns id, name, URL, default_branch, last_fetched_at. Always call first when the user mentions a repo by name. |
| `git_create_remote_repository` | single_confirm | Create a NEW repo on GitHub via the REST API + auto-register it in FlowWeaver. Use this when the user says "create a repo" — the other tools only operate on repos that already exist. Returns the new GitHub URLs AND the FlowWeaver `repository_id` you pass to the next tool. |
| `git_list_files` | autonomous | List entries (files + dirs) at a path on a ref. Use `path=""` for the repo root. |
| `git_read_file` | autonomous | Read a file as UTF-8 text. Binary files return base64 with `is_binary=true`. Files >5 MiB are rejected. |
| `git_diff` | autonomous | Unified diff between two refs, OR (default) between HEAD and the working tree. Always run this BEFORE `git_write_file` if the user is about to overwrite. |
| `git_pull` | single_confirm | Fetch and fast-forward the working copy. Mutates local state but is reversible. |
| `git_write_file` | single_confirm | Create or overwrite a file AND commit. Optional `push=true`. |
| `git_commit_push` | single_confirm | Commit pending changes (after multiple writes). Optional `push=true`. |
| `git_list_webhooks` | autonomous | List inbound webhook receivers for a repo. |
| `git_create_webhook` | single_confirm | Register a new inbound webhook + return the public ingestion URL the user pastes into GitHub/GitLab. |

There is intentionally no `git_delete_webhook` or `git_force_push` — both
require human steps in the UI (revoke first on the provider, then delete
the registration; force-push needs an admin's hand on the wheel).

## Canonical recipes

### Recipe 0 — "Create a new repo named X (and put a file in it)"

Most-common ask in chat. Resolve the credential, create on GitHub +
auto-register in FlowWeaver, then write the file in the same plan.

1. `list_credentials` — find a credential whose `type` is `git_token`
   (or that the user named, e.g. "github credential"). Read-only,
   autonomous.
2. Propose ONE plan that does:
   - `git_create_remote_repository(name="<name>", auth_credential_id="<from step 1>")`
     — defaults: private=true, auto_init=true, branch=main, registers
     in FlowWeaver. Returns `repository_id`.
   - (optional, if the user asked to upload a file in the same turn)
     `git_write_file(repository_id="<from previous>", path, content,
     commit_message, branch="main", push=true)`.
3. Execute the batch. Surface `html_url`, `clone_url`, `repository_id`
   in the result.

If the user only gave a name and a credential reference (no URL, no
owner), DO NOT block on those — `git_create_remote_repository`
defaults the owner to the PAT's authenticated user and the URL is
returned by GitHub. The user shouldn't have to provide a URL for a
repo that doesn't exist yet.

If the create fails (`name already exists on this account`), tell the
user verbatim and ask whether to pick a different name or use the
existing one (`git_list_repositories` to check).

### Recipe 1 — "Show me what's in repo X"

```
1. git_list_repositories                 # find the id by name
2. git_list_files(repository_id, path="")
3. git_read_file(repository_id, path="…")  # for whichever the user asked
```

All autonomous → no confirmation needed.

### Recipe 2 — "Update file Y in repo X with this content"

This is the most common edit flow. NEVER skip the diff step.

> **Plan**
>
> I'll edit `<path>` in `<repo name>` (`<branch>`) and push:
>
> 1. `git_list_repositories` — resolve repo id (autonomous).
> 2. `git_read_file(repository_id, path)` — load current contents (autonomous).
> 3. `git_diff(repository_id, path)` — show the diff *vs* what I'd write
>    (autonomous; render in fenced ```diff block).
> 4. `git_write_file(repository_id, path, content, commit_message,
>    branch, push=true)` — single_confirm.
>
> **Risk:** writes to `<branch>`; `push=true` updates origin.
>
> Reply **"yes"** to execute.

When the user says yes, fire all four in one batch. The first three are
autonomous and can run before the write to populate the diff in your
proposal — but the WRITE is the only one that requires confirmation.

If `branch == "main"` or matches `release/*`, escalate to
**elevated_confirm**: name the impact in the Risk line ("pushes directly
to production-tracking branch — no PR review path"). If the user is
unhappy, propose a feature branch instead.

### Recipe 3 — "Apply this config from the repo to device D"

This bridges Git and SSH. Two halves:

```
git_read_file(repository_id, path) → returns content
                                         ↓
fw_workflows:create_workflow with an `ssh` node whose
config_overrides.command_list = each line of `content`
```

Confirm with the user that the config is the right one BEFORE creating
the workflow — `git_read_file` is autonomous so you can show them the
content first, then propose the workflow plan.

### Recipe 4 — "Commit a generated artifact back to the repo"

A `python_snippet` produces a JSON or YAML report. The user wants it
in the repo for review.

```
git_write_file(
  repository_id,
  path = "reports/<run-id>.json",
  content = <serialized output from previous step>,
  commit_message = "report: run <id> for <workflow>",
  branch = "reports",
  push = true,
)
```

Use a dedicated branch (`reports`, `audits`, `generated/<date>`) so the
audit trail doesn't clutter `main`. If the branch doesn't exist locally,
`git_write_file` creates it off the current HEAD — that's fine but
explain it in the plan ("creates branch if missing").

### Recipe 5 — "Set up a webhook so a push triggers workflow W"

> **Plan**
>
> 1. `git_list_repositories` — resolve repo id (autonomous).
> 2. `fw_workflows:list_workflows(name="<W>")` — resolve workflow id (autonomous).
> 3. `git_create_webhook(repository_id, name="<W>-on-push",
>    provider="github", on_push_workflow_id=<id>,
>    on_push_branches=["main"], auto_pull=true)` — single_confirm.
>
> **Output:** I'll show you the public URL + the secret string. You'll
> paste both into GitHub: **Repo settings → Webhooks → Add webhook →
> Content type `application/json`**, paste URL, paste secret, select
> **Just the push event**, save.
>
> Reply **"yes"** to register the webhook.

Always offer the manual paste step explicitly — the agent cannot reach
out to GitHub/GitLab to install the webhook on their side, only the
user can.

After the user confirms the GitHub side is done, suggest they push a
test commit and follow up with `git_list_webhooks` → "Recent
deliveries" in the UI to confirm the first delivery landed as
`dispatched`.

## The `git` workflow node — Git inside a run

To read or write a repo **as a step in a workflow** (not from chat), add a
node backed by the seeded `git` snippet (`list_snippets(type="git")`). It
runs on the worker and reuses the same engine as the tools above — so a
pipeline can read a repo, process, and commit a result back **without any
chat round-trip or manual push**.

`config_overrides` shape:

```json
{
  "operation":      "read_file | write_file | commit | pull | push",
  "repository_id":  "<uuid>",            // required — from git_list_repositories
  "path":           "configs/r1.cfg",    // read_file / write_file
  "ref":            "main",              // read_file (optional; defaults to HEAD)
  "content":        "{{ steps.build.output.content_json }}",  // write_file (templates expand first)
  "commit_message": "compliance for {{ device.name }}",       // write_file / commit
  "branch":         "main",              // write_file / commit / pull / push
  "push":           true                 // write_file / commit
}
```

Outputs:

- `read_file` → `{ path, ref, content, size, is_binary }`
- `write_file` / `commit` / `pull` / `push` → `{ ok, commit_sha, branch, message }`

Rules:

- **`target_mode` is a snippet property.** The seeded `git` snippet is
  `once` (repo-scoped). To write **one file per device** (e.g.
  `compliance_{{ device.name }}.json`), point the node at a `git` snippet
  whose `target_mode` is `per_device` — create one with
  `create_snippet(type="git", target_mode="per_device")`, same pattern as
  `integration_action_per_device`. Then template `path`/`content` with
  `{{ device.name }}` and each device iteration writes its own file. A
  `once` git node only sees `device.*` when the run targets exactly one
  device.
- **Save accepts templated paths — don't predict a phantom error.** The
  create/update validator does NOT reject `{{ device.name }}` or
  `{{ steps.X.output.Y }}` in a node's config: `WorkflowReferenceValidator`
  skips template-looking strings on purpose. An "unresolved template
  references" failure is the RUNTIME residual scan (a producer step emitted
  nothing for that path), never a save-time `schema_invalid`. Never abandon
  a templated path because you assume the save will reject it — save it,
  then fix at runtime if a reference truly resolves to nothing.
- **Idempotency**: `read_file` / `pull` are safe, but `write_file` /
  `commit` / `push` are **not** — the handler defaults to
  `RequiresCompensation`. In a multi-node DAG, draw a revert/cleanup edge
  on failure (the editor's promotion check surfaces this).
- **Auth lives on the registered repo**, not the node — never put secrets
  in `config_overrides`.

Canonical pipeline — the compliance / config-backup shape, all in one run:

```
[git read_file]   →   [python_snippet compare]   →   [git write_file push=true]
 reads <device>.json     builds the report           writes compliance_<device>.json
```

## Diff rendering

When you call `git_diff` and surface its output to the user, ALWAYS
wrap it in a fenced ```diff block so the chat UI highlights additions
and deletions:

````
```diff
- old line
+ new line
```
````

Truncate to ~200 lines with a "(truncated, X more lines)" note when the
diff is bigger. Big diffs are usually a sign the user should review in
the UI's file browser at `/integrations/git/{id}` — surface that link.

## Branch hygiene

- The user means **`<branch>`** literally. Don't normalize "main" to
  "master" or vice versa — `git_list_repositories` returns
  `default_branch`, use that as the default when the user doesn't say.
- If the user asks to write on a branch that doesn't exist locally,
  `git_write_file` will CREATE it off HEAD. Mention this in your plan
  so they know it's a new branch, not an edit on existing work.
- Never propose `git_pull` on a branch with uncommitted changes (the
  pull would attempt a merge and possibly conflict). If the working
  tree is dirty, propose `git_commit_push` first.

## Auth troubleshooting

| Error | Likely cause | Action |
|---|---|---|
| `auth_credential_id not found` | Credential was deleted or soft-deleted | Tell the user to recreate the credential and re-link the repo. |
| `ssh clone failed: Permission denied (publickey)` | Public key not added to the Git provider | Tell the user to add the public half of the credential to their GitHub/GitLab account. You can't read the key; tell them to extract from the credential or generate a new keypair. |
| `ssh clone failed: Host key verification failed` | StrictHostKeyChecking flagged a fingerprint change | Investigate; could be MITM or legitimate host migration. Don't auto-bypass. |
| `passphrase-protected SSH keys are not supported` | Credential's private key has a passphrase | Tell the user to re-import a key without a passphrase, or switch the repo to HTTPS+PAT. |
| `git operation failed: 403 Forbidden` (HTTPS) | PAT expired or lacks scope | Tell the user to rotate the PAT in `/credentials` with `repo` scope (GitHub) / `read_repository`+`write_repository` (GitLab). |

When you surface an error to the user, paste the verbatim message in a
fenced block — they'll forward it to whoever runs the Git provider.

## What NOT to do

- ❌ Hand-rolling `execute_operation` against the GitHub or GitLab
  REST APIs to do work that `git_*` tools cover. The internal tools
  are faster, authenticated for them already, and survive UI
  permission boundaries.
- ❌ Calling `git_pull` defensively before every read. The platform
  fetches on first access and on every push that lands via webhook —
  proactive pulls just slow the conversation.
- ❌ Suggesting `git_write_file` for a binary file. The tool sends
  UTF-8 only; binary writes corrupt content. Tell the user to commit
  binaries through their normal Git client.
- ❌ Inventing a `repository_id`. Always run `git_list_repositories`
  first if you don't have an exact UUID in the conversation context.
- ❌ Promoting webhook setup as fully automatic. The user MUST paste
  URL + secret into the provider's UI; FlowWeaver has no credentials to
  install webhooks remotely.
