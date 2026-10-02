# Skill: sending email

Use this whenever the user asks to "send", "mail", "notify", "deliver" or
"share" something, or when a step pushes an artifact out of the platform.

There are two paths, and picking the wrong one is the most common mistake:

| Situation | Path |
|---|---|
| **You are building a workflow** — the send happens later, when the workflow runs | an **`email_send` node** |
| **You are sending right now**, in this conversation | `execute_operation(fw_email:…)` |

## Building a workflow that emails — use the `email_send` node

`email_send` is a native handler. It delivers through an SMTP channel an
admin configured under `/email` (Gmail, Microsoft 365, SendGrid, SES,
Mailgun or a custom relay); host, port, TLS and the password live on that
channel and NEVER on the node.

Put `to` + `subject` + `body` and/or `html` in the node's
`config_overrides`, with `target_mode: "once"` (a send is not
device-scoped):

```json
{
  "to": "noc@example.com",
  "cc": ["oncall@example.com"],
  "subject": "Backup finished: {{ run.workflow_name }}",
  "body": "{{ steps.backup.output.summary }}",
  "attachments": [
    { "file_name": "report.pdf",
      "content_base64": "{{ steps.report.output.base64 }}",
      "content_type": "application/pdf" }
  ]
}
```

- `to` / `cc` / `bcc` each take one address, a comma-separated string, or
  an array.
- Omit `channel_id` to use the deployment's default channel. Only set it
  when the user names a specific relay.
- Supplying only `html` generates the plain-text alternative for you.
- Branch on `{{ steps.<node>.output.ok }}` — the step fails on an SMTP
  rejection, and `output.error` carries the server's reason.
- The send is **non-reversible**: promotion warns and rollback refuses.
  Put it last, after everything that can still fail.

If no channel is configured yet, say so — the workflow will fail at run
time with "no default email channel is configured", and an admin fixes it
at `/email`. Do not work around it with `python_snippet`.

### The `/email` section (email channels)

Where admins manage the SMTP relays that `email_send` delivers through.
When the user asks "how do I set up email?", "which relay is used?", or a
send fails with a channel error, point them here:

- One row per relay, with provider presets that pre-fill host/port/TLS:
  **Gmail / Google Workspace** (App Password required), **Outlook /
  Microsoft 365**, **SendGrid** (username is the literal `apikey`),
  **Amazon SES** (IAM SMTP credential), **Mailgun**, or **custom SMTP**.
- Exactly one channel can be the **default** — that's the one a node
  with no `channel_id` uses. Several channels can coexist (e.g. a
  transactional relay and a bulk one); a node picks a specific one by
  UUID.
- Credentials (password/API key) are encrypted at rest on the channel
  row and never appear in the API or on nodes.
- The section has a **test send** so the admin can validate a channel
  before workflows depend on it.

You have no tool to list channels — do not guess `channel_id` values.
Omit `channel_id` (default channel) unless the user gives you a specific
channel UUID; if they only know the channel's name, tell them to copy
its id from `/email`.

## Sending from chat — `fw_email`

The rest of this skill covers the conversational path: a SMTP microservice
(spec `fw_email`) reached through `execute_operation`.

## Pick the right endpoint

| User says | operationId |
|---|---|
| "send a quick note / notification" | `fw_email:send_email` |
| "send a nice/branded email" | `fw_email:send_html_email` |
| "email X and Y" / "notify the team" / "blast" | `fw_email:send_bulk_email` |
| "attach the CSV" (small text) | `fw_email:send_with_text_attachment` |
| "email me the PDF / report / Excel" | `fw_email:send_with_attachment` |

Default to `send_email` when the user doesn't specify; switch based on
the artifact.

## Canonical pairing — `generate_report` → `send_with_attachment`

The most common flow. Run both in one turn when the user says
"send me the report by email":

1. `generate_report({ format, document })` → returns
   `{ report_artifact_id, filename, content_type, base64, ... }`.
2. `execute_operation(fw_email:send_with_attachment, { ... })`:

```json
{
  "to": "<user email — ask if you don't know>",
  "subject": "<match the report title>",
  "body": "<one-sentence summary of what's attached>",
  "attachments": [{
    "filename": "<generate_report.filename>",
    "content_base64": "${report:<generate_report.report_artifact_id>}"
  }]
}
```

3. Confirm with the recipient echo the server returned.

**Reference the report by id, never paste the base64.** Put
`${report:<report_artifact_id>}` in `content_base64` — the backend expands
it to the file bytes server-side. Copying the raw `base64`
blob into the tool call is the #1 cause of corrupt/empty attachments: the
model truncates long base64. The reference is a short id, so it can't rot.

The attachment is the deliverable — don't paste the report content into
the body. Keep the body 1–3 sentences.

## Several formats in ONE email

When the user asks for "PDF and Excel", "all formats", or "csv + pdf",
send a single email with one attachment per format — do NOT send one
email per format, and do NOT force the user to pick one:

1. Call `generate_report` once per format (same `document`, different
   `format`). Each returns its own `report_artifact_id` + `filename`.
2. Call `send_with_attachment` ONCE, listing every artifact:

```json
{
  "to": "ops@example.com",
  "subject": "Inventory sync — 2026-07-16",
  "body": "Attached in PDF, Excel and CSV.",
  "attachments": [
    { "filename": "<pdf.filename>",  "content_base64": "${report:<pdf.report_artifact_id>}" },
    { "filename": "<xlsx.filename>", "content_base64": "${report:<xlsx.report_artifact_id>}" },
    { "filename": "<csv.filename>",  "content_base64": "${report:<csv.report_artifact_id>}" }
  ]
}
```

`content_base64` handles every format (pdf/xlsx/csv/html) — there is no
need to split binary vs text across two endpoints. Keep the combined
attachment size under ~10 MB (the SMTP relay's cap); warn the user and
drop the largest format if you'd exceed it.

## Base URL + auth

Reach it via `execute_operation` against `fw_email`. The base URL and
auth come from the Integration the `fw_email` spec is linked to (set on
the spec in `/ai/specs`; the Integration is configured in
`/integrations`), never from the prompt.

## Hard rules

1. **Ask for the recipient** if not supplied. Never guess from
   conversation or memory without confirmation.
2. **Never send secrets / passwords / API keys** — not in body,
   attachments, or subject.
3. **Body size** — plain-text bodies under ~2 KB. For long content
   switch to `send_html_email` or move it into an attachment.
4. **Attachment size** — upstream SMTP relay may reject >10 MB. Warn
   the user before attempting.
5. **HTML must be self-contained** — inline CSS only, no external
   `<link>` / `<script>`, no remote images (blocked or tracked).
6. **Bulk** — cap 50 recipients per call. Larger lists → batch or
   point the user at a mailing-list provider.
7. **After calling, surface**: recipients echoed by the API
   (may be normalized) + the artifact link if you ran `generate_report`.
8. **Never use `python_snippet` to send email.** In a workflow it goes
   through `email_send`; in chat through `execute_operation`. Those
   paths validate the payload, log the outbound attempt, and surface
   failures as step errors. A `python_snippet` body that does
   `set_output({"ok": true, "message": "email sent"})` is exactly
   the scaffold pattern `SnippetService` rejects (and even when it
   really does open an SMTP socket, you have duplicated the handler and
   skipped its validation). Need a custom transform pre-send? Use a
   `transform` snippet, then the `email_send` node.

## Embedding upstream output in the body

Anything user-facing inside `body` should be sanitised. SSH stdout is
already ANSI-stripped by the runner, but other text (CLI banners,
exception traces, `parsed[]` rendered as JSON) may still contain control
chars or be too long for an email body:

```json
"body": "Last command: {{ steps.ssh.output.results[0].output | strip | truncate(500) }}"
```

For per_device summary emails (the canonical use case after a fan-out),
prefer the envelope shortcuts over hard-coded `devices[N]` indices:

```
{{ steps.ssh.output.first_success.device_name }}
{{ steps.ssh.output.devices_by_name['router-1'].output.results[0].output | strip }}
{{ steps.ssh.output.success_count }} / {{ steps.ssh.output.total }}
```

If the summary is built by an upstream `python_snippet`, that snippet
must run in `target_mode: once` so its `inp['steps']['<ssh>']['output']`
is the full aggregate envelope (`devices[]`, `devices_by_name`, etc.).
A `per_device` aggregator gets its upstream auto-scoped to a single
device and never sees the envelope — that's the fan-out → empty-summary
trap.

## Examples

**Minimal notification**
```
User: notify ops@example.com that the sync finished
→ execute_operation(fw_email:send_email, {
    to: "ops@example.com",
    subject: "Sync finished",
    body: "NetBox↔Infoblox sync completed at 14:07 UTC: 121 in sync, 7 missing DNS."
  })
```

**Report delivery (the pairing flow)**
```
User: generate a PDF with the workflows and send it to ops@example.com
→ generate_report({ format: "pdf", document: { title: "Workflow list — ...", sections: [...] } })
→ tool_result: { report_artifact_id: "2f5b1c8e-...", filename: "workflow-list-20260421153000.pdf", ... }
→ execute_operation(fw_email:send_with_attachment, {
    to: "ops@example.com",
    subject: "Workflow list",
    body: "PDF attached with the current workflows.",
    attachments: [{ filename: "<filename>", content_base64: "${report:2f5b1c8e-...}" }]
  })
→ "Done — sent the PDF (workflow-list-20260421153000.pdf) to ops@example.com."
```

## What NOT to do

- Don't invent internal email aliases — confirm with the user.
- Don't retry on 422 with the same payload; surface the validation
  message so the user can fix it.
- Don't send the same report twice without an explicit ask.
- Don't paste `generate_report`'s `base64` into the attachment — reference
  it with `${report:<report_artifact_id>}` instead. The raw blob only
  belongs in the conversation as the download link the UI renders.
- Don't send one email per format when the user wants several — bundle
  them as multiple `attachments[]` in a single `send_with_attachment`.
- If a `${report:...}` reference errors as "could not be resolved", the
  artifact expired or the id is wrong — regenerate it, don't retry the
  same id.
