<script lang="ts">
  // Device credentials — auth material (username + password or SSH
  // private key) that Device rows reference so handlers like ssh/netconf
  // can authenticate. Plaintext never leaves the backend; the list view
  // shows metadata + a "key uploaded" indicator for key-auth rows.

  import { onMount } from 'svelte';
  import { credentials, type Credential, errorMessage } from '$lib/api/client';
  import {
    PageHeader, Card, DataTable, Button, IconButton, Input, Textarea, Select,
    Dialog, Alert, Spinner, EmptyState, SearchInput, StatusBadge, Badge,
    formatDateTime, toast, confirm,
  } from '$lib/components/ui';
  import { KeyRound, Plus, Pencil, Trash2, RefreshCw } from 'lucide-svelte';

  let rows = $state<Credential[]>([]);
  let loading = $state(true);
  let loadError = $state<string | null>(null);

  // Client-side filter over name / type / username — the columns a human
  // scans. credentials.list() returns the full set (no server paging), so
  // filtering in memory keeps every keystroke instant and avoids a
  // round-trip. Match is substring, case-insensitive.
  let query = $state('');
  const filteredRows = $derived.by(() => {
    const q = query.trim().toLowerCase();
    if (!q) return rows;
    return rows.filter((c) => {
      const hay = [c.name, c.type, c.username]
        .filter(Boolean)
        .map((s) => s!.toLowerCase());
      return hay.some((h) => h.includes(q));
    });
  });

  // Map each credential type to a Badge tone so the kind is scannable at a
  // glance instead of mono text. Secret-bearing tokens (PAT / API key) lean
  // warning; transport logins (ssh / netconf) primary; snmp info; unknown
  // user-defined strings fall back to neutral.
  function typeTone(t: string): 'primary' | 'success' | 'warning' | 'info' | 'neutral' {
    switch (t) {
      case 'ssh':
      case 'netconf':
        return 'primary';
      case 'snmp_v3':
        return 'info';
      case 'git_token':
      case 'api_key':
        return 'warning';
      case 'http_basic':
        return 'success';
      default:
        return 'neutral';
    }
  }

  // `editing === null` means "create". Update otherwise. Password /
  // private-key fields always start blank on edit — the user re-enters
  // them to rotate, leaves them empty to keep the current ciphertext.
  let showDialog = $state(false);
  let editing = $state<Credential | null>(null);
  let name = $state('');
  let type = $state('ssh');
  let username = $state('');
  let authMethod = $state<'password' | 'key'>('password');
  let password = $state('');
  let privateKey = $state('');
  let keyPassphrase = $state('');
  let saving = $state(false);
  let formError = $state<string | null>(null);

  // The Type field used to be a free-text input — users had to know
  // strings like "git_token" exist to use Git over HTTPS. The Select
  // surfaces every known type the platform consumes; "custom" allows
  // ad-hoc strings without us pinning the list.
  type KnownType =
    | 'ssh'
    | 'netconf'
    | 'snmp_v3'
    | 'git_token'
    | 'api_key'
    | 'http_basic'
    | 'custom';
  const KNOWN_TYPES: { value: KnownType; label: string; hint: string }[] = [
    { value: 'ssh', label: 'ssh — username + password / key for SSH and NETCONF over SSH', hint: 'Used by SSH and NETCONF handlers.' },
    { value: 'netconf', label: 'netconf — dedicated NETCONF user (when separate from SSH)', hint: 'Used when NETCONF auth differs from SSH.' },
    { value: 'snmp_v3', label: 'snmp_v3 — SNMPv3 user, auth + priv passphrases', hint: 'Used by the SNMPv3 handler.' },
    { value: 'git_token', label: 'git_token — Personal Access Token for HTTPS Git', hint: 'PAT in the password field. Username = scope label or "git".' },
    { value: 'api_key', label: 'api_key — bearer / header token for REST integrations', hint: 'Token in the password field; used by integration_action.' },
    { value: 'http_basic', label: 'http_basic — username + password for HTTP Basic auth', hint: 'For REST integrations that need Basic auth.' },
    { value: 'custom', label: 'custom — type your own…', hint: 'Use only if no preset matches.' },
  ];
  let typeMode = $state<KnownType>('ssh');
  let customType = $state('');

  // Keep the visible type string in sync with the Select choice. When
  // the user picks "custom", the free-text input becomes editable.
  function syncTypeFromMode() {
    if (typeMode === 'custom') {
      type = customType.trim();
    } else {
      type = typeMode;
    }
  }
  function applyExistingType(t: string) {
    const known = KNOWN_TYPES.find((k) => k.value === t);
    if (known) {
      typeMode = known.value;
      customType = '';
    } else {
      typeMode = 'custom';
      customType = t;
    }
  }

  onMount(load);

  async function load() {
    loading = true;
    loadError = null;
    try {
      const res = await credentials.list();
      rows = res.data;
    } catch (e) {
      loadError = errorMessage(e);
      toast.fromError(e, "Couldn't load credentials");
    } finally {
      loading = false;
    }
  }

  function openCreate() {
    editing = null;
    name = '';
    type = 'ssh';
    typeMode = 'ssh';
    customType = '';
    username = '';
    authMethod = 'password';
    password = '';
    privateKey = '';
    keyPassphrase = '';
    formError = null;
    showDialog = true;
  }

  function openEdit(c: Credential) {
    editing = c;
    name = c.name;
    type = c.type ?? 'ssh';
    applyExistingType(type);
    username = c.username ?? '';
    authMethod = c.auth_method ?? 'password';
    password = '';
    privateKey = '';
    keyPassphrase = '';
    formError = null;
    showDialog = true;
  }

  function closeDialog() {
    if (saving) return;
    showDialog = false;
    editing = null;
  }

  function validate(): string | null {
    if (!name.trim()) return 'Name is required.';
    if (!type.trim()) return 'Type is required.';
    if (!editing) {
      // Creating: require matching secret material for the chosen method.
      if (authMethod === 'password' && !password) return 'Password is required for password auth.';
      if (authMethod === 'key' && !privateKey) return 'Private key is required for key auth.';
    } else if (authMethod === 'key' && !privateKey && !editing.has_private_key) {
      // Edit path: switching to key auth on a row that has no key yet
      // requires pasting one now — otherwise the backend 400s.
      return 'Private key is required when switching to key auth.';
    }
    return null;
  }

  async function save() {
    if (saving) return;
    formError = validate();
    if (formError) return;

    saving = true;
    try {
      if (editing) {
        // Only send fields the user actually changed. Empty password /
        // private_key leaves the existing ciphertext in place; the
        // backend's CredentialService is lenient on partial updates.
        const body: Record<string, unknown> = {
          name: name.trim(),
          type: type.trim(),
          username: username.trim() || undefined,
          auth_method: authMethod,
        };
        if (password) body.password = password;
        if (privateKey) body.private_key = privateKey;
        if (keyPassphrase) body.key_passphrase = keyPassphrase;
        await credentials.update(editing.credential_id, body);
        toast.success('Credential updated');
      } else {
        await credentials.create({
          name: name.trim(),
          type: type.trim(),
          username: username.trim() || undefined,
          auth_method: authMethod,
          password: authMethod === 'password' ? password : undefined,
          private_key: authMethod === 'key' ? privateKey : undefined,
          key_passphrase: authMethod === 'key' && keyPassphrase ? keyPassphrase : undefined,
        });
        toast.success('Credential created');
      }
      showDialog = false;
      await load();
    } catch (e) {
      formError = errorMessage(e);
    } finally {
      saving = false;
    }
  }

  async function del(c: Credential) {
    if (!(await confirm({
      title: `Delete credential "${c.name}"?`,
      message: 'Any device referencing it will lose SSH/NETCONF access until reassigned.',
      tone: 'danger',
      confirmLabel: 'Delete',
    }))) return;
    try {
      await credentials.delete(c.credential_id);
      toast.success('Credential deleted');
      await load();
    } catch (e) {
      toast.fromError(e, 'Delete failed');
    }
  }
</script>

<svelte:head><title>Credentials · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title="Credentials"
    description="Auth material (username + password or SSH private key) that devices use at workflow runtime. Plaintext is stored encrypted and never returned."
  >
    {#snippet actions()}
      <Button variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
      <Button variant="primary" icon={Plus} onclick={openCreate}>New credential</Button>
    {/snippet}
  </PageHeader>

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" /></div>
  {:else if loadError}
    <Alert tone="error">{loadError}</Alert>
  {:else if rows.length === 0}
    <Card padding="none">
      <EmptyState
        icon={KeyRound}
        title="No credentials yet"
        description="Add a credential before running ssh / netconf workflows. Devices reference a credential by id."
      >
        {#snippet actions()}
          <Button variant="primary" icon={Plus} onclick={openCreate}>Create first credential</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else}
    <div class="flex items-center justify-between gap-2">
      <SearchInput
        bind:value={query}
        placeholder="Filter by name, type or username…"
        width="w-80"
      />
      {#if query}
        <span class="text-xs text-surface-500">
          {filteredRows.length} / {rows.length} match{filteredRows.length === 1 ? '' : 'es'}
        </span>
      {/if}
    </div>

    {#if filteredRows.length === 0}
      <Card padding="none">
        <EmptyState
          icon={KeyRound}
          title="No credentials match that filter"
          description={`Nothing contains "${query}" in its name, type or username.`}
        >
          {#snippet actions()}
            <Button variant="ghost" onclick={() => (query = '')}>Clear filter</Button>
          {/snippet}
        </EmptyState>
      </Card>
    {:else}
    <DataTable caption="Device credentials with their type, username, auth method and last update">
      <thead>
        <tr>
          <th>Name</th>
          <th>Type</th>
          <th>Username</th>
          <th>Auth</th>
          <th>Updated</th>
          <th class="!text-right">Actions</th>
        </tr>
      </thead>
      <tbody>
        {#each filteredRows as c (c.credential_id)}
          <tr>
            <td>
              <div class="flex items-center gap-2">
                <KeyRound size={12} class="text-surface-500" />
                <span class="font-medium text-surface-900-100">{c.name}</span>
              </div>
            </td>
            <td><Badge tone={typeTone(c.type)} mono>{c.type}</Badge></td>
            <td class="text-surface-700-300">{c.username ?? '—'}</td>
            <td>
              {#if c.auth_method === 'key'}
                <StatusBadge status={c.has_private_key ? 'success' : 'failed'}
                             label={c.has_private_key ? 'key' : 'key missing'}
                             showDot={false} />
              {:else}
                <StatusBadge status="success" label="password" showDot={false} />
              {/if}
            </td>
            <td class="text-surface-500 text-[11px] tabular-nums whitespace-nowrap">{formatDateTime(c.updated_at)}</td>
            <td class="text-right">
              <div class="inline-flex items-center gap-1">
                <IconButton icon={Pencil} label={`Edit ${c.name}`} onclick={() => openEdit(c)} />
                <IconButton icon={Trash2} label={`Delete ${c.name}`} variant="danger" onclick={() => del(c)} />
              </div>
            </td>
          </tr>
        {/each}
      </tbody>
    </DataTable>
    <p class="text-xs text-surface-500">
      {#if query}
        Showing {filteredRows.length} of {rows.length} credential{rows.length === 1 ? '' : 's'}
      {:else}
        {rows.length} credential{rows.length === 1 ? '' : 's'}
      {/if}
    </p>
    {/if}
  {/if}
</div>

<Dialog bind:open={showDialog} title={editing ? `Edit ${editing.name}` : 'New credential'} size="md">
  <div class="space-y-4 p-1">
    {#if formError}
      <Alert tone="error">{formError}</Alert>
    {/if}
    <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
      <Input label="Name" help="credentials.name" placeholder="core-routers-ssh" bind:value={name} disabled={saving} />
      <div>
        <Select
          label="Type"
          help="credentials.type"
          bind:value={typeMode}
          onchange={syncTypeFromMode}
          disabled={saving}
        >
          {#each KNOWN_TYPES as opt}
            <option value={opt.value}>{opt.label}</option>
          {/each}
        </Select>
        {#if typeMode === 'custom'}
          <Input
            class="mt-2"
            placeholder="custom-type"
            bind:value={customType}
            oninput={syncTypeFromMode}
            disabled={saving}
          />
        {/if}
        <p class="text-[11px] text-surface-500 mt-1">
          {KNOWN_TYPES.find((k) => k.value === typeMode)?.hint ?? ''}
        </p>
      </div>
      <Input label="Username" help="credentials.username" placeholder="netops" bind:value={username} disabled={saving} />
      <Select label="Auth method" help="credentials.auth_method" bind:value={authMethod} disabled={saving}>
        <option value="password">Password / token</option>
        <option value="key">SSH private key</option>
      </Select>
    </div>

    {#if authMethod === 'password'}
      <Input
        label={editing ? 'New password (leave empty to keep current)' : 'Password'}
        help="credentials.password"
        type="password"
        revealable
        placeholder={editing ? '••••••••' : 'Enter password'}
        bind:value={password}
        disabled={saving}
      />
    {:else}
      <div>
        <Textarea
          label={editing
            ? (editing.has_private_key ? 'New private key (leave empty to keep current)' : 'Private key (PEM)')
            : 'Private key (PEM)'}
          help="credentials.private_key"
          placeholder={'-----BEGIN OPENSSH PRIVATE KEY-----\n…'}
          rows={8}
          bind:value={privateKey}
          disabled={saving}
        />
        <p class="text-[11px] text-surface-500 mt-1">
          The server parses the PEM before storing; malformed keys are rejected up-front so you don't find out at 3am.
        </p>
      </div>
      <Input
        label="Key passphrase (optional)"
        help="credentials.key_passphrase"
        type="password"
        revealable
        placeholder={editing ? '••••••••' : 'Leave empty if the key is not passphrase-protected'}
        bind:value={keyPassphrase}
        disabled={saving}
      />
    {/if}
  </div>

  {#snippet footer()}
    <Button variant="ghost" onclick={closeDialog} disabled={saving}>Cancel</Button>
    <Button variant="primary" onclick={save} loading={saving}>{editing ? 'Save changes' : 'Create'}</Button>
  {/snippet}
</Dialog>
