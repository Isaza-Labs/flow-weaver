<script lang="ts">
  import DocLayout from '../../_components/DocLayout.svelte';
  import Callout from '../../_components/Callout.svelte';
</script>

<DocLayout
  title="Messaging channels"
  lead="Reach the assistant from Slack, Telegram, WhatsApp, or Microsoft Teams. A user writes from their platform, the message enters through a webhook (or an outbound WebSocket), the agent answers over the same job queue that powers chat, and the reply lands back in the thread."
>
  <Callout tone="where" title="Where to find it">
    Channels (admin): <a href="/admin/messaging-channels"><code>/admin/messaging-channels</code></a> ·
    Account linking (any user): the <code>/link?token=…</code> URL the bot DMs you.
    Sidebar → <strong>Integrate</strong> → <strong>Messaging channels</strong> (admin-only).
  </Callout>

  <section>
    <h2>What it is</h2>
    <p>
      A messaging channel binds one external workspace (a Slack app, a Telegram
      bot, a WhatsApp number, a Teams bot) to your assistant. Once
      configured, conversations flow both ways:
    </p>
    <ol>
      <li>A user sends a message from the external platform.</li>
      <li>
        It arrives at FlowWeaver — over a public <strong>webhook</strong>, or,
        for Slack, an outbound <strong>Socket Mode</strong> WebSocket — and is
        verified by the provider's signature, never by a JWT.
      </li>
      <li>
        The ingest layer records the inbound event, applies backpressure, and
        enqueues an <code>agent_message</code> job on the shared
        <code>jobs</code> queue. The HTTP call returns immediately.
      </li>
      <li>
        A worker picks up the job, runs the same tool-calling agent the web
        chat uses, and enqueues a <code>messaging_send</code> job with the
        reply.
      </li>
      <li>The provider posts the reply back into the originating thread.</li>
    </ol>
    <p>
      The conversation is stored with the provider as its source and the
      external thread id, so a follow-up continues the same thread instead of
      starting fresh.
    </p>
  </section>

  <section>
    <h2>Permissions are identical to the web</h2>
    <p>
      This is the core guarantee: <strong>a channel can only restrict
      privileges, never widen them.</strong> Talking to the agent from Slack
      grants you exactly what your FlowWeaver account already has — no more.
    </p>
    <dl>
      <dt>Linked user's role</dt>
      <dd>
        Every turn runs as the <strong>real internal user</strong> linked to
        the external identity, never a fixed "channel role". An unlinked
        sender never runs the agent: with <code>require_linked_user</code> on
        (the default) the bot answers with an account-linking prompt; with it
        off the message is rejected silently.
      </dd>
      <dt>Channel ceiling (max_role)</dt>
      <dd>
        The effective role is <code>min(userRole, max_role)</code>. A channel
        with <code>max_role: operator</code> means even an admin is capped at
        operator while talking through it; leaving it empty applies no ceiling
        (the user's own role wins). An unknown/typo'd ceiling is treated as
        "no ceiling" so a mistake never <em>grants</em> more.
      </dd>
      <dt>Same enforcement point</dt>
      <dd>
        The turn goes through the same tool permission check as the web chat.
        A viewer asking the
        Slack bot to run an admin-only tool gets the same 403 it would get in
        the web chat.
      </dd>
    </dl>
    <Callout tone="admin" title="No escalation by transport">
      There is no path by which arriving through a channel raises your
      privileges. The effective role is always the <em>more restrictive</em>
      of the two inputs.
    </Callout>
  </section>

  <section>
    <h2>Account linking (self-service)</h2>
    <p>
      The first time an unrecognized external user messages the bot, FlowWeaver
      issues a single-use, ~15-minute, hashed link token and the bot DMs a
      link. The flow is self-service — no admin has to wire identities by hand:
    </p>
    <ol>
      <li>
        The user opens the <code>/link?token=…</code> URL. The page is public
        so the token survives the sign-in redirect, but confirming requires
        being authenticated as a FlowWeaver user.
      </li>
      <li>
        A preview (<code>GET /api/messaging/link/{'{token}'}</code>) shows which
        provider, channel, and external user they are about to bind.
      </li>
      <li>
        On confirm (<code>POST /api/messaging/link/{'{token}'}/confirm</code>)
        the external identity is bound to <strong>their</strong> account. The
        token is consumed atomically so it cannot be reused.
      </li>
    </ol>
    <Callout tone="warning" title="The link needs a reachable base URL">
      The DM'd link is built from <code>Messaging:PublicBaseUrl</code> (env
      <code>MESSAGING_PUBLIC_BASE_URL</code>). Point it at the
      <strong>frontend</strong> URL your users actually reach (e.g. the
      VPN-internal address). If it is empty, the bot sends a bare
      <code>/link?token=…</code> with no host.
    </Callout>
  </section>

  <section>
    <h2>Creating a channel</h2>
    <p>
      Route: <code>/admin/messaging-channels</code> (admin-only). <em>New
      channel</em> opens a form; the fields map to the channel record:
    </p>
    <dl>
      <dt>provider</dt>
      <dd><code>slack</code>, <code>telegram</code>, <code>whatsapp</code>, or <code>teams</code>.</dd>
      <dt>name</dt>
      <dd>A label for the channel in the admin list.</dd>
      <dt>bot_token / signing_secret</dt>
      <dd>
        Provider credentials. Stored encrypted and never returned — the API
        only reports <code>has_bot_token</code> / <code>has_signing_secret</code>.
        Re-submitting a non-empty value rotates the secret; leaving it blank on
        edit keeps the current one. The form relabels both fields for the
        selected provider and hides the ones it does not use (Teams has no
        signing secret; only Slack has an app token).
      </dd>
      <dt>external_config</dt>
      <dd>
        Provider-specific non-secret settings. Teams <strong>requires</strong>
        <code>app_id</code> and optionally takes <code>tenant_id</code>;
        WhatsApp takes <code>phone_number_id</code> and
        <code>verify_token</code>; Slack and Telegram need nothing. The form
        renders these as labeled inputs for the selected provider — there is no
        JSON to hand-write — with a <em>JSON</em> tab as an escape hatch and an
        <em>Add field</em> editor for providers whose config is open-ended.
      </dd>
      <dt>app_token</dt>
      <dd>
        The opt-in credential for the provider's <strong>no-public-ingress</strong>
        mode — set it and the backend dials out instead of waiting on a webhook.
        Slack: the App-Level Token (<code>xapp-…</code>) that enables Socket
        Mode. Teams: an Azure Relay Hybrid Connection string (see below).
        Telegram and WhatsApp are webhook-only. Encrypted; surfaced as
        <code>has_app_token</code>.
      </dd>
      <dt>default_agent_id</dt>
      <dd>
        Which agent profile answers on this channel. Empty falls back to the
        default assistant (the one with the full built-in tool set).
      </dd>
      <dt>max_role</dt>
      <dd>The privilege ceiling described above. Empty = no ceiling.</dd>
      <dt>require_linked_user</dt>
      <dd>
        When on (default), an unlinked sender gets an account-linking prompt
        instead of an answer. When off, the message is rejected silently.
        Either way the agent never runs for an unlinked sender.
      </dd>
      <dt>allowed_external_ids</dt>
      <dd>Optional allow-list of external user IDs that may talk to the channel.</dd>
      <dt>allow_unsigned</dt>
      <dd>
        Off by default. When on, deliveries without verification material are
        accepted instead of refused with 401: Slack, Telegram and WhatsApp when
        the channel has no signing secret stored; Teams when the request has no
        Bearer token. For local testing only.
      </dd>
      <dt>enabled</dt>
      <dd>Master switch. Disabled channels ignore inbound traffic.</dd>
    </dl>
    <p>
      The response also exposes a <code>webhook_url</code> —
      <code>/api/messaging/webhooks/{'{provider}'}/{'{channelId}'}</code> — to
      paste into the provider's settings, plus
      <code>last_delivery_at</code> / <code>last_delivery_status</code> for a
      quick health glance.
    </p>
  </section>

  <section>
    <h2>Slack Socket Mode (no public ingress)</h2>
    <p>
      If FlowWeaver is deployed somewhere with no inbound internet exposure
      (e.g. a VPN-only host), Slack can still reach it through Socket Mode: an
      <strong>outbound</strong> WebSocket the server opens to Slack. No public
      webhook URL is required.
    </p>
    <ul>
      <li>
        In your Slack app, enable <strong>Socket Mode</strong> and create an
        <strong>App-Level Token</strong> with the <code>connections:write</code>
        scope. Paste that <code>xapp-…</code> token into the channel's
        <code>app_token</code> field. No Request URL is needed.
      </li>
      <li>
        The backend checks enabled Socket-Mode channels every 30&nbsp;s, opens
        one WebSocket per channel via <code>apps.connections.open</code>,
        acknowledges each envelope, and reconnects with backoff. Inbound socket
        events skip HMAC verification: the WebSocket itself is the trust
        boundary.
      </li>
    </ul>
    <Callout tone="info" title="Where the agent actually runs">
      The Socket-Mode listener can run in any process, but the agent turn is
      processed where the tool registry is populated — the API process. With
      the standard split deployment (an API container plus a worker-only
      container) the shared <code>jobs</code> queue means a socket event the
      worker receives is still answered by the API container, which holds the
      full tool set. A turn that comes back with "no tools enabled" means the
      agent ran in a process without the tool registry.
    </Callout>
  </section>

  <section>
    <h2>Webhook providers (public ingress)</h2>
    <p>
      For deployments with a reachable URL, register
      <code>/api/messaging/webhooks/{'{provider}'}/{'{channelId}'}</code> in the
      provider's settings. The endpoint is anonymous; authenticity comes from
      the provider signature, verified per-provider in the ingest service.
    </p>
    <table>
      <thead>
        <tr><th>Provider</th><th>Inbound</th><th>Notes</th></tr>
      </thead>
      <tbody>
        <tr>
          <td><code>slack</code></td>
          <td>Events API webhook, or Socket Mode (above)</td>
          <td>Signing secret verifies the request; bot-message subtypes are ignored to avoid loops.</td>
        </tr>
        <tr>
          <td><code>telegram</code></td>
          <td><code>setWebhook</code></td>
          <td>Simplest to set up; <code>is_bot</code> messages are dropped so the bot never answers itself.</td>
        </tr>
        <tr>
          <td><code>whatsapp</code></td>
          <td>Meta Cloud API webhook</td>
          <td><code>GET</code> handles the <code>hub.challenge</code> verification handshake.</td>
        </tr>
        <tr>
          <td><code>teams</code></td>
          <td>Bot Framework messaging endpoint</td>
          <td>
            The activity's Bearer JWT is validated against Microsoft's public
            keys; replies post back to the captured <code>serviceUrl</code>
            (forced to https). See the setup walkthrough below.
          </td>
        </tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Microsoft Teams, step by step</h2>
    <p>
      Teams is the one provider whose credentials do not map onto the generic
      field names, so it gets its own walkthrough. The bot is an
      <strong>Azure Bot</strong> resource backed by a Microsoft Entra app
      registration; FlowWeaver speaks the Bot Framework protocol directly, with
      no Azure-hosted bot code in between. Where skipping a step produces a
      specific error message, the step quotes it.
    </p>
    <p>
      You will need: an Azure subscription with permission to create resources,
      permission to manage app registrations in your Entra tenant (or an admin
      who can create the client secret for you), and a Microsoft 365 tenant
      where either custom-app upload is allowed or a Teams admin can approve an
      org app. All Teams users who will talk to the bot must belong to the same
      tenant as the bot (single-tenant bots do not answer users from other
      tenants).
    </p>

    <h3>1. Create the Azure Bot</h3>
    <ol>
      <li>
        Azure portal → <strong>Create a resource</strong> → search for
        <code>bot</code> → pick the <strong>Azure Bot</strong> card →
        <strong>Create</strong>.
      </li>
      <li>
        Fill in: a globally unique <strong>Bot handle</strong>, subscription,
        resource group, region, and pricing tier <strong>F0</strong> (free —
        standard channels such as Teams cost nothing).
      </li>
      <li>
        Under <em>Microsoft App ID</em>, set <em>Type of App</em> to
        <strong>Single Tenant</strong> and <em>Creation type</em> to
        <strong>Create new Microsoft App ID</strong>. (Multi-tenant bot creation
        was retired by Microsoft in July 2025; existing multi-tenant bots keep
        working, but new bots must be single-tenant. Do not pick
        <em>User-assigned managed identity</em>: it authenticates via Azure's
        internal metadata endpoint, which only exists inside Azure — FlowWeaver
        authenticates with a client secret.)
      </li>
      <li>
        <strong>Review + create</strong> → <strong>Create</strong> →
        <strong>Go to resource</strong>.
      </li>
      <li>
        Open the bot's <strong>Settings → Configuration</strong> blade and copy
        two values you will need later: <strong>Microsoft App ID</strong> and
        <strong>App Tenant ID</strong>.
      </li>
    </ol>

    <h3>2. Create the client secret</h3>
    <ol>
      <li>
        Still on the <em>Configuration</em> blade, click <strong>Manage</strong>
        next to <em>Microsoft App ID</em>. This opens the app registration's
        <strong>Certificates &amp; secrets</strong> blade. (If the link is
        missing, go to <em>Microsoft Entra ID → App registrations → All
        applications</em>, search by the App ID, and open <em>Certificates &amp;
        secrets</em> there.)
      </li>
      <li>
        <strong>Client secrets → + New client secret</strong>. Give it a
        recognizable description and an expiry (24 months is the maximum).
      </li>
      <li>
        Copy the <strong>Value</strong> column — not <em>Secret ID</em>, which
        is a useless GUID. The value is shown once; after you navigate away it
        is masked forever. If you lose it, create a new secret and delete the
        old one.
      </li>
    </ol>
    <Callout tone="warning" title="Secrets expire">
      When the secret expires, inbound keeps working but every reply fails with
      <code>teams AAD token failed: 401</code> in the channel's delivery log.
      Set a reminder before the expiry date. Rotation is painless: paste the new
      value into the channel's <em>App password</em> field and save — it takes
      effect on the next message, no restart needed.
    </Callout>

    <h3>3. Enable the Microsoft Teams channel on the bot</h3>
    <ol>
      <li>Azure Bot resource → <strong>Settings → Channels</strong>.</li>
      <li>
        Under <em>Available channels</em>, click
        <strong>Microsoft Teams</strong>, accept the terms, keep
        <strong>Microsoft Teams Commercial</strong> selected, and
        <strong>Apply</strong>.
      </li>
      <li>
        Wait until Teams appears in the enabled-channels list. Propagation can
        take a few minutes.
      </li>
    </ol>
    <Callout tone="warning" title="If you skip this step">
      Adding the app in Teams later fails with <em>"Invalid bot — make sure the
      bot is registered and the Teams channel is enabled"</em>. That error means
      exactly what it says: come back here. The same message also appears when
      the bot ID in the Teams app manifest has a typo, when the Teams account
      you are testing with belongs to a different tenant than a single-tenant
      bot, or — transiently — for a few minutes right after the bot was created.
    </Callout>

    <h3>4. Create the FlowWeaver channel</h3>
    <p>
      At <a href="/admin/messaging-channels"><code>/admin/messaging-channels</code></a>
      (admin only), click <em>New channel</em> and choose provider
      <code>teams</code>. The form relabels itself for the provider; the mapping
      is:
    </p>
    <dl>
      <dt>App password (Entra client secret) → <code>bot_token</code></dt>
      <dd>
        The secret <em>value</em> from step 2. FlowWeaver exchanges it for an
        AAD client-credentials token when it replies (cached until shortly
        before expiry), so a rotated secret takes effect on the next message.
      </dd>
      <dt>Signing secret</dt>
      <dd>
        Not used, and hidden for this provider. Teams authenticity comes from
        the Bot Framework JWT on each activity, not from a shared secret.
      </dd>
      <dt>External config → <code>Microsoft App ID</code> (required)</dt>
      <dd>
        From step 1. It is both the <em>audience</em> the inbound token is
        validated against and the <em>client_id</em> the reply is minted with,
        so the channel is refused at save time without it
        (<code>teams_app_id_required</code>).
      </dd>
      <dt>External config → <code>App Tenant ID</code></dt>
      <dd>
        From step 1. <strong>Required for single-tenant bots</strong> — which is
        every bot created since July 2025. Without it the outbound token request
        goes to the shared <code>botframework.com</code> authority and fails
        with 401. Only a legacy multi-tenant bot may leave it empty.
      </dd>
      <dt>Require linked account / Allow unsigned deliveries</dt>
      <dd>
        Keep the defaults (on / off). <em>Require linked account</em> off does
        not open access — it silently rejects unknown senders instead of sending
        them the self-service linking prompt. <em>Allow unsigned deliveries</em>
        on accepts activities that carry no Bearer JWT; a token that is present
        is still verified. Never set it outside local testing.
      </dd>
    </dl>

    <h3>5. Connect the messaging endpoint</h3>
    <p>
      Azure delivers activities by POSTing to the bot's
      <strong>Messaging endpoint</strong> (Azure Bot →
      <em>Settings → Configuration</em>). It must be an https URL reachable from
      Microsoft's network — a private IP or plain http will never receive
      anything. Pick one of two paths:
    </p>
    <p>
      <strong>Path A — public https URL.</strong> If the deployment is
      internet-reachable (directly, behind a reverse proxy, or through a tunnel
      such as Cloudflare Tunnel), open the FlowWeaver channel's details, copy
      the <code>webhook_url</code>, and paste it as the messaging endpoint. Make
      sure <code>MESSAGING_PUBLIC_BASE_URL</code> points at the public host so
      the displayed URL is the right one, and route the public hostname to the
      <em>frontend</em> (it proxies <code>/api/*</code> to the backend), so the
      account-linking page works on the same host.
    </p>
    <p>
      <strong>Path B — no public ingress (Azure Relay).</strong> For VPN-only or
      NAT-ed deployments, skip the webhook URL entirely and follow
      <em>Teams without public ingress</em> below; the messaging endpoint will
      be the Relay's URL instead.
    </p>

    <h3>6. Create and install the Teams app</h3>
    <p>
      The Azure Bot alone is invisible inside Teams — users can only reach it
      through a Teams <em>app</em> that references it. The quickest way is the
      Teams Developer Portal:
    </p>
    <ol>
      <li>
        Go to <a href="https://dev.teams.microsoft.com" rel="external">dev.teams.microsoft.com</a>
        → <strong>Apps</strong> → <strong>New app</strong>.
      </li>
      <li>
        Under <strong>Configure → Basic information</strong>, fill in
        <em>every</em> required field: app name, <strong>short description</strong>
        (max 80 characters), <strong>long description</strong>, and under
        <em>Developer information</em> the developer name,
        <strong>website</strong>, <strong>privacy policy</strong> and
        <strong>terms of use</strong> URLs. The three URLs must be valid
        <em>https</em> URLs — an http URL or a private IP fails validation. For
        an internal app nobody reviews them; your company website is fine for
        all three.
      </li>
      <li>
        <strong>App features → Bot → Enter a bot ID</strong> → paste the
        <strong>Microsoft App ID</strong>. Tick the scopes you want:
        <em>Personal</em> for 1:1 chat, <em>Team</em> / <em>Group chat</em> if
        the bot should answer @mentions in channels.
      </li>
      <li><strong>Save</strong>.</li>
      <li>
        Install it. There are two routes, and which one you use depends on
        whether your tenant allows custom-app upload:
        <ul>
          <li>
            <strong>Side-load (fastest, for testing):</strong>
            <em>Preview in Teams</em> loads the app straight into your Teams
            client and offers <em>Add</em>. Only you get the app; nobody else
            in the org sees it.
          </li>
          <li>
            <strong>Publish to the organization (for real use):</strong> see
            the flow below. This is also the fallback when <em>Preview in
            Teams</em> is greyed out or fails — that means the tenant blocks
            custom-app upload and side-loading is not available to you.
          </li>
        </ul>
      </li>
    </ol>
    <Callout tone="info" title="Manifest validation errors">
      <em>"Schema validation failed at 'developer': Required properties are
      missing … privacyUrl, termsOfUseUrl"</em> or <em>"… at 'description':
      short, full"</em> means step 2 of this list is incomplete — the portal
      lets you save a draft without those fields, but the manifest will not
      validate until all of them are filled in.
    </Callout>

    <h4>Publishing to the organization</h4>
    <p>
      Side-loading installs the app only for the person who loaded it. For the
      whole team to use the bot, publish it to the org catalog — a two-role
      flow: the developer submits, a Teams administrator approves.
    </p>
    <ol>
      <li>
        <strong>Developer:</strong> in the Developer Portal, open the app →
        <strong>Publish → Publish to your org</strong>. The app is submitted to
        the tenant's catalog with status <em>Submitted / pending approval</em>.
        (Equivalently, an admin can skip the portal entirely:
        <em>Teams admin center → Teams apps → Manage apps → Actions → Upload new
        app</em>, using the manifest zip downloaded from the Developer Portal's
        <em>App package</em> page.)
      </li>
      <li>
        <strong>Teams admin:</strong> go to
        <a href="https://admin.teams.microsoft.com" rel="external">admin.teams.microsoft.com</a>
        → <strong>Teams apps → Manage apps</strong>, search for the app by name
        (filter by <em>Publishing status: Submitted</em> if the list is long),
        open it, review, and <strong>Publish</strong>. The status changes to
        <em>Published</em> and the app's availability follows the tenant's app
        permission policies.
      </li>
      <li>
        <strong>Users:</strong> the app now appears in Teams under
        <strong>Apps → Built for your org</strong>, and anyone in the tenant can
        add it. Catalog propagation is usually minutes but can take up to a few
        hours — a just-published app not showing up immediately is normal.
      </li>
      <li>
        <strong>Optional — install it for people automatically:</strong> in the
        admin center, <em>Teams apps → Setup policies</em> lets an admin add the
        app to <em>Installed apps</em> (and pin it) for a user group, so
        operators get the bot without hunting the catalog. Remember each user
        still links their own FlowWeaver account on first message — installing
        the app grants nothing by itself.
      </li>
    </ol>
    <Callout tone="info" title="Updating a published app">
      Changes to the manifest (name, icons, scopes, new bot ID) require
      publishing a new version, which goes through admin approval again:
      Developer Portal → <em>Publish to your org</em> → admin center →
      <em>Manage apps</em> → the app → <em>Update available</em> → review and
      publish. Changes on the FlowWeaver side — prompts, tools, permissions,
      secrets — need none of this: the app package only points at the bot ID,
      so the app in the catalog stays untouched.
    </Callout>

    <h3>7. First conversation and account linking</h3>
    <ol>
      <li>
        Open the bot's 1:1 chat in Teams and send any message
        (<code>hello</code> is fine).
      </li>
      <li>
        The bot replies with an account-linking URL
        (<code>/link?token=…</code>). Receiving this reply already proves the
        whole loop: Teams → Bot Framework → your endpoint → JWT verification →
        ingest → queue → outbound reply minted with the client secret.
      </li>
      <li>
        Open the link (it points at <code>MESSAGING_PUBLIC_BASE_URL</code>, so
        it needs to be reachable by <em>you</em>, not by Microsoft — a VPN-only
        frontend URL is fine), sign in to FlowWeaver if needed, and confirm. The
        token is single-use and expires in about 15 minutes; if it expired,
        message the bot again for a fresh one.
      </li>
      <li>
        Send the next message — it is now answered by the agent with
        <em>your</em> permissions, capped by the channel's
        <code>max_role</code>.
      </li>
    </ol>

    <Callout tone="info" title="Mentions and threads">
      In a channel or group chat, Teams only delivers messages that @mention the
      bot, and the mention travels inside the message text as
      <code>&lt;at&gt;…&lt;/at&gt;</code> markup. FlowWeaver strips the bot's own
      mention before the agent sees the prompt, unwraps everyone else's to their
      display name, and ignores a message that has nothing left. Replies go back
      to the conversation id the activity carried, so a message sent inside a
      channel thread is answered in that thread.
    </Callout>

    <Callout tone="admin" title="What the inbound check enforces">
      Every POST must carry a Bearer JWT issued by
      <code>api.botframework.com</code>, signed by a key from Microsoft's
      published metadata, still within its lifetime, with an audience equal to
      the channel's <code>app_id</code> <em>and</em> a <code>serviceurl</code>
      claim matching the activity's <code>serviceUrl</code>. That last check
      matters because the reply carries an AAD bearer token to that URL: without
      it, a token minted for another deployment could point FlowWeaver's
      credential somewhere else. Activities authored by a bot
      (<code>from.id</code> starting <code>28:</code>) are dropped as a loop
      guard.
    </Callout>

    <h3>Teams without public ingress (Azure Relay)</h3>
    <p>
      Slack solves the VPN-only case with Socket Mode, and Teams has no
      equivalent — Azure Bot Service only ever <em>POSTs</em> to a messaging
      endpoint, so on its own it needs a reachable https URL. <strong>Azure
      Relay Hybrid Connections</strong> closes that gap from the other side:
      FlowWeaver opens an <strong>outbound</strong> control channel to the Relay,
      and the Relay forwards the Bot Framework's requests over it. No port
      forward, no public IP, no inbound firewall rule.
    </p>
    <ol>
      <li>
        Azure portal → <strong>Create a resource</strong> → search
        <code>Relay</code> → <strong>Relay</strong> → <strong>Create</strong>.
        The namespace name becomes the public hostname
        (<code>&lt;namespace&gt;.servicebus.windows.net</code>).
      </li>
      <li>
        In the namespace: <strong>Entities → Hybrid Connections → + Hybrid
        Connection</strong>. Pick a name (it becomes the URL path) and
        <strong>untick <em>Requires Client Authorization</em></strong> — see the
        note below for why this is the correct setting, not a compromise.
      </li>
      <li>
        Open the new Hybrid Connection → <strong>Settings → Shared access
        policies → + Add</strong>: name it, tick <strong>Listen</strong> only,
        create it, open it, and copy the <strong>Primary Connection
        String</strong>. Created at the Hybrid Connection level it already ends
        in <code>;EntityPath=&lt;connection-name&gt;</code>; if you used a
        namespace-level policy instead, append that part by hand — a string
        without <code>EntityPath</code> is the most common cause of
        <code>teams.relay.error</code>.
      </li>
      <li>
        Paste it into the channel's <em>Azure Relay connection string</em>
        field and save. It is encrypted at rest exactly like the other secrets,
        and the reconcile loop picks it up within 30 seconds — no restart.
      </li>
      <li>
        Set the Azure Bot's <strong>Messaging endpoint</strong> to
        <code>https://&lt;namespace&gt;.servicebus.windows.net/&lt;connection-name&gt;</code>
        — note <code>https://</code>, not the <code>sb://</code> from the
        connection string, and no webhook path after it (the listener already
        knows which channel it belongs to).
      </li>
    </ol>
    <p>Then verify the transport before involving Teams at all:</p>
    <ol>
      <li>
        Backend logs
        (<code>docker compose logs -f backend | grep teams.relay</code>) should
        show <code>teams.relay.start</code> followed by
        <code>teams.relay.connected</code> for the channel. A repeated
        <code>teams.relay.error</code> carries the exact exception —
        typically a missing <code>EntityPath</code> or a policy without
        <em>Listen</em>. A <code>teams.relay.webhook_only</code> line means the
        channel has no connection string stored at all (it was never saved, or
        was cleared).
      </li>
      <li>
        From any machine,
        <code>curl -i https://&lt;namespace&gt;.servicebus.windows.net/&lt;connection-name&gt;</code>:
        a <strong>502 Bad Gateway</strong> means the Relay has no listener (the
        backend is not connected); <strong>anything else</strong> — a 401, a
        404, a JSON body — means the request crossed the Relay, reached the
        ingest pipeline, and was rejected for carrying no JWT, which is exactly
        right. The 502-to-something transition is the fastest way to see the
        Relay–backend link come alive.
      </li>
    </ol>
    <p>
      The backend checks enabled Teams channels every 30&nbsp;s, opens one
      Relay listener per configured channel, and reopens with backoff if the
      control channel drops — the same pattern as Slack Socket Mode. The channel's details panel
      stops advertising the webhook URL once a Relay string is set, because that
      URL is no longer what Azure should be pointed at. If both the API and a
      worker replica run the service, each opens a listener against the same
      Hybrid Connection — that is safe (Azure Relay load-balances across up to
      25 listeners and inbound dedupe covers any overlap) but each listener
      bills its own listener-hours.
    </p>
    <Callout tone="admin" title="Why anonymous senders are the right setting">
      Turning Relay client authorization <em>on</em> makes the Relay consume the
      request's <code>Authorization</code> header as its own SAS token — which
      would eat the Bot Framework JWT before it reaches FlowWeaver. Leaving it off
      costs nothing: unlike Socket Mode, this path does <strong>not</strong> take
      the transport as proof. The relayed request goes through the same
      verification as the public webhook, so issuer, audience,
      signature and the <code>serviceurl</code> claim are all still verified. The
      JWT is the guard. (If you do want Relay-level auth as
      well, pass the SAS token as an <code>sb-hc-token</code> query parameter on
      the messaging endpoint instead — the Relay strips it before forwarding and
      leaves <code>Authorization</code> alone.)
    </Callout>
    <Callout tone="info" title="Cost">
      Socket Mode is free; Azure Relay is not. Hybrid Connections bill per
      listener-hour plus messages, so a channel left enabled bills continuously
      whether or not anyone is chatting. A tunnel (Cloudflare, dev tunnels) or a
      reverse proxy solves the same problem for free — the Relay's advantage is
      that it is first-party Microsoft and involves no third party in the path.
    </Callout>

    <h3>Troubleshooting</h3>
    <p>
      The channel's <em>Details</em> panel is the diagnostic surface: inbound
      events carry the rejection reason, outbound deliveries carry the send
      error. Read failures top-down — a broken link early in the chain produces
      misleading symptoms further down.
    </p>
    <table>
      <thead>
        <tr><th>Symptom</th><th>Cause and fix</th></tr>
      </thead>
      <tbody>
        <tr>
          <td><em>"Invalid bot"</em> when adding the app in Teams</td>
          <td>
            The Teams channel is not enabled on the Azure Bot (step 3), the bot
            ID in the app manifest does not exactly match the Microsoft App ID,
            the Teams account belongs to a different tenant than the
            single-tenant bot, or the bot was created minutes ago — retry after
            five.
          </td>
        </tr>
        <tr>
          <td>Manifest schema validation fails on save/preview</td>
          <td>
            Required <em>Basic information</em> fields are empty: short/long
            description, privacy policy and terms of use URLs (which must be
            https).
          </td>
        </tr>
        <tr>
          <td>Inbound stays at zero</td>
          <td>
            Nothing reaches FlowWeaver: the messaging endpoint is wrong or not
            publicly reachable (path A), the Relay listener is not connected
            (path B — check <code>teams.relay.connected</code> in the logs and
            the 502 curl test above), or the app was never actually installed.
          </td>
        </tr>
        <tr>
          <td>Inbound rows appear as <code>rejected</code> with 401</td>
          <td>
            The activity arrived but its JWT failed verification. If the reason
            is <em>missing bearer token</em> on the Relay path, the Hybrid
            Connection was created with <em>Requires Client Authorization</em>
            on and the Relay consumed the <code>Authorization</code> header —
            recreate it with the box unticked. Otherwise the
            <code>app_id</code> in external config does not match the App ID the
            token was minted for.
          </td>
        </tr>
        <tr>
          <td>
            Inbound <code>completed</code>, outbound <code>failed</code> with
            <em>teams AAD token failed: 401</em>
          </td>
          <td>
            The reply could not authenticate: the client secret is wrong or
            expired, or <code>tenant_id</code> is missing on a single-tenant bot
            so the token request went to the wrong authority. Paste a fresh
            secret / add the tenant ID and message the bot again.
          </td>
        </tr>
        <tr>
          <td>Outbound <code>failed</code> with <em>teams reply failed: 403/404</em></td>
          <td>
            The AAD token was minted but the Bot Framework rejected the post —
            usually the conversation is gone (app uninstalled) or the bot lost
            access to it. Reinstall the app and start a new chat.
          </td>
        </tr>
        <tr>
          <td>The bot answers, but with a broken account-linking URL</td>
          <td>
            <code>MESSAGING_PUBLIC_BASE_URL</code> is empty or points at a host
            the user cannot open. It must be the frontend URL your users
            actually reach (a VPN-internal address is fine — Microsoft never
            opens this link, people do).
          </td>
        </tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Reliability &amp; auditing</h2>
    <ul>
      <li>
        <strong>Dedupe</strong> — every inbound event is keyed by
        <code>channel + provider_event_id</code>; a redelivered event is
        recognized and skipped.
      </li>
      <li>
        <strong>At-most-once agent turn</strong> — the inbound row is the
        idempotency anchor. It flips <code>queued → processing</code> atomically
        before the agent runs, so a job that gets reclaimed after its lease
        expires sees <code>processing</code> and skips — no double LLM call, no
        duplicate reply.
      </li>
      <li>
        <strong>Send retries</strong> — outbound deliveries retry with an
        attempt counter up to a cap.
      </li>
      <li>
        <strong>Retention</strong> — a background sweep prunes old inbound
        events and deliveries on a schedule.
      </li>
      <li>
        <strong>Activity view</strong> — the channel exposes recent inbound
        events and outbound deliveries (status, attempt, error, timestamps) for
        troubleshooting.
      </li>
    </ul>
  </section>

  <section>
    <h2>Role differences</h2>
    <ul>
      <li>
        <strong>Viewer / Operator</strong> — can link their own external
        identity at <code>/link</code> and talk to the bot at their own
        privilege level. They cannot create or edit channels.
      </li>
      <li>
        <strong>Admin</strong> — creates and configures channels at
        <code>/admin/messaging-channels</code>, sets the <code>max_role</code>
        ceiling and default agent, rotates secrets, and reviews the activity
        log.
      </li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/ai">AI overview</a> — how a turn assembles and the built-in tools.</li>
      <li><a href="/docs/ai/chat">Chat</a> — the same agent on the web surface.</li>
      <li><a href="/docs/ai/agents">Agents</a> — the profile a channel can pin via <code>default_agent_id</code>.</li>
    </ul>
  </section>
</DocLayout>
