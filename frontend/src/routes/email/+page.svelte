<script lang="ts">
  import { onMount } from 'svelte';
  import {
    emailChannels,
    errorMessage,
    type EmailChannel,
    type CreateEmailChannelBody,
    type EmailProviderPreset,
    type EmailProviderKind,
    type EmailSecurity,
    type EmailTestResult,
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
    Textarea,
    Alert,
    Spinner,
    EmptyState,
    ErrorState,
    toast,
    confirm,
    formatDateTime,
    FieldHint,
  } from '$lib/components/ui';
  import { Plus, Pencil, Trash2, RefreshCw, Mail, Send, ExternalLink } from 'lucide-svelte';

  const SECURITY_OPTIONS: { value: EmailSecurity; label: string }[] = [
    { value: 'starttls', label: 'STARTTLS (port 587)' },
    { value: 'ssl', label: 'SSL/TLS (port 465)' },
    { value: 'none', label: 'None — unencrypted (port 25)' },
  ];

  type ChannelForm = {
    name: string;
    provider: EmailProviderKind;
    host: string;
    port: number;
    security: EmailSecurity;
    username: string;
    password: string;
    from_address: string;
    from_name: string;
    reply_to: string;
    allow_private_network: boolean;
    tls_skip_verify: boolean;
    is_default: boolean;
    enabled: boolean;
  };

  function emptyForm(): ChannelForm {
    return {
      name: '',
      provider: 'gmail',
      host: '',
      port: 587,
      security: 'starttls',
      username: '',
      password: '',
      from_address: '',
      from_name: '',
      reply_to: '',
      allow_private_network: false,
      tls_skip_verify: false,
      is_default: false,
      enabled: true,
    };
  }

  // ═══ State ═══
  let list = $state<EmailChannel[]>([]);
  let presets = $state<EmailProviderPreset[]>([]);
  let loading = $state(true);
  let loadError = $state<unknown>(null);

  let showForm = $state(false);
  let editing = $state<EmailChannel | null>(null);
  let form = $state<ChannelForm>(emptyForm());
  let saving = $state(false);
  let formError = $state<string | null>(null);

  let showTest = $state(false);
  let testChannel = $state<EmailChannel | null>(null);
  let testTo = $state('');
  let testSubject = $state('');
  let testBody = $state('');
  let testing = $state(false);
  let testResult = $state<EmailTestResult | null>(null);

  const preset = $derived(presets.find((p) => p.provider === form.provider) ?? null);

  // SendGrid mandates the literal `apikey`; showing an editable field there
  // invites an admin to type their account address and get a 535 they cannot
  // explain. The backend enforces the same value — this just makes it visible.
  const usernameLocked = $derived(preset?.fixed_username != null);

  onMount(load);

  async function load() {
    loading = true;
    loadError = null;
    try {
      const [channelRes, presetRes] = await Promise.all([
        emailChannels.list(),
        emailChannels.presets(),
      ]);
      list = channelRes.data;
      presets = presetRes;
    } catch (e) {
      loadError = e;
    } finally {
      loading = false;
    }
  }

  // ═══ Create / Edit ═══
  function openCreate() {
    editing = null;
    form = emptyForm();
    applyPreset(form.provider);
    formError = null;
    showForm = true;
  }

  function openEdit(channel: EmailChannel) {
    editing = channel;
    form = {
      name: channel.name,
      provider: channel.provider,
      host: channel.host,
      port: channel.port,
      security: channel.security,
      username: channel.username ?? '',
      // Never prefilled — the backend does not return it, and an empty field
      // means "keep the stored one" on save.
      password: '',
      from_address: channel.from_address,
      from_name: channel.from_name ?? '',
      reply_to: channel.reply_to ?? '',
      allow_private_network: channel.allow_private_network,
      tls_skip_verify: channel.tls_skip_verify,
      is_default: channel.is_default,
      enabled: channel.enabled,
    };
    formError = null;
    showForm = true;
  }

  // Changing the provider re-seeds the connection fields. On an edit we leave
  // a host the admin already tuned alone unless they actually switch provider,
  // which is why this only runs from the select's onchange.
  function applyPreset(provider: EmailProviderKind) {
    const p = presets.find((x) => x.provider === provider);
    if (!p) return;
    form.host = p.host;
    form.port = p.port;
    form.security = p.security;
    if (p.fixed_username != null) form.username = p.fixed_username;
  }

  function buildBody(): CreateEmailChannelBody {
    return {
      name: form.name.trim(),
      provider: form.provider,
      host: form.host.trim(),
      port: form.port,
      security: form.security,
      username: form.username.trim(),
      // Blank on edit = keep the stored password. Blank on create = no SMTP
      // AUTH at all, which is only valid for an unauthenticated relay.
      password: editing && !form.password ? undefined : form.password,
      from_address: form.from_address.trim(),
      from_name: form.from_name.trim(),
      reply_to: form.reply_to.trim(),
      allow_private_network: form.allow_private_network,
      tls_skip_verify: form.tls_skip_verify,
      is_default: form.is_default,
      enabled: form.enabled,
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
    if (!form.host.trim()) {
      formError = 'SMTP host is required';
      return;
    }
    if (!form.from_address.trim()) {
      formError = 'From address is required';
      return;
    }

    saving = true;
    try {
      if (editing) {
        const updated = await emailChannels.update(editing.email_channel_id, buildBody());
        // A new default demotes every other row, so refetch rather than
        // patching one entry and leaving two rows both claiming it.
        list = updated.is_default
          ? (await emailChannels.list()).data
          : list.map((c) => (c.email_channel_id === updated.email_channel_id ? updated : c));
        toast.success('Channel updated');
      } else {
        const created = await emailChannels.create(buildBody());
        list = created.is_default ? (await emailChannels.list()).data : [...list, created];
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

  async function remove(channel: EmailChannel) {
    const ok = await confirm({
      title: `Delete ${channel.name}?`,
      message:
        'The channel will be disabled and soft-deleted. Any email_send step pointing at it starts failing.',
      confirmLabel: 'Delete',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await emailChannels.delete(channel.email_channel_id);
      list = list.filter((c) => c.email_channel_id !== channel.email_channel_id);
      toast.success('Channel deleted');
    } catch (e) {
      toast.fromError(e, 'Delete failed');
    }
  }

  // ═══ Test send ═══
  function openTest(channel: EmailChannel) {
    testChannel = channel;
    testTo = '';
    testSubject = '';
    testBody = '';
    testResult = null;
    showTest = true;
  }

  async function runTest(event?: SubmitEvent) {
    event?.preventDefault();
    if (testing || !testChannel) return;
    testResult = null;
    testing = true;
    try {
      testResult = await emailChannels.test(testChannel.email_channel_id, {
        to: testTo.trim(),
        subject: testSubject.trim() || undefined,
        body: testBody.trim() || undefined,
      });
      // The attempt updates last_send_* on the row whether it succeeded or not.
      const refreshed = await emailChannels.get(testChannel.email_channel_id);
      list = list.map((c) => (c.email_channel_id === refreshed.email_channel_id ? refreshed : c));
    } catch (e) {
      // A 4xx here is a rejected request (bad address, missing permission),
      // not a failed send — surface it as such rather than as a test result.
      toast.fromError(e, 'Test failed');
    } finally {
      testing = false;
    }
  }
</script>

<svelte:head><title>Email · Flow Weaver</title></svelte:head>

<div class="p-6 space-y-6">
  <PageHeader
    title="Email"
    description="SMTP relays workflows send mail through, one per provider account."
  >
    {#snippet actions()}
      <Button variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
      <Button variant="primary" icon={Plus} onclick={openCreate}>New channel</Button>
    {/snippet}
  </PageHeader>

  {#if loading}
    <Spinner size="lg" label="Loading email channels…" />
  {:else if loadError}
    <Card padding="none"><ErrorState error={loadError} onRetry={load} /></Card>
  {:else if list.length === 0}
    <Card padding="none">
      <EmptyState
        icon={Mail}
        title="No email channels yet"
        description="Add an SMTP relay so workflows can send mail with the email_send node. Gmail, Microsoft 365, SendGrid, Amazon SES, Mailgun and custom servers are supported."
      >
        {#snippet actions()}
          <Button variant="primary" icon={Plus} onclick={openCreate}>Create first channel</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else}
    <Card padding="none">
      <DataTable caption="Email channels">
        <thead>
          <tr>
            <th>Name</th>
            <th>Provider</th>
            <th>Server</th>
            <th>From</th>
            <th>Status</th>
            <th>Last send</th>
            <th class="!text-right">Actions</th>
          </tr>
        </thead>
        <tbody>
          {#each list as channel (channel.email_channel_id)}
            <tr>
              <td>
                <span class="font-medium">{channel.name}</span>
                {#if channel.is_default}
                  <Badge tone="success">default</Badge>
                {/if}
              </td>
              <td><Badge tone="primary" mono>{channel.provider}</Badge></td>
              <td class="font-mono text-xs">
                {channel.host}:{channel.port}
                <span class="text-surface-500">· {channel.security}</span>
              </td>
              <td class="text-sm">{channel.from_address}</td>
              <td>
                <StatusBadge
                  status={channel.enabled ? 'success' : 'warning'}
                  label={channel.enabled ? 'Enabled' : 'Disabled'}
                  showDot={false}
                />
              </td>
              <td class="text-surface-500 text-sm">
                {channel.last_send_at ? formatDateTime(channel.last_send_at) : '—'}
                {#if channel.last_send_status}
                  <span class="text-xs">({channel.last_send_status})</span>
                {/if}
              </td>
              <td class="text-right">
                <div class="inline-flex items-center gap-1">
                  <IconButton
                    icon={Send}
                    label={`Send a test message with ${channel.name}`}
                    onclick={() => openTest(channel)}
                  />
                  <IconButton icon={Pencil} label={`Edit ${channel.name}`} onclick={() => openEdit(channel)} />
                  <IconButton
                    icon={Trash2}
                    label={`Delete ${channel.name}`}
                    variant="danger"
                    onclick={() => remove(channel)}
                  />
                </div>
              </td>
            </tr>
          {/each}
        </tbody>
      </DataTable>
    </Card>
  {/if}
</div>

<!-- Create / Edit -->
<Dialog bind:open={showForm} title={editing ? `Edit ${editing.name}` : 'New email channel'} size="lg">
  <form class="space-y-4 p-1" onsubmit={save}>
    {#if formError}
      <Alert tone="error">{formError}</Alert>
    {/if}

    <div class="grid grid-cols-2 gap-4">
      <Select
        label="Provider"
        help="email.provider"
        bind:value={form.provider}
        onchange={(e) => applyPreset((e.currentTarget as HTMLSelectElement).value as EmailProviderKind)}
        disabled={saving}
      >
        {#each presets as p (p.provider)}
          <option value={p.provider}>{p.label}</option>
        {/each}
      </Select>
      <Input label="Name" help="email.name" placeholder="alerts-gmail" bind:value={form.name} disabled={saving} />
    </div>

    {#if preset?.docs_url}
      <Alert tone="info">
        <div class="flex items-start justify-between gap-3">
          <span>{preset.password_hint}</span>
          <a
            href={preset.docs_url}
            target="_blank"
            rel="noopener noreferrer"
            class="shrink-0 inline-flex items-center gap-1 text-primary-300 hover:text-primary-200"
          >
            Provider docs <ExternalLink size={12} />
          </a>
        </div>
      </Alert>
    {/if}

    <div class="grid grid-cols-3 gap-4">
      <div class="col-span-2">
        <Input label="SMTP host" help="email.host" placeholder="smtp.example.com" bind:value={form.host} disabled={saving} />
      </div>
      <Input label="Port" help="email.port" type="number" bind:value={form.port} disabled={saving} />
    </div>

    <Select label="Encryption" help="email.security" bind:value={form.security} disabled={saving}>
      {#each SECURITY_OPTIONS as opt (opt.value)}
        <option value={opt.value}>{opt.label}</option>
      {/each}
    </Select>

    <div class="grid grid-cols-2 gap-4">
      <Input
        label="Username"
        help="email.username"
        placeholder={preset?.username_hint ?? 'SMTP AUTH user'}
        bind:value={form.username}
        disabled={saving || usernameLocked}
      />
      <Input
        label={editing ? 'Password (leave blank to keep)' : 'Password'}
        help="email.password"
        type="password"
        revealable
        placeholder={editing ? '••••••••' : 'SMTP AUTH password'}
        bind:value={form.password}
        disabled={saving}
      />
    </div>
    {#if usernameLocked}
      <p class="text-xs text-surface-500 -mt-2">
        {preset?.label} requires the username <code class="font-mono">{preset?.fixed_username}</code>.
      </p>
    {/if}

    <div class="grid grid-cols-3 gap-4">
      <Input
        label="From address"
        help="email.from_address"
        placeholder="alerts@example.com"
        bind:value={form.from_address}
        disabled={saving}
      />
      <Input label="From name" help="email.from_name" placeholder="FlowWeaver" bind:value={form.from_name} disabled={saving} />
      <Input label="Reply-To" help="email.reply_to" placeholder="noc@example.com" bind:value={form.reply_to} disabled={saving} />
    </div>

    <div class="flex flex-col gap-2">
      <label class="inline-flex items-center gap-2 text-sm text-surface-900-100">
        <input type="checkbox" bind:checked={form.is_default} disabled={saving} />
        Use as the default channel for email_send steps <FieldHint id="email.is_default" />
      </label>
      <label class="inline-flex items-center gap-2 text-sm text-surface-900-100">
        <input type="checkbox" bind:checked={form.enabled} disabled={saving} />
        Enabled
      </label>
      <label class="inline-flex items-center gap-2 text-sm text-surface-900-100">
        <input type="checkbox" bind:checked={form.allow_private_network} disabled={saving} />
        Allow a relay on a private network (self-hosted) <FieldHint id="email.allow_private_network" />
      </label>
      <label class="inline-flex items-center gap-2 text-sm text-surface-900-100">
        <input type="checkbox" bind:checked={form.tls_skip_verify} disabled={saving} />
        Skip TLS certificate verification (internal relays only) <FieldHint id="email.tls_skip_verify" />
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

<!-- Test send -->
<Dialog bind:open={showTest} title={testChannel ? `Test ${testChannel.name}` : 'Test send'} size="md">
  {#if testChannel}
    <form class="space-y-4 p-1" onsubmit={runTest}>
      <p class="text-sm text-surface-600-400">
        Sends a real message through
        <code class="font-mono text-xs">{testChannel.host}:{testChannel.port}</code>
        as <code class="font-mono text-xs">{testChannel.from_address}</code>.
      </p>

      <Input label="To" help="email.test_to" placeholder="you@example.com" bind:value={testTo} disabled={testing} />
      <Input
        label="Subject (optional)"
        help="email.test_subject"
        placeholder="FlowWeaver test message"
        bind:value={testSubject}
        disabled={testing}
      />
      <Textarea
        label="Body (optional)"
        help="email.test_body"
        rows={3}
        placeholder="Leave empty for the default test body."
        bind:value={testBody}
        disabled={testing}
      />

      {#if testResult}
        <Alert tone={testResult.ok ? 'success' : 'error'}>
          {#if testResult.ok}
            Delivered in {testResult.elapsed_ms} ms.
            {#if testResult.message_id}
              <span class="block font-mono text-xs mt-1 break-all">{testResult.message_id}</span>
            {/if}
          {:else}
            {testResult.error}
          {/if}
        </Alert>
      {/if}
    </form>
  {/if}
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showTest = false)} disabled={testing}>Close</Button>
    <Button variant="primary" icon={Send} onclick={() => runTest()} loading={testing} disabled={!testTo.trim()}>
      Send test
    </Button>
  {/snippet}
</Dialog>
