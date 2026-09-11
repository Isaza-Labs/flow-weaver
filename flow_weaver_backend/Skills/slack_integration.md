# Sending Slack messages from a workflow

When the user wants a workflow to **post/send a message, notification, or alert
to Slack** (run results, warnings, "notify #channel", etc.), there is a built-in
snippet for exactly this — **use it; do not build a python/rest snippet.**

## Default: the native `slack_message` snippet

FlowWeaver ships a built-in snippet of type **`slack_message`** (seeded per
— it **already exists**, so reference it, don't create a new one). Find it
in the snippet list by type/name `slack_message` and add it as a workflow node.

- **Inputs** (the node's `config_overrides`):
  - `channel` — required. `"#alerts"` or a channel ID (`C0123…`).
  - `text` — required. Literal or a `{{ steps.<id>.output.<path> }}` reference.
  - `thread_ts` — optional, to reply in a thread.
- **Token**: read from deployment config (`SLACK_BOT_TOKEN`). You do NOT wire a
  token anywhere in the workflow. If it's unset the step fails with a clear
  message — tell the user an admin must set `SLACK_BOT_TOKEN` on the deployment
  (the bot needs the `chat:write` scope and must be a member of the channel).
- `target_mode` is `once` (posts one message; it is not device-scoped).

Example node config:

```json
{ "channel": "#alerts", "text": "{{ steps.detect.output.summary }}" }
```

**Output**: `{ ok, channel, ts, error, status_code }`. Slack returns HTTP 200
even on failure, so branch downstream on `{{ steps.<node>.output.ok }}` — NOT the
HTTP status. `ok:false` carries an `error` such as `channel_not_found`,
`not_in_channel`, or `invalid_auth`.

## Alternative: integration_action (multi-workspace / encrypted token)

Use this only when a single deployment env token doesn't fit — multiple Slack
workspaces, or the token must be encrypted at rest. Create a `generic_rest`
Integration (base URL `https://slack.com/api`, auth method **`bearer`** — NOT
`token`, which sends the "Token" prefix Slack rejects) with an uploaded OpenAPI
spec defining `POST /chat.postMessage`, then wire an `integration_action` node
with the same `channel`/`text` body. Here the result is under
`{{ steps.X.output.data.ok }}`. The bot token is a secret — have an admin enter
it in `/integrations`, never in chat.

## Notes

- The bot can only post to channels it is a member of.
- Never put the bot token in snippet code, a node body, or a chat message.
