<script lang="ts">
  import { onMount } from 'svelte';
  import { copyText } from '$lib/utils/clipboard';
  import {
    messagingChannels,
    errorMessage,
    type MessagingChannel,
    type CreateMessagingChannelBody,
    type MessagingChannelActivity,
    type MessagingIdentityLink,
  } from '$lib/api/client';
  import {
    PageHeader,
    Card,
    DataTable,
    Badge,
    StatusBadge,
    Button,
    IconButton,
    Dialog,
    Input,
    Select,
    Alert,
    Spinner,
    EmptyState,
    ErrorState,
    toast,
    confirm,
    formatDateTime,
    FieldHint,
  } from '$lib/components/ui';
  import ParamsEditor from '$lib/components/ParamsEditor.svelte';
  import { Plus, Pencil, Trash2, RefreshCw, MessageSquare, Eye, Copy } from 'lucide-svelte';

  const PROVIDERS = ['telegram', 'slack', 'whatsapp', 'teams'] as const;

  // The three secret fields mean something different on every platform — a
  // Teams "bot token" is an Entra client secret, and Teams has no inbound
  // signing secret at all. A generic form makes an admin read the source to
  // find out; this spells it out per provider and hides what does not apply.
  // `configSchema` drives the External config editor: with properties it
  // renders labelled inputs (ParamsEditor → JsonSchemaForm) so nobody has to
  // hand-write JSON; empty, it falls back to the Add-field key/value editor for
  // the providers whose config is open-ended.
  type ProviderSpec = {
    botToken: { label: string; placeholder: string } | null;
    signingSecret: { label: string; placeholder: string } | null;
    appToken: { label: string; placeholder: string } | null;
    configSchema: Record<string, unknown>;
    setup: string;
  };

  const PROVIDER_SPECS: Record<(typeof PROVIDERS)[number], ProviderSpec> = {
    telegram: {
      botToken: { label: 'Bot token', placeholder: '123456:ABC-DEF… from @BotFather' },
      signingSecret: { label: 'Secret token', placeholder: 'the secret_token you passed to setWebhook' },
      appToken: null,
      configSchema: {},
      setup: 'Register the webhook URL below with setWebhook, passing the same secret_token.',
    },
    slack: {
      botToken: { label: 'Bot token', placeholder: 'xoxb-…' },
      signingSecret: { label: 'Signing secret', placeholder: 'from the app\'s Basic Information page' },
      appToken: { label: 'App-level token · Socket Mode (optional)', placeholder: 'xapp-… → outbound WebSocket, no public webhook needed' },
      configSchema: {},
      setup: 'Either paste the webhook URL as the Events API Request URL, or set an app-level token to use Socket Mode instead.',
    },
    whatsapp: {
      botToken: { label: 'Access token', placeholder: 'Meta Cloud API permanent access token' },
      signingSecret: { label: 'App secret', placeholder: 'verifies X-Hub-Signature-256' },
      appToken: null,
      configSchema: {
        type: 'object',
        required: ['phone_number_id', 'verify_token'],
        'x-order': ['phone_number_id', 'verify_token', 'graph_version'],
        properties: {
          phone_number_id: {
            type: 'string',
            title: 'Phone number ID',
            description: 'From the WhatsApp → API setup page of your Meta app. Identifies the number replies are sent from.',
            placeholder: '1234567890',
          },
          verify_token: {
            type: 'string',
            title: 'Verify token',
            description: 'A string you invent. Meta echoes it back during the GET webhook handshake; paste the same value on both sides.',
          },
          graph_version: {
            type: 'string',
            title: 'Graph API version (optional)',
            description: 'Overrides the default Graph API version used for outbound messages.',
            placeholder: 'v21.0',
          },
        },
      },
      setup: 'phone_number_id identifies the sending number; verify_token is echoed back during Meta\'s GET handshake.',
    },
    teams: {
      botToken: { label: 'App password (Entra client secret)', placeholder: 'the client secret of the bot\'s app registration' },
      // Teams authenticates inbound traffic with the Bot Framework JWT, so
      // there is no secret to store for it.
      signingSecret: null,
      // Teams has no Socket Mode; Azure Relay plays the same role, and the
      // credential lives in the same opt-in field.
      appToken: {
        label: 'Azure Relay connection string · no public ingress (optional)',
        placeholder: 'Endpoint=sb://…servicebus.windows.net/;SharedAccessKeyName=…;SharedAccessKey=…;EntityPath=…',
      },
      configSchema: {
        type: 'object',
        required: ['app_id'],
        'x-order': ['app_id', 'tenant_id'],
        properties: {
          app_id: {
            type: 'string',
            title: 'Microsoft App ID',
            description: 'Azure Bot → Configuration → Microsoft App ID. Both the audience the inbound token is validated against and the client_id replies are minted with.',
            placeholder: '00000000-0000-0000-0000-000000000000',
          },
          tenant_id: {
            type: 'string',
            title: 'App Tenant ID',
            description: 'Azure Bot → Configuration → App Tenant ID. Required for single-tenant bots (the only type Azure still creates); leave empty for a legacy multi-tenant bot.',
            placeholder: '00000000-0000-0000-0000-000000000000',
          },
        },
      },
      setup:
        'app_id is the Azure Bot\'s Microsoft App ID (required — it is both the token audience and the reply client_id). '
        + 'tenant_id is only needed for single-tenant / managed-identity bots. Paste the webhook URL below as the bot\'s messaging endpoint.',
    },
  };

  type ChannelForm = {
    provider: string;
    name: string;
    bot_token: string;
    signing_secret: string;
    app_token: string;
    external_config: Record<string, unknown>;
    max_role: string;
    require_linked_user: boolean;
    allow_unsigned: boolean;
    allowed_external_ids: string;
  };

  function emptyForm(): ChannelForm {
    return {
      provider: 'telegram',
      name: '',
      bot_token: '',
      signing_secret: '',
      app_token: '',
      external_config: {},
      max_role: '',
      require_linked_user: true,
      allow_unsigned: false,
      allowed_external_ids: '',
    };
  }

  // ═══ State ═══
  let list = $state<MessagingChannel[]>([]);
  let loading = $state(true);
  let loadError = $state<unknown>(null);

  let showForm = $state(false);
  let editing = $state<MessagingChannel | null>(null);
  let form = $state<ChannelForm>(emptyForm());
  let saving = $state(false);
  let formError = $state<string | null>(null);

  const spec = $derived(
    PROVIDER_SPECS[form.provider as (typeof PROVIDERS)[number]] ?? PROVIDER_SPECS.telegram,
  );

  let showDetails = $state(false);
  let detailsChannel = $state<MessagingChannel | null>(null);
  let activity = $state<MessagingChannelActivity | null>(null);
  let links = $state<MessagingIdentityLink[]>([]);
  let detailsLoading = $state(false);

  // Aggregated "Linked accounts" panel: every identity link across all
  // channels in one place so an admin can unlink a Slack/WhatsApp/… account
  // without drilling into each channel's details.
  let allLinks = $state<{ channel: MessagingChannel; link: MessagingIdentityLink }[]>([]);
  let linksLoading = $state(false);

  onMount(load);

  async function load() {
    loading = true;
    loadError = null;
    try {
      const res = await messagingChannels.list();
      list = res.data;
      void loadAllLinks();
    } catch (e) {
      loadError = e;
    } finally {
      loading = false;
    }
  }

  // The backend exposes links per channel; fan out and flatten so the panel
  // shows them together. A failed channel fetch degrades to no rows for that
  // channel rather than failing the whole panel.
  async function loadAllLinks() {
    if (list.length === 0) {
      allLinks = [];
      return;
    }
    linksLoading = true;
    try {
      const results = await Promise.all(
        list.map((c) =>
          messagingChannels
            .links(c.messaging_channel_id)
            .then((r) => r.data.map((link) => ({ channel: c, link })))
            .catch(() => [] as { channel: MessagingChannel; link: MessagingIdentityLink }[]),
        ),
      );
      allLinks = results.flat();
    } finally {
      linksLoading = false;
    }
  }

  // ═══ Create / Edit ═══
  function openCreate() {
    editing = null;
    form = emptyForm();
    formError = null;
    showForm = true;
  }

  function openEdit(channel: MessagingChannel) {
    editing = channel;
    form = {
      provider: channel.provider,
      name: channel.name,
      bot_token: '',
      signing_secret: '',
      app_token: '',
      external_config: { ...(channel.external_config ?? {}) },
      max_role: channel.max_role ?? '',
      require_linked_user: channel.require_linked_user,
      allow_unsigned: channel.allow_unsigned,
      allowed_external_ids: channel.allowed_external_ids.join(', '),
    };
    formError = null;
    showForm = true;
  }

  function buildBody(): CreateMessagingChannelBody {
    // Blank entries are dropped rather than stored as "": an optional key the
    // admin left alone should not persist, and a required one left alone should
    // trip the backend's "missing" error instead of its "empty" one.
    const externalConfig = Object.fromEntries(
      Object.entries(form.external_config ?? {}).filter(
        ([, v]) => !(typeof v === 'string' && v.trim() === '') && v !== null && v !== undefined,
      ),
    );
    const ids = form.allowed_external_ids
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean);

    return {
      provider: form.provider,
      name: form.name.trim(),
      // Empty string on edit means "leave unchanged" (undefined); on create it
      // simply means "no token". A secret the selected provider has no field
      // for is dropped, so switching provider mid-form cannot smuggle in a
      // value the admin can no longer see.
      bot_token: spec.botToken && form.bot_token ? form.bot_token : undefined,
      signing_secret: spec.signingSecret && form.signing_secret ? form.signing_secret : undefined,
      app_token: spec.appToken && form.app_token ? form.app_token : undefined,
      external_config: externalConfig,
      max_role: form.max_role || null,
      require_linked_user: form.require_linked_user,
      allow_unsigned: form.allow_unsigned,
      allowed_external_ids: ids,
    };
  }

  async function save(event?: SubmitEvent) {
    event?.preventDefault();
    if (saving) return;
    formError = null;
    if (!form.name.trim()) {
      formError = 'Name is required';
      return;
    }
    const body = buildBody();

    saving = true;
    try {
      if (editing) {
        const updated = await messagingChannels.update(editing.messaging_channel_id, body);
        list = list.map((c) => (c.messaging_channel_id === updated.messaging_channel_id ? updated : c));
        toast.success('Channel updated');
      } else {
        const created = await messagingChannels.create(body);
        list = [...list, created];
        toast.success(`Channel ${created.name} created`);
      }
      showForm = false;
      editing = null;
    } catch (e) {
      formError = errorMessage(e);
    } finally {
      saving = false;
    }
  }

  async function remove(channel: MessagingChannel) {
    const ok = await confirm({
      title: `Delete ${channel.name}?`,
      message: 'The channel will be disabled and soft-deleted. Inbound webhooks stop working.',
      confirmLabel: 'Delete',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await messagingChannels.delete(channel.messaging_channel_id);
      list = list.filter((c) => c.messaging_channel_id !== channel.messaging_channel_id);
      allLinks = allLinks.filter((e) => e.channel.messaging_channel_id !== channel.messaging_channel_id);
      toast.success('Channel deleted');
    } catch (e) {
      toast.fromError(e, 'Delete failed');
    }
  }

  // ═══ Details (webhook URL + deliveries + links) ═══
  async function openDetails(channel: MessagingChannel) {
    detailsChannel = channel;
    activity = null;
    links = [];
    showDetails = true;
    detailsLoading = true;
    try {
      const [act, lnk] = await Promise.all([
        messagingChannels.activity(channel.messaging_channel_id),
        messagingChannels.links(channel.messaging_channel_id),
      ]);
      activity = act;
      links = lnk.data;
    } catch (e) {
      toast.fromError(e, 'Failed to load channel activity');
    } finally {
      detailsLoading = false;
    }
  }

  async function revokeLink(channel: MessagingChannel, link: MessagingIdentityLink) {
    const ok = await confirm({
      title: 'Revoke link?',
      message: `External user ${link.external_user_id} will lose agent access on "${channel.name}" until they link again.`,
      confirmLabel: 'Revoke',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await messagingChannels.revokeLink(channel.messaging_channel_id, link.messaging_identity_link_id);
      links = links.filter((l) => l.messaging_identity_link_id !== link.messaging_identity_link_id);
      allLinks = allLinks.filter((e) => e.link.messaging_identity_link_id !== link.messaging_identity_link_id);
      toast.success('Link revoked');
    } catch (e) {
      toast.fromError(e, 'Revoke failed');
    }
  }

  async function copyWebhook(url: string) {
    if (await copyText(url)) toast.success('Webhook URL copied');
    else toast.error('Copy failed');
  }
</script>

<svelte:head><title>Messaging channels · Flow Weaver</title></svelte:head>

<div class="p-6 space-y-6">
  <PageHeader title="Messaging channels" description="Connect Slack, Teams, WhatsApp and Telegram to the agent.">
    {#snippet actions()}
      <Button variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
      <Button variant="primary" icon={Plus} onclick={openCreate}>New channel</Button>
    {/snippet}
  </PageHeader>

  {#if loading}
    <Spinner size="lg" label="Loading channels…" />
  {:else if loadError}
    <Card padding="none"><ErrorState error={loadError} onRetry={load} /></Card>
  {:else if list.length === 0}
    <Card padding="none">
      <EmptyState
        icon={MessageSquare}
        title="No channels yet"
        description="Create a channel to let users talk to the agent from Slack, Teams, WhatsApp or Telegram."
      >
        {#snippet actions()}
          <Button variant="primary" icon={Plus} onclick={openCreate}>Create first channel</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else}
    <Card padding="none">
      <DataTable caption="Messaging channels">
        <thead>
          <tr>
            <th>Name</th>
            <th>Provider</th>
            <th>Status</th>
            <th>Last delivery</th>
            <th class="!text-right">Actions</th>
          </tr>
        </thead>
        <tbody>
          {#each list as channel (channel.messaging_channel_id)}
            <tr>
              <td><span class="font-medium">{channel.name}</span></td>
              <td><Badge tone="primary" mono>{channel.provider}</Badge></td>
              <td>
                <StatusBadge
                  status={channel.enabled ? 'success' : 'warning'}
                  label={channel.enabled ? 'Enabled' : 'Disabled'}
                  showDot={false}
                />
              </td>
              <td class="text-surface-500 text-sm">
                {channel.last_delivery_at ? formatDateTime(channel.last_delivery_at) : '—'}
                {#if channel.last_delivery_status}
                  <span class="text-xs">({channel.last_delivery_status})</span>
                {/if}
              </td>
              <td class="text-right">
                <div class="inline-flex items-center gap-1">
                  <IconButton icon={Eye} label={`Details for ${channel.name}`} onclick={() => openDetails(channel)} />
                  <IconButton icon={Pencil} label={`Edit ${channel.name}`} onclick={() => openEdit(channel)} />
                  <IconButton icon={Trash2} label={`Delete ${channel.name}`} variant="danger" onclick={() => remove(channel)} />
                </div>
              </td>
            </tr>
          {/each}
        </tbody>
      </DataTable>
    </Card>
  {/if}

  <!-- Dedicated unlink panel: all identity links across every channel. -->
  {#if !loading && !loadError && list.length > 0}
    <Card padding="none">
      <div class="px-4 pt-4 pb-3 flex items-start justify-between gap-3">
        <div>
          <h2 class="text-sm font-semibold text-surface-900-100 inline-flex items-center gap-1">Linked accounts <FieldHint id="channels.linked_accounts" /></h2>
          <p class="text-xs text-surface-500 mt-0.5">
            External identities (Slack, WhatsApp, Telegram, Teams) bound to a FlowWeaver
            user. Revoking unlinks the account — the user must link again to regain agent access.
          </p>
        </div>
        <Button variant="ghost" icon={RefreshCw} onclick={loadAllLinks}>Refresh</Button>
      </div>
      {#if linksLoading}
        <div class="p-4"><Spinner label="Loading linked accounts…" /></div>
      {:else if allLinks.length === 0}
        <div class="px-4 pb-4"><p class="text-sm text-surface-500">No linked accounts yet.</p></div>
      {:else}
        <DataTable caption="Linked accounts">
          <thead>
            <tr>
              <th>Provider</th>
              <th>Channel</th>
              <th>External user</th>
              <th>Linked</th>
              <th class="!text-right">Actions</th>
            </tr>
          </thead>
          <tbody>
            {#each allLinks as entry (entry.link.messaging_identity_link_id)}
              <tr>
                <td><Badge tone="primary" mono>{entry.channel.provider}</Badge></td>
                <td>{entry.channel.name}</td>
                <td>
                  <span class="font-mono">{entry.link.external_user_id}</span>
                  {#if entry.link.display_name}<span class="text-surface-500"> ({entry.link.display_name})</span>{/if}
                </td>
                <td class="text-surface-500 text-sm">{formatDateTime(entry.link.created_at)}</td>
                <td class="text-right">
                  <IconButton
                    icon={Trash2}
                    label={`Revoke link for ${entry.link.external_user_id}`}
                    variant="danger"
                    onclick={() => revokeLink(entry.channel, entry.link)}
                  />
                </td>
              </tr>
            {/each}
          </tbody>
        </DataTable>
      {/if}
    </Card>
  {/if}
</div>

<!-- Create / Edit -->
<Dialog bind:open={showForm} title={editing ? `Edit ${editing.name}` : 'New channel'} size="lg">
  <form class="space-y-4 p-1" onsubmit={save}>
    {#if formError}
      <Alert tone="error">{formError}</Alert>
    {/if}

    <div class="grid grid-cols-2 gap-4">
      <Select label="Provider" help="channels.provider" bind:value={form.provider} disabled={saving || !!editing}>
        {#each PROVIDERS as p (p)}
          <option value={p}>{p}</option>
        {/each}
      </Select>
      <Input label="Name" help="channels.name" placeholder="ops-telegram" bind:value={form.name} disabled={saving} />
    </div>

    <Alert tone="info">{spec.setup}</Alert>

    {#if spec.botToken}
      <Input
        label={editing ? `${spec.botToken.label} (leave blank to keep)` : spec.botToken.label}
        help="channels.bot_token"
        placeholder={spec.botToken.placeholder}
        bind:value={form.bot_token}
        disabled={saving}
      />
    {/if}
    {#if spec.signingSecret}
      <Input
        label={editing ? `${spec.signingSecret.label} (leave blank to keep)` : spec.signingSecret.label}
        help="channels.signing_secret"
        placeholder={spec.signingSecret.placeholder}
        bind:value={form.signing_secret}
        disabled={saving}
      />
    {/if}
    {#if spec.appToken}
      <Input
        label={editing ? `${spec.appToken.label} (leave blank to keep)` : spec.appToken.label}
        help="channels.app_token"
        placeholder={spec.appToken.placeholder}
        bind:value={form.app_token}
        disabled={saving}
      />
    {/if}

    <div class="flex flex-col gap-1">
      <span class="text-xs font-medium text-surface-600-400 inline-flex items-center gap-1">
        External config <FieldHint id="channels.external_config" />
      </span>
      <ParamsEditor schema={spec.configSchema} bind:value={form.external_config} readonly={saving} />
    </div>

    <div class="grid grid-cols-2 gap-4">
      <Select label="Max role (ceiling)"
      help="channels.max_role" bind:value={form.max_role} disabled={saving}>
        <option value="">No ceiling (user's role)</option>
        <option value="viewer">viewer</option>
        <option value="operator">operator</option>
        <option value="admin">admin</option>
      </Select>
      <Input
        label="Allowed external ids (comma-separated)"
      help="channels.allowed_external_ids"
        placeholder="U123, U456"
        bind:value={form.allowed_external_ids}
        disabled={saving}
      />
    </div>

    <div class="flex flex-col gap-2">
      <label class="inline-flex items-center gap-2 text-sm text-surface-900-100">
        <input type="checkbox" bind:checked={form.require_linked_user} disabled={saving} />
        Require linked account (recommended — no agent access until the user links) <FieldHint id="channels.require_linked_user" />
      </label>
      <label class="inline-flex items-center gap-2 text-sm text-surface-900-100">
        <input type="checkbox" bind:checked={form.allow_unsigned} disabled={saving} />
        Allow unsigned deliveries (testing only) <FieldHint id="channels.allow_unsigned" />
      </label>
    </div>
  </form>
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showForm = false)} disabled={saving}>Cancel</Button>
    <Button variant="primary" onclick={() => save()} loading={saving}>
      {editing ? 'Save changes' : 'Create'}
    </Button>
  {/snippet}
</Dialog>

<!-- Details -->
<Dialog bind:open={showDetails} title={detailsChannel ? `${detailsChannel.name} — activity` : 'Activity'} size="lg">
  {#if detailsChannel}
    <div class="space-y-5 p-1">
      {#if detailsChannel.has_app_token}
        <!-- The channel dials out, so the webhook URL below is not what the
             provider should be pointed at — saying otherwise sends an admin
             off configuring an endpoint that will never be called. -->
        <Alert tone="info">
          {#if detailsChannel.provider === 'teams'}
            This channel receives over <strong>Azure Relay</strong>. Point the Azure Bot's
            <em>Messaging endpoint</em> at the Hybrid Connection's public URL
            (<code>https://&lt;namespace&gt;.servicebus.windows.net/&lt;connection-name&gt;</code>), not at the URL below.
          {:else if detailsChannel.provider === 'slack'}
            This channel receives over <strong>Socket Mode</strong>. No Request URL is needed in the Slack app.
          {:else}
            This channel is configured to receive over an outbound connection rather than the webhook below.
          {/if}
        </Alert>
      {/if}

      <div>
        <div class="text-xs uppercase tracking-wide text-surface-500 mb-1">Webhook URL</div>
        <div class="flex items-center gap-2">
          <code class="text-xs break-all bg-surface-200-800/40 rounded px-2 py-1 flex-1">{detailsChannel.webhook_url}</code>
          <IconButton icon={Copy} label="Copy webhook URL" onclick={() => copyWebhook(detailsChannel!.webhook_url)} />
        </div>
        <p class="text-xs text-surface-500 mt-1">Register this URL in the provider's webhook settings.</p>
      </div>

      {#if detailsLoading}
        <Spinner label="Loading activity…" />
      {:else}
        <div>
          <div class="text-sm font-medium mb-2">Linked accounts ({links.length})</div>
          {#if links.length === 0}
            <p class="text-sm text-surface-500">No linked users yet.</p>
          {:else}
            <ul class="space-y-1">
              {#each links as link (link.messaging_identity_link_id)}
                <li class="flex items-center justify-between text-sm bg-surface-200-800/30 rounded px-2 py-1">
                  <span class="font-mono">{link.external_user_id}{link.display_name ? ` (${link.display_name})` : ''}</span>
                  <Button variant="ghost" onclick={() => revokeLink(detailsChannel!, link)}>Revoke</Button>
                </li>
              {/each}
            </ul>
          {/if}
        </div>

        <div class="grid grid-cols-2 gap-4">
          <div>
            <div class="text-sm font-medium mb-2">Inbound ({activity?.inbound.length ?? 0})</div>
            <ul class="space-y-1 text-xs max-h-48 overflow-auto">
              {#each activity?.inbound ?? [] as e (e.messaging_inbound_event_id)}
                <li class="bg-surface-200-800/30 rounded px-2 py-1">
                  <span class="font-medium">{e.status}</span> · {formatDateTime(e.at)}
                  {#if e.error}<div class="text-error-300">{e.error}</div>{/if}
                </li>
              {/each}
            </ul>
          </div>
          <div>
            <div class="text-sm font-medium mb-2">Outbound ({activity?.outbound.length ?? 0})</div>
            <ul class="space-y-1 text-xs max-h-48 overflow-auto">
              {#each activity?.outbound ?? [] as d (d.messaging_delivery_id)}
                <li class="bg-surface-200-800/30 rounded px-2 py-1">
                  <span class="font-medium">{d.status}</span> · {formatDateTime(d.at)}
                  {#if d.error}<div class="text-error-300">{d.error}</div>{/if}
                </li>
              {/each}
            </ul>
          </div>
        </div>
      {/if}
    </div>
  {/if}
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showDetails = false)}>Close</Button>
  {/snippet}
</Dialog>
