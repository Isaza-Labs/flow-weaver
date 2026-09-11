# Skill: generating reports

The `generate_report` tool materializes structured data into a downloadable file. Every generated report is persisted with full audit (who, when, from which conversation, with what prompt) — admins can review them in `/admin/artifacts`.

## When to use

Produce a report whenever the user asks for:

- **"Export / download"** anything — runs, devices, audits, inventory diffs.
- **A timestamped snapshot** for stakeholders, change-windows, or post-mortems.
- **A file they can forward** (email, Slack attachment, printed handover).

Don't generate a report just because you have tabular data — only when the user explicitly wants a file. For "show me" requests, a markdown table in chat is enough.

## Format guide

| Format | Best for |
|---|---|
| `pdf`  | executive summaries, handovers, print-ready output, PRs/RFCs |
| `xlsx` | large tabular data the user will want to filter/sort (>50 rows) |
| `csv`  | piping into another tool, importing elsewhere, minimal size |
| `html` | quick in-browser view of the same rich layout as the PDF |

Default to `pdf` when the user doesn't specify — it's the most universal. Switch to `xlsx` if the rows go past ~50 or the user mentions "filter" / "spreadsheet".

## Document structure

```json
{
  "format": "pdf",
  "document": {
    "title": "Inventory Sync — 2026-04-21",
    "subtitle": "NetBox ↔ Infoblox reconciliation",
    "badge": "Scheduled run",
    "generated_by": "FlowWeaver Agent",
    "stats": [
      { "label": "Devices scanned", "value": "128", "tone": "accent" },
      { "label": "Missing DNS", "value": "7", "tone": "high" },
      { "label": "In sync", "value": "121", "tone": "ok" }
    ],
    "sections": [
      {
        "title": "Delta",
        "description": "Devices present in NetBox but missing from Infoblox.",
        "category": "critical",
        "tables": [
          {
            "caption": "Missing DNS records (7)",
            "headers": ["Hostname", "Primary IP", "Status"],
            "rows": [
              ["rtr-bog-01", "10.0.0.1", "Missing DNS"]
            ],
            "severity_columns": { "2": "high" }
          }
        ],
        "callouts": [
          { "title": "Next step", "body": "Run push-dns workflow to create host records.", "tone": "info" }
        ]
      }
    ]
  }
}
```

### Tones

- Stats: `critical`, `high`, `medium`, `low`, `ok`, `accent`, `neutral` — map to the color of the big number.
- Severity columns: `critical`, `high`, `medium`, `low`, `ok` — paint the cell background/text.
- Callouts: `info`, `warn`, `danger`, `success`.
- Section category: `critical`, `api`, `ai`, `engine`, `store`, `worker`, `scheduler`, `frontend` — tints the section header.

## Hard rules

1. **Never include secrets.** No passwords, API keys, tokens, JWTs, bearer headers. Report body is persisted; even though prompts are redacted + encrypted, the document itself is not. Reference items by name/id, never by credential.
2. **Cap rows at 10 000 per table.** If the data is bigger, paginate or filter before calling the tool. The service will reject anything larger with an error.
3. **Attribute generation context in the title or badge** (date, environment, scope) so the artifact is self-describing when someone opens it six months later from `/admin/artifacts`.
4. **Return the tool result unaltered to the user.** The response contains `base64` + `filename` + `content_type` + `report_artifact_id` — the chat UI renders a download link automatically; do not paraphrase the bytes.

## Multi-device reports inside a workflow

The `report` snippet runs with `target_mode = once` (see `workflows.md`
for the full rule set). Its `rows` array is a **fixed list** — the
handler does NOT loop over an upstream per-device array to produce
rows automatically.

When the source data is a `per_device` step upstream, its output is
delivered to the `once` report as an aggregated envelope:

```
{{ steps.<per-device-node>.output }} →
  { "devices": [ <per-device-output-1>, <per-device-output-2>, … ] }
```

Three correct ways to build the rows depending on how many devices
you know about:

1. **Known device count at design time** — enumerate the indexes
   explicitly:

   ```json
   "rows": [
     ["{{ steps.lldp-sync.output.devices[0].device }}", "{{ steps.lldp-sync.output.devices[0].success_count }}"],
     ["{{ steps.lldp-sync.output.devices[1].device }}", "{{ steps.lldp-sync.output.devices[1].success_count }}"],
     ["{{ steps.lldp-sync.output.devices[2].device }}", "{{ steps.lldp-sync.output.devices[2].success_count }}"]
   ]
   ```

2. **Dynamic device count** — insert a `transform` (JMESPath) node
   between the producer and the report that flattens
   `devices[*].{ device: device, interface: updated[0].interface, … }`
   into a row-shaped array, then reference the transformed output from
   the report node.

3. **One artifact per device is actually what you want** — flip the
   `report` step to `target_mode = per_device` (overrides the snippet
   default). Each device gets its own file; downstream email/attach
   steps must also be `per_device` or expect the envelope.

**Anti-pattern:** writing `{{ steps.X.output.device }}` (singular) in
a once-mode report whose upstream was per-device. The resolver leaves
the template literal because the aggregated envelope has no `.device`
at the root — you'll see `{{ steps.X.output.device }}` printed in the
final PDF/HTML verbatim. The only fix is to rewrite the template with
`devices[N]` indexing or flatten via a `transform`.

## Follow-up patterns

- After a successful generation, briefly tell the user what you produced and its filename.
- If the user asks to share via email, pair with `fw_email:send_with_attachment` (see `email.md`). Reference the file as `${report:<report_artifact_id>}` in the attachment's `content_base64` — the backend splices in the bytes server-side, so you never copy the base64 blob (which corrupts on long files). Never paste the raw `base64` into the attachment.
- **Multiple formats in one message:** call `generate_report` once per format, then attach every `report_artifact_id` as its own `attachments[]` entry in a single send. Don't make the user choose one format or send several emails.
- If the user asks "where's my last report?" or "who generated X?", direct them to `/admin/artifacts` (admins) — the audit log there has everything you've generated.
