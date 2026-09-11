# `email_send` handler

Delivers one MIME message through a configured `EmailChannel` (an SMTP
relay registered at `/email`). Native handler — the credential lives in an
encrypted DB row, not in deployment config, so one deployment can hold
several relays and a step picks one.

| | |
|---|---|
| Snippet type | `email_send` |
| Idempotency floor | `NonReversible` — a delivered email cannot be recalled |
| Target mode | `once` (a send is not device-scoped) |
| Source | `Services/Worker/Handlers/EmailSendHandler.cs` → `Services/Email/EmailSender.cs` |
| Transport | MailKit (`SmtpClient`), 30 s socket timeout |
| Capabilities | `email.read` / `email.manage` / `email.send` |

## Split of responsibilities

Connection and credential are **channel** state; the message is **node**
state. That is deliberate: rotating a password, moving from a personal
Gmail to a corporate relay, or pointing staging at a catch-all mailbox
touches one row and no workflows.

| Channel (`/email`, admin) | Node (`config_overrides`, author) |
|---|---|
| `host`, `port`, `security` | `to`, `cc`, `bcc` |
| `username`, encrypted password | `subject`, `body`, `html` |
| `from_address`, `from_name`, `reply_to` | `attachments[]` |
| `allow_private_network`, `tls_skip_verify` | optional `from_*` / `reply_to` overrides |
| `is_default`, `enabled` | optional `channel_id` |

## Input

Read from the step's resolved `InputPayload`. A nested `input` object is
unwrapped, matching `python_snippet` and `slack_message`.

```json
{
  "channel_id": "…",
  "to": "a@x.com",
  "cc": ["b@x.com"],
  "bcc": "c@x.com",
  "subject": "…",
  "body": "plain text",
  "html": "<p>…</p>",
  "from_address": "…",
  "from_name": "…",
  "reply_to": "…",
  "attachments": [
    { "file_name": "r.pdf", "content_base64": "…", "content_type": "application/pdf" }
  ]
}
```

Required: at least one recipient across `to`/`cc`/`bcc`, a `subject`, and
at least one of `body`/`html`. Everything else is optional.

`to`/`cc`/`bcc` each accept a single address, a comma-separated string, or
an array of strings — hand-authored nodes write one address, nodes fed from
an upstream fan-out write arrays, and both are normal.

`html` alone gets a generated `text/plain` alternative (tags stripped,
entities decoded). Many relays and spam filters penalise HTML-only mail.

Attachments take base64, which is exactly what an upstream `report` step
produces. `content_type` defaults to `application/octet-stream`.

## Channel resolution

1. `channel_id` present → `FindEnabledByIdAsync`. A disabled or
   soft-deleted channel **fails the step**; it is not silently swapped.
2. `channel_id` absent → `FindDefaultAsync` (the single `IsDefault`
   + `Enabled` row).
3. Neither → the step fails with a pointer to `/email`.

The handler never guesses "the only channel". A deployment with one
non-default relay fails loudly rather than sending corporate mail through
whatever row happened to be created first.

`EmailChannelService` enforces the single-default invariant: promoting a
channel demotes the previous one, and soft-deleting the default clears the
flag.

## Output

```json
{
  "ok": true,
  "channel_id": "…", "channel_name": "…", "provider": "gmail",
  "message_id": "<…@…>",
  "recipients": 3,
  "elapsed_ms": 412,
  "error": null
}
```

`Success` mirrors `ok`. Branch on `{{ steps.X.output.ok }}`.

## Security

### SSRF

An admin-supplied SMTP host is user-supplied egress, exactly like an
`Integration` base URL. Without a guard, "send a test mail" is a port
scanner against the cluster's private network. `EmailSender` synthesises
`smtp://<host>:<port>` and runs it through `IUrlGuard.EnsureSafe` —
`UrlGuard` only reads host/port, so the scheme is irrelevant.

`EmailChannel.AllowPrivateNetwork` relaxes the RFC-1918 check **only**,
mirroring `Integration.AllowPrivateNetwork`; loopback and
`169.254.169.254` stay blocked regardless. Self-hosted relays are almost
always on private ranges, so this flag is the normal case for
`provider: smtp` and should never be set on a public provider.

### TLS

`security` maps to a MailKit `SecureSocketOptions`:

| `security` | Option | Port |
|---|---|---|
| `starttls` | `StartTls` | 587 |
| `ssl` | `SslOnConnect` | 465 |
| `none` | `None` | 25 |
| anything else / null | `StartTls` | — |

The fallback is `StartTls`, **not** `StartTlsWhenAvailable`: the
"when available" variant lets a MITM strip the STARTTLS advertisement and
collect the password in the clear. An unrecognised mode fails closed.

`TlsSkipVerify` installs an accept-everything certificate callback. It
exists for an internal relay with a self-signed certificate; on a public
provider it removes the guarantee that you are talking to the real server.

### Credentials

- The password is encrypted with `ICredentialEncryptionService`
  (DataProtection, purpose `flow-weaver.credentials.v1`) and decrypted
  only inside `EmailSender.SendAsync`, immediately before
  `AuthenticateAsync`.
- Responses expose `has_password`, never the value.
  `EmailChannelResponse` has no password member at all.
- `EmailChannelService` refuses `security: none` together with a username
  unless `allow_private_network` is also set — otherwise the credential
  would go out in the clear to a public host
  (`ValidationException`, code `plaintext_auth_refused`).
- An empty username means no SMTP AUTH, which is legitimate for an
  unauthenticated internal relay. A username without a password is not
  (`password_required`).

### What reaches logs, traces and audit

- `SnippetResult.Logs` records the relay and the recipient **count** —
  never the addresses. Step logs land in traces.
- Log lines carry channel id, provider, host, port and security. Never a
  decrypted password.
- Create / update / delete / test are audited with the channel's posture
  (host, port, security, sender, the private-network and TLS flags) and a
  `password_rotated` boolean. Never the secret.
- A test send audits `{ ok, host, error }` — not the message body.

## Failure modes

`Describe()` prefixes MailKit's message with the stage that failed, because
the raw message rarely says which one it was.

| Error text | Cause |
|---|---|
| `SMTP authentication failed` | Wrong credential, or the provider wants an app password / API key instead of the account password. |
| `TLS handshake failed` | `security` does not match the port. |
| `cannot reach the SMTP server` | Wrong host, or egress blocked (port 25 is commonly blocked). |
| `SMTP server timed out after 30s` | Relay accepted the connection and stalled. |
| `SMTP command rejected (<status>)` | Server accepted the session but refused the message — usually a `From` the account does not own. |
| `SSRF blocked: …` | Host resolves to a private/loopback address; see above. |

Only `OperationCanceledException` propagates (the worker owns
cancellation). Every transport exception becomes a failed `SnippetResult`,
so a bad relay fails one step instead of crashing the worker.

## Provider notes

`Services/Email/EmailProviderPreset.cs` holds the connection defaults and
the per-provider credential hints the UI renders. The preset only seeds the
form — the row always stores the effective host/port/security, so retuning
a preset later cannot silently repoint existing channels.

| Provider | Host | Gotcha |
|---|---|---|
| `gmail` | `smtp.gmail.com` | Needs a 16-char App Password; the account password is rejected. Requires 2-Step Verification. |
| `outlook365` | `smtp.office365.com` | SMTP AUTH must be enabled for the mailbox in Exchange Online — off by default on new tenants. |
| `sendgrid` | `smtp.sendgrid.net` | Username is always the literal `apikey` (`FixedUsername`, enforced server-side). |
| `ses` | `email-smtp.<region>.amazonaws.com` | Region-specific; the preset is `us-east-1`. Credentials are IAM **SMTP** credentials, not an access key pair. |
| `mailgun` | `smtp.mailgun.org` | Credentials come from the domain's SMTP page. |
| `smtp` | — | Nothing pre-filled. Usually pairs with `allow_private_network`. |

## Relationship to `fw_email`

An external SMTP microservice (spec `Specs/fw_email.yaml`, skill
`Skills/email.md`) predates this handler and still serves the **chat**
agent through `execute_operation`. Workflows should use `email_send`:
no external dependency, credentials encrypted in-platform, and the SSRF /
TLS / redaction guarantees above.

`WorkflowReferenceValidator`'s intent heuristic accepts either — a
workflow whose description mentions email needs an `email_send` **or** an
`integration_action` node, and warns when it has neither.
