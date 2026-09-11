# Flow Weaver Backend

ASP.NET Core 10 backend for Flow Weaver — a network-automation platform. It manages device inventory, credentials (encrypted at rest), reusable snippets, DAG workflows and their runs, and a tool-calling AI assistant. The assistant is reachable both from the web app and from **bilateral messaging channels** (Slack / Telegram / WhatsApp / Teams), with role-based access control enforced identically across every transport.

---

## Architecture

### Models vs DTOs

- **Models** (`Database/Models/`): EF Core entities. Used only for persistence. They do **not** carry `[JsonPropertyName]` attributes because they are never serialized to HTTP responses directly.
- **DTOs** (`Database/DTos/<Entity>/`): the HTTP contract. Every inbound/outbound payload goes through a DTO. DTOs own all `[JsonPropertyName]`, validation attributes, and snake_case JSON mapping.

This separation keeps persistence concerns out of the API surface, prevents accidental leakage of sensitive fields (e.g. encrypted blobs), and lets the DB schema evolve independently from the public contract.

### Folder layout

```
Database/
  Models/
    BaseModel.cs           # IsActive, CreatedAt, UpdatedAt
    Device.cs
    InventorySource.cs
    Credential.cs          # stores encrypted secrets as byte[]
  DTos/
    Device/
      CreateDevice.cs
      UpdateDevice.cs
      DeleteDevice.cs
      ListDevice.cs
      DeviceResponse.cs
    InventorySource/
      CreateInventorySource.cs
      UpdateInventorySource.cs
      InventorySourceResponse.cs
    Credential/
      CreateCredential.cs
      UpdateCredential.cs
      CredentialResponse.cs    # public — no secrets
      CredentialFull.cs        # internal — decrypted secrets
      DeviceCredentials.cs     # runtime service payload
      DeviceInfo.cs            # job payload with embedded credentials
Services/
  Interfaces/
    Credential/
      ICredentialEncryptionService.cs
  Credential/
    CredentialEncryptionService.cs
```

---

## Messaging channels (Slack / Telegram / WhatsApp / Teams)

The AI assistant is reachable from external chat platforms. A user writes from their platform, the message enters through a webhook (or, for Slack, an outbound Socket Mode WebSocket), the agent runs over the existing `jobs` queue, and the reply returns to the same thread.

### Flow

```
inbound (webhook / socket)
  → MessagingIngestService   # verify signature, dedupe, backpressure, enqueue
  → job: agent_message       # shared `jobs` queue
  → MessagingJobProcessor    # runs the same tool-calling agent as web chat
  → job: messaging_send
  → IMessagingProvider.Send  # posts the reply back to the thread
```

### Key pieces

| Area | Type(s) |
|------|---------|
| Inbound endpoint | `MessagingWebhookController` (`POST /api/messaging/webhooks/{provider}/{channelId}`, anonymous, signature-verified) |
| Ingest | `MessagingIngestService` (`ReceiveCoreAsync` / `ReceiveVerifiedAsync`), modeled on `GitWebhookReceiver` |
| Providers | `IMessagingProvider` + `MessagingProviderResolver`; `SlackProvider`, `TelegramProvider`, `WhatsAppProvider`, `TeamsProvider` |
| Workers | `MessagingWorkerHostedService` (+ `MessagingJobProcessor`), `MessagingRetentionHostedService`, `SlackSocketModeHostedService`, `TeamsRelayHostedService` |
| Account linking | `MessagingLinkController` (`GET`/`POST /api/messaging/link/{token}[/confirm]`), `MessagingLinkService`, `MessagingLinkToken` |
| Admin CRUD | `MessagingChannelController` (`/api/messaging/channels`, admin-only) |
| Data | `MessagingChannel`, `MessagingIdentityLink`, `MessagingInboundEvent`, `MessagingDelivery`, `MessagingLinkToken` |
| Migrations | `Messaging_Initial`, `Messaging_SocketModeAppToken` |

### RBAC is identical across transports

A channel can only **restrict** privileges, never widen them. Every turn runs as the **real internal user** linked to the external identity (`MessagingIdentityLink.LinkedUserId`), never a fixed channel role; an unlinked sender is denied. The effective role is `MessagingRoles.Effective(userRole, channel.MaxRole)` = `min(userRole, MaxRole)`, bound onto the request scope so the existing `ToolDispatcher` gate (which reads the caller's roles) enforces it exactly as on the web. A viewer cannot run an admin tool from Slack any more than from the browser.

> **Granular RBAC (see `docs/permissions.md`).** The coarse 3-tier model is being replaced by capability-based **permission grants** (env/device/resource-conditioned), enforced identically on web (`[HasPermission]`), the agent (`ToolCapabilityMap`), and messaging — where `channel.MaxRole` becomes a **capability ceiling** that intersects the user's grants and rides the agent's HTTP self-call via a `cap_ceiling` claim. The application-wide `rbac_mode` (`legacy` default) gates the rollout; `admin` bypasses; everyone else is default-deny.

### Account linking (self-service)

The bot DMs a single-use, ~15-minute, hashed link token. The user opens `/link?token=…` authenticated and confirms; the token is resolved by hash and consumed atomically, and a wrong or expired one answers 404 exactly like an unknown one so nothing leaks about which tokens exist. The link is built from `Messaging:PublicBaseUrl` (env `MESSAGING_PUBLIC_BASE_URL`) — point it at the user-facing **frontend** URL.

### Reliability

- **Dedupe** — inbound events keyed by `channel + provider_event_id`.
- **At-most-once agent turn** — `MessagingInboundEvent` is the idempotency anchor; `TryClaimInboundForProcessingAsync` flips `queued → processing` atomically, so a reclaimed job skips instead of re-running the LLM.
- **Send retries** — `SendPayload.Attempt` up to `MaxSendAttempts`; **retention** sweeps old rows on a schedule.

### Slack Socket Mode (no public ingress)

For VPN-only deployments, Slack is reachable over an outbound WebSocket — no public webhook. Set the channel's encrypted `app_token` (`xapp-…`, scope `connections:write`); `SlackSocketModeHostedService` reconciles enabled channels, opens one WSS per channel via `apps.connections.open`, and acks each envelope.

> **Run the agent where the tools are.** `ToolRegistry` is populated only in the API (non-worker) process, so `MessagingWorkerHostedService` is registered only when `!WorkerOnly`. The shared `jobs` queue means a socket event the worker container receives is still answered by the API container, which holds the full tool set. (A turn answering "no tools enabled" is the symptom of the agent running where the registry is empty.)

### Microsoft Teams (Bot Framework)

Teams is the one provider that authenticates inbound traffic with a JWT rather than a shared secret, so `EncryptedSigningSecret` is unused for it and the admin form hides the field. The credential mapping is `bot_token` = the Entra **client secret**, `external_config.app_id` = the Azure Bot's **Microsoft App ID** (required — `MessagingChannelService.ValidateProviderConfig` refuses to save without it), `external_config.tenant_id` = optional, only for single-tenant / managed-identity bots.

`TeamsProvider.VerifyAsync` implements all of the Bot Connector auth requirements: issuer `https://api.botframework.com`, signature against the keys at `login.botframework.com/v1/.well-known/openidconfiguration`, lifetime, audience = `app_id`, **and** a `serviceurl` claim matching the activity's `serviceUrl`. That last one is load-bearing: the reply carries an AAD bearer token to that URL, so an otherwise-valid token for another deployment must not be able to redirect our credential (`SendAsync` additionally refuses any non-https `serviceUrl`). The issuer is the same for every bot app type — only the *outbound* token authority differs, and that is what `tenant_id` selects.

`ParseInbound` strips the bot's own `<at>…</at>` mention (Teams only delivers @mentioned messages in channels and group chats, and the markup is part of `text`), unwraps other mentions to their display name, HTML-decodes, and returns null when nothing is left. Activities whose `from.id` starts `28:` are bot-authored and dropped as a loop guard. `ExternalThreadId` is `"{serviceUrl}::{conversationId}"`; because Teams puts `;messageid=` in the conversation id of a threaded message, replying to it lands in the same thread. AAD client-credentials tokens are cached per channel + app id + secret digest until shortly before expiry, and evicted when the Connector answers 401/403.

#### Teams without public ingress (Azure Relay)

Teams has no Socket Mode: Azure Bot Service only ever POSTs to a messaging endpoint. `TeamsRelayHostedService` gets the same property from the other side — it opens an **outbound** Azure Relay Hybrid Connection control channel and the Relay forwards the Bot Framework's requests over it, so a VPN-only or NAT-ed deployment needs no ingress. Set the channel's encrypted `app_token` to a Hybrid Connection connection string with **Listen** rights (`…;EntityPath=<connection-name>`) and point the Azure Bot's messaging endpoint at `https://<namespace>.servicebus.windows.net/<connection-name>`. The reconcile/backoff structure mirrors `SlackSocketModeHostedService`.

> **The Relay is transport, not authentication.** Socket Mode events skip signature checks because the socket itself is the trust boundary (`ReceiveVerifiedAsync`). The relayed path does **not** get that treatment: the same Bot Framework POST arrives carrying the same JWT and goes through the ordinary `ReceiveAsync`, so issuer, audience, signature and the `serviceurl` claim are all still verified. Consequently the Hybrid Connection should be created with *Requires client authorization* **off** — with it on, the Relay consumes the request's `Authorization` header as its own SAS token and the JWT never reaches us. (Relay-level auth is still available via an `sb-hc-token` query parameter, which the Relay strips before forwarding.)

`EncryptedAppToken` is therefore the generic "no-public-ingress credential" for a channel, not a Slack-only field: `xapp-…` for Slack, a Relay connection string for Teams, unused for telegram/whatsapp.

---

## MCP servers (external tools)

Flow-weaver is an **MCP client**: register external Model Context Protocol servers (**Govern → MCP servers**, admin-only) and their tools become usable from the **agent** (`list_mcp_servers` / `discover_mcp_tools` / `call_mcp_tool`) and from a **workflow** (virtual `mcp_call` node) via one shared `McpToolExecutor`. Built on the official `ModelContextProtocol` SDK — connect-per-operation over remote Streamable HTTP, linked-CTS timeouts, SSRF-guarded (`IUrlGuard`), secrets encrypted at rest.

| Concern | Where |
|---|---|
| Data | `McpServer`, `McpTool` (migration `AddMcpServers`) |
| Core | `Services/Mcp/` — `IMcpClient`, `McpConnectionFactory`, `McpToolExecutor` |
| Auth | header (`none`/`api_key`/`bearer`/`headers`, encrypted) **and OAuth 2.1** — `McpOAuthService` (discovery + DCR + client-credentials + auth-code/PKCE), `McpTokenService` (lazy refresh), `McpOAuthFlowService` (`oauth/start` + anonymous `oauth/callback`, signed `state`) |
| Management | `McpServerService` + `McpServerController` (`api/mcp-servers`, `[HasPermission("mcpserver.*")]`) |
| Agent | 3 tools in `Services/Ai/Tools/Handlers/` + `Skills/mcp.md` |
| Workflow | `McpCallHandler` (`Type => "mcp_call"`, `RequiresCompensation`) |
| RBAC | caps `mcpserver.read/manage`, `mcp.read/execute`; `mcp.execute` conditionable by server **and** tool (`mcp_server`/`mcp_tool` dimensions) |

See **`docs/mcp.md`** for the full guide and `plan_mcp.md` for the design.

---

## Colour themes (user-authored)

Users build themes from **one base colour per palette** and the browser expands each into the 11 shades + contrast tokens the UI uses (`frontend/src/lib/theme/ramp.ts`, OKLCh interpolation). The backend only stores the bases, so the generation curve can change without rewriting saved rows. A theme may also carry **style settings** — corner roundness, interface scale, font choices, heading weight — that ride along in a second jsonb map.

| Concern | Where |
|---|---|
| Data | `Theme` (migrations `AddThemes`, `AddThemeSettings`) — `Colors` jsonb, `Settings` jsonb (nullable), `IsShared`, `OwnerUserId` |
| Management | `ThemeService` + `ThemeController` (`api/themes`, `[Authorize]` — **not** admin-gated) |
| Visibility | shared themes ∪ the caller's own; someone else's private theme 404s rather than 403s, so its existence never leaks |
| Privilege | only `is_shared` is admin-gated, in both directions — publishing repaints everyone's picker, unpublishing yanks a theme others are using |
| Audit | publish / unpublish / delete of a **shared** theme. Private themes are a personal preference and aren't logged |

Validation rejects unknown palette names and anything that isn't `#rrggbb` — the ramp generator parses exactly that, and a colour it can't read would save cleanly and then render as an unstyled theme. Settings are validated the same way: keys come from `Theme.SettingKeys`, numbers must sit inside their ranges (`roundness` 0–2, `ui_scale` 0.85–1.15, `heading_weight` 300–900) and the font keys come from closed vocabularies — a saved theme is injected as CSS into every reader's page, so free-form values are not an option. Both maps replace wholesale on update; an explicit `{}` clears the overrides. Deletes are soft so a stale id in someone's `localStorage` stays recoverable; the name frees up immediately.

See **`/docs/themes`** in the app for the user-facing guide.

---

## Network-enabled python snippets (interactive SSH)

By default a `python_snippet` runs in a sandbox with **no network** (`--unshare-all`) and a strict import allow-list. Some device operations need a live, *interactive* SSH session the plain `ssh` handler can't model — e.g. a password change that prompts for confirmation. For those, a snippet can be flagged **network-enabled** (`Snippet.NetworkEnabled`): the isolation is lifted for that one snippet so it can use `netmiko` / `paramiko`, while every other snippet stays fully isolated.

- **Admin-gated, end to end.** `SnippetService.GuardNetworkEnabled` rejects non-`python_snippet` types and non-admins, and blocks a non-admin from editing an already-network-enabled snippet at all. `CreateSnippet.NetworkEnabled` / `UpdateSnippet.NetworkEnabled` are `bool?` (the update applies only when non-null, so unrelated edits never flip it); `SnippetResponse.NetworkEnabled` is always serialized.
- **Scoped sandbox change.** `PythonHandler.BuildBwrap(scriptPath, networkEnabled)` adds `--share-net` and binds `/etc/resolv.conf` only when enabled; an extended `NetworkSafeModules` allow-list layers on top of `SafeModules` for that snippet. Process/CPU/memory limits and dangerous-builtin checks are unchanged. The `time` module is allowed so a script can pace itself between prompts.
- **Surfaces.** Admin-only toggle on `/snippets/{id}` and in the workflow editor's node dialog (Service tab). OpenAPI: `Specs/fw_snippets.yaml`. Agent guidance: `Skills/netmiko_snippets.md`. Migration: `Snippet_NetworkEnabled`.
- **The agent cannot set it.** `create_snippet` returns tier `human_only` for `network_enabled=true` (`IArgumentSensitiveTier`), so `ToolDispatcher` refuses the call regardless of the caller's role and tells the user to set the flag in the UI. The admin check above is necessary but not sufficient on its own: everything the agent reads is untrusted input (SSH output, a git file, an MCP tool result, a chat message), so an admin's chat session is exactly where a prompt injection would aim — and a role check doesn't help when the role is the vehicle. Setting the flag stays a thing a human does with their eyes on it.

> Never hard-code device passwords into snippet code. Use credential placeholders (`${secret:credential:<id>:password}`) so secrets stay encrypted and are injected at run time.

---

## python_snippet import allow-list & package install

By default a `python_snippet` may only import a fixed stdlib safe-list
(`PythonHandler.SafeModules`). An **admin** can extend that list at
`/admin/python-packages`; for a PyPI package the worker installs it automatically.
The allow-list only adds *what may be imported* — the sandbox is otherwise
unchanged (no network unless network-enabled; the `exec`/`eval`/`__import__`/`open`
bans always apply). Admin-only — the admin is the final barrier.

Enforcement is `deploy/python/flow_weaver_import_guard.py`, which `ast.parse`s the
snippet and walks the tree (imports, plus eval/exec/compile/open/getattr and the
`__builtins__`/`__subclasses__` escape ladder). It never executes the snippet, so it
runs before the sandbox is built — and it **fails closed**: missing, timing out or
answering unparseably means the snippet is refused outside Development. A prefix
match on source text is not enough here; `import<TAB>os` and `if 1: import os` are
valid Python that no string comparison catches. `PythonHandler.CheckDangerousCode`
survives as a cheap pre-filter only. See `docs/handlers/python_snippet.md`.

### Key pieces

| Area | Type(s) |
|------|---------|
| Data | `AllowedPythonModule` (`ImportName` ≠ `PipSpec`; `Source` stdlib\|pip; `Status` pending→installing→ready\|failed). Migration `AllowedPythonModule_Initial`, unique `(ImportName)` |
| Admin API | `AllowedPythonModuleController` (`/api/admin/python-modules`, `[Authorize(Policy="Admin")]`) + `AllowedPythonModuleService` |
| Import guard | `PythonHandler.CheckDangerousCode(script, networkEnabled, extraAllowed?)` merges the `ready` import names (cached 30 s in `IMemoryCache`) |
| Installer | `PythonPackageProvisionerHostedService` (worker only; atomic per-row claim) |
| Sandbox bind | `PythonHandler.BuildBwrap(scriptPath, networkEnabled, pkgDir)` |

### Install flow

```
admin adds module (API)        # status=pending (stdlib → ready at once)
  → PythonPackageProvisioner   # worker only — single writer to the shared volume
      pip install --target /app/pyenv/site [--only-binary :all:] <PipSpec>
      python3 -c "import <ImportName>"   # verify
  → status ready (+ version) | failed (+ error)
```

- The install runs **in the worker** (which has network) — never inside the bwrap
  sandbox. Wheels-only by default (`Python:PipOnlyBinary`, env
  `PYTHON_PIP_ONLY_BINARY`) so no sdist packaging code runs; flip to allow source.
- At run time the sandbox `--ro-bind`s `/app/pyenv/site` and prepends it to
  `PYTHONPATH`, so the freshly-installed package imports without network. Only
  `ready` modules pass the import guard.
- Deployment: Dockerfile creates `/app/pyenv` (owned by the non-root `app`);
  compose mounts a shared named volume `python_packages` on backend + worker.

---

## Native Slack snippet (`slack_message`)

`SlackHandler` (`Type = "slack_message"`) is a built-in step that posts to a
Slack channel via `chat.postMessage`. The bot token is a **deployment secret**
read from config — `Slack:BotToken` (env `SLACK_BOT_TOKEN`) — so it works like
`ping`/`rest_call` without an Integration. (The `integration_action` route, with
the token in an encrypted Integration, remains for multi-workspace setups.)

- **Input** (node `config_overrides`): `{ "channel": "#alerts" | "C…", "text":
  "…", "thread_ts"? }`. Token + `chat:write` scope on the deployment; the bot
  must be in the channel.
- **Output**: `{ ok, channel, ts, error, status_code }`. Slack returns HTTP 200
  even on failure, so `Success` follows the response `ok` field — branch
  downstream on `output.ok`, never the HTTP status. `DefaultIdempotency =
  NonReversible` (a sent message can't be un-sent).
- Seeded as a built-in by `DefaultSnippetsSeedService` (`target_mode=once`), so
  it shows up in `/snippets` and the workflow palette out of the box.

---

## SSH host-key pinning

Every SSH session checks the device's key against `Device.ExpectedSshHostKeyFingerprint` (`SHA256:<base64>`, what `ssh-keygen -lf` prints). A mismatch aborts the connection before any command runs — without it, an on-path attacker impersonating a device harvests the credential the handler is about to send.

- **Set it** on device create/update (`expected_ssh_host_key_fingerprint`) or in the device edit dialog. `SshHostKeyFingerprint.TryNormalize` accepts the value with or without the `SHA256:` prefix and with or without base64 padding, and stores the canonical form so it compares equal to what the runner computes (`_normalize_pin` in `flow_weaver_ssh_runner.py`). MD5 colon-hex fingerprints and truncated digests are **refused**: a short pin compares equal for keys that differ, which is the attack it exists to stop.
- **Trust on first use.** An unpinned device is pinned automatically by the first successful connect (`IDeviceRepository.TryPinHostKeyAsync` — a conditional `UPDATE … WHERE fingerprint IS NULL`, so concurrent steps can't race and an operator's deliberate pin is never overwritten). Exposure is therefore the first run, not every run.
- **`Ssh:AutoPinHostKeyOnFirstUse=false`** disables that for labs that legitimately re-key between runs. Each connect then logs `ssh.host_key.unpinned` naming the device — those devices stay open to key substitution until someone pins them by hand.
- **Rotation.** After a legitimate re-key, clear the field (send `""`); the next connect re-pins.

---

## Outbound requests & SSRF

`IUrlGuard` blocks loopback, RFC-1918, CGNAT (100.64/10), link-local + cloud metadata (169.254.169.254), the documentation/benchmarking ranges, multicast and reserved space, for IPv4 and IPv6 including IPv4-mapped forms — plus a scheme allowlist (http/https only). `Integration.AllowPrivateNetwork` / `McpServer.AllowPrivateNetwork` opens RFC-1918 for that one target and **never** opens loopback or the metadata IP.

Redirects are the other half, and they used to be the hole: `HttpClient` follows them by default, so one `302 Location: http://169.254.169.254/…` from an allowed host bypassed the guard at every call site simultaneously. Clients that fetch influenced URLs are registered with `AddGuardedHttpClient`, which forces `AllowAutoRedirect = false` **and** installs `SsrfGuardingRedirectHandler`:

- Follows redirects itself, running `IUrlGuard` on every hop, capped at 5.
- Strips `Authorization` / `X-API-Key` / `Cookie` on a cross-origin hop — otherwise answering 302 is enough to harvest an integration's bearer.
- Judges hops with the caller's own private-network policy, passed via `msg.WithPrivateNetworkPolicy(integration.AllowPrivateNetwork)`. It deliberately does **not** re-check the initial URL: each caller already validated that with the policy that belongs to it.

Guarded clients: `rest_call`, `integration` / `integration-insecure`, `mcp` / `mcp-insecure`. Residual risk: DNS rebinding between the check and the connect (`Security:AllowInternalUrls=true` disables the guard entirely — development only).

**Non-HTTP egress** (the SMTP relay behind `email_send`) uses `EnsureHostSafe(host)`, which applies the same address rules without a scheme. Do not fake a URL to reuse `EnsureSafe` — `EmailSender` used to pass a synthetic `smtp://host:port`, and it broke the moment the scheme allowlist landed.

---

## Webhook trigger targeting

A `webhook` trigger's caller authenticates with the trigger's shared secret, not a user session, and the run is enqueued straight through `IWorkflowExecutor` — so it never passes the environment/resource RBAC check a manual run does.

`WorkflowTrigger.AllowTargetOverride` (`allow_target_override`) therefore decides whether the delivery body may pick targets at all. **Default false**, which means a leaked webhook secret can only re-fire the workflow at the devices an operator already chose:

| `allow_target_override` | Trigger scope | Body `target_devices` / `target_pools` |
|---|---|---|
| `false` (default) | any | Ignored; the trigger's `target_devices` are used. Logged as `workflow_webhook.targets_ignored`. |
| `true` | `target_devices` set | Intersected with it — the caller may **narrow**, never extend. Pools are dropped (membership changes outside the request, so it can't be checked against the scope). |
| `true` | empty | Honoured as-is: the operator said this webhook picks its own targets. |

The payload always reaches the run as `input.webhook` regardless — this governs targeting only.

---

## Credential Encryption

Sensitive credential fields (`Password`, `PrivateKey`) are **never** stored in plaintext. They are encrypted at the application layer before being written to the database, and decrypted only when injected into runtime service handlers.

### Components

- **`ICredentialEncryptionService`** — contract: `Encrypt(string?) -> byte[]?` and `Decrypt(byte[]?) -> string?`. Nulls and empty strings round-trip as `null` so optional credentials stay `NULL` in DB.
- **`CredentialEncryptionService`** — implementation backed by ASP.NET Core Data Protection API (`IDataProtectionProvider`). Under the hood it uses AES-256-CBC with HMAC-SHA256 authentication.
- **Purpose string** (`flow-weaver.credentials.v1`) — isolates credential keys from any other Data Protection usage in the app. Bumping the version forces a re-key.

### What gets encrypted

| Field | Storage | Notes |
|-------|---------|-------|
| `Credential.EncryptedPassword` | `byte[]` | Set via `Encrypt(dto.Password)` on create/update |
| `Credential.EncryptedPrivateKey` | `byte[]` | Same |
| `Credential.Username` | `string` | Not sensitive; stored in clear |
| `Credential.Extra` | `JsonElement` (`jsonb`) | Not encrypted by default — audit fields before persisting |

### Usage example

```csharp
public class CredentialService
{
    private readonly ICredentialEncryptionService _encryption;
    private readonly AppDbContext _db;

    public CredentialService(ICredentialEncryptionService encryption, AppDbContext db)
    {
        _encryption = encryption;
        _db = db;
    }

    public async Task<CredentialResponse> CreateAsync(CreateCredential dto)
    {
        var entity = new Credential
        {
            CredentialId = Guid.NewGuid(),
            Name = dto.Name,
            Type = dto.Type,
            Username = dto.Username,
            EncryptedPassword = _encryption.Encrypt(dto.Password),
            EncryptedPrivateKey = _encryption.Encrypt(dto.PrivateKey),
            Extra = dto.Extra ?? default,
        };

        _db.Credentials.Add(entity);
        await _db.SaveChangesAsync();

        return new CredentialResponse
        {
            Id = entity.CredentialId,
            Name = entity.Name,
            Type = entity.Type,
            Username = entity.Username,
            Extra = entity.Extra,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        };
    }

    public async Task<CredentialFull?> GetForInjectionAsync(Guid id)
    {
        var cred = await _db.Credentials.FindAsync(id);
        if (cred is null) return null;

        return new CredentialFull
        {
            Id = cred.CredentialId,
            Name = cred.Name,
            Type = cred.Type,
            Username = cred.Username ?? string.Empty,
            Password = _encryption.Decrypt(cred.EncryptedPassword) ?? string.Empty,
            PrivateKey = _encryption.Decrypt(cred.EncryptedPrivateKey) ?? string.Empty,
            Extra = /* parse cred.Extra to Dictionary<string, object?> */ new(),
        };
    }
}
```

---

## Configuration

### `appsettings.json`

```json
{
  "DataProtection": {
    "KeyRingPath": "./keyring"
  }
}
```

- `KeyRingPath` — filesystem directory where Data Protection persists its key ring. Override per environment (dev/staging/prod).

### `Program.cs` wiring

Already configured:

```csharp
builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
    .SetApplicationName("flow-weaver-backend");

builder.Services.AddScoped<ICredentialEncryptionService, CredentialEncryptionService>();
```

---

## Production Hardening — Step by Step

Follow these steps **before** going to production. Skipping any of them defeats the encryption.

### 1. Keep the key ring out of source control

Add to the repo's `.gitignore`:

```
# Data Protection key ring — must NEVER be committed
keyring/
**/keyring/
```

Also exclude it from any Docker image, container registry, or CI artifact. If the keyring leaks, every encrypted credential in the DB is compromised.

### 2. Switch key storage off the local filesystem

Filesystem persistence is fine for development. In production, choose one of:

- **Azure**: `.PersistKeysToAzureBlobStorage(...)` + `.ProtectKeysWithAzureKeyVault(...)`
- **AWS**: store key ring in S3 with SSE-KMS, or use `Aws.DistributedCacheDataProtection` community package
- **Redis** (required for multi-instance): `.PersistKeysToStackExchangeRedis(...)` + `.ProtectKeysWithCertificate(...)`
- **Certificate-wrapped** (HSM-backed): `.ProtectKeysWithCertificate(cert)` where `cert` comes from an HSM or Key Vault

### 3. Back up the key ring

Treat the key ring with the same rigor as DB backups:

- Automated, encrypted off-site backup.
- Documented recovery runbook (who, how, from where).
- Periodically verify restore actually works.

**If the key ring is lost, all encrypted credentials become permanently unreadable.** There is no recovery.

### 4. Never delete old keys

Data Protection rotates keys automatically every 90 days. Old keys stay in the ring so that previously-encrypted data can still be decrypted. Do not manually prune the key ring.

### 5. Enforce TLS

`UseHttpsRedirection()` is already enabled. In production, terminate TLS at a trusted load balancer or reverse proxy and reject plaintext HTTP. Credentials travel in `POST` bodies — they must never hit the wire unencrypted.

### 6. Redact sensitive fields from logs

- Do **not** log `CreateCredential`, `UpdateCredential`, `CredentialFull`, or `DeviceCredentials` instances directly.
- The `ToString()` overrides on `CredentialFull` and `DeviceCredentials` already omit secrets — but structured logging frameworks (Serilog, etc.) will happily serialize every property. Configure destructuring policies to redact `Password`, `PrivateKey`, and any key in `Extra` that looks sensitive.
- Review request logging middleware: by default ASP.NET Core does not log bodies, but any third-party middleware that does must be configured to skip `/credentials` endpoints.

### 7. Lock down the `CredentialFull` path

- `CredentialFull` and `DeviceCredentials` carry decrypted secrets.
- These DTOs are for **internal service-to-service** or **in-process** consumption only.
- Never return them from a public controller action.
- If a downstream service needs them (e.g. a job worker), transport over mTLS or a signed internal channel.

### 8. Audit credential access — done

Every decryption through `SecretResolver` emits a `secret.access` row into
`trace_events`, carrying who (the user, or the bound automation identity),
when, which credential (source + id/name + field) and the correlation id that
ties it back to the run or request that asked for it. The value is never
recorded; `value_bytes` stands in, so a rotation is visible as a length change
without exposing anything. Failed resolutions are recorded too — a burst of
them is what a credential deleted mid-run, or a probe for secrets that don't
exist, looks like from outside.

It lands in `trace_events` rather than `audit_logs` deliberately: this is a
read, it happens once per step per device (volume is run-shaped, not
admin-shaped), and it wants the retention sweeper. The *mutation* trail for the
same credentials — created, rotated, deleted — is in `audit_logs`, which is
append-only and kept forever by default.

Query it via `GET /api/admin/traces?action=secret.access`.

### 9. Authorization on credential endpoints

- `POST /credentials` and `PUT /credentials/{id}` — admin-only.
- `GET /credentials/{id}` — returns `CredentialResponse` (no secrets), authenticated users with the right role.
- Any endpoint that exposes `CredentialFull` must be gated by a distinct, stricter policy (e.g. service-account-only).

### 10. Tests to add

- **Round-trip**: encrypt a known plaintext, decrypt, assert equality.
- **Non-determinism**: encrypt the same plaintext twice, assert the two ciphertexts differ (Data Protection includes a random IV).
- **Null handling**: `Encrypt(null)` and `Decrypt(null)` return `null`; `Encrypt("")` returns `null`.
- **Tamper detection**: flip a byte in a ciphertext, assert `Decrypt` throws (HMAC should catch it).
- **Response DTOs never contain secrets**: serialize `CredentialResponse` from a `Credential` that has encrypted fields, assert the JSON has no `password` or `private_key` key.

---

## Next Steps

In rough order:

1. Add a DB context (`AppDbContext`) with `DbSet<Device>`, `DbSet<InventorySource>`, `DbSet<Credential>`.
2. Install `EFCore.NamingConventions` and enable `UseSnakeCaseNamingConvention()` so DB columns are snake_case without `[Column]` attributes on every property.
3. Create the initial EF migration (`dotnet ef migrations add Initial`).
4. Wire up controllers for each entity: `DeviceController`, `InventorySourceController`, `CredentialController`.
5. Implement the credential service (`CredentialService`) using the example above.
6. ~~Add audit logging for credential reads.~~ Done — see **Production Hardening §8**.
7. Add integration tests covering the encryption round-trip against a real PostgreSQL instance.
8. Before deploying: complete every item in **Production Hardening** above.
