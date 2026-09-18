<script lang="ts">
  import { onMount, onDestroy } from 'svelte';
  import {
    allowedPythonModules,
    errorMessage,
    type AllowedPythonModule,
    type CreateAllowedPythonModuleBody,
  } from '$lib/api/client';
  import {
    PageHeader, Card, DataTable, Badge, Button, IconButton, Dialog,
    Input, Select, Alert, Spinner, EmptyState, ErrorState, toast, confirm, formatDateTime,
  } from '$lib/components/ui';
  import { Plus, Trash2, RefreshCw, Package, RotateCw } from 'lucide-svelte';

  // Built-in safe modules — always importable, shown read-only for context so
  // admins don't re-add them. Mirrors PythonHandler.SafeModules.
  const BUILTIN = [
    'json', 'math', 'datetime', 're', 'ipaddress', 'flowweaver_runtime',
    'csv', 'io', 'base64', 'string', 'collections', 'itertools', 'hashlib', 'time',
  ];

  let list = $state<AllowedPythonModule[]>([]);
  let loading = $state(true);
  let loadError = $state<unknown>(null);

  let showForm = $state(false);
  let form = $state<{ import_name: string; source: string; pip_spec: string }>({
    import_name: '', source: 'pip', pip_spec: '',
  });
  let saving = $state(false);
  let formError = $state<string | null>(null);

  // Quiet poll while anything is still installing.
  let pollTimer: ReturnType<typeof setTimeout> | null = null;

  onMount(load);
  onDestroy(() => { if (pollTimer) clearTimeout(pollTimer); });

  async function load() {
    loading = true;
    loadError = null;
    try {
      const res = await allowedPythonModules.list();
      list = res.data;
      schedulePoll();
    } catch (e) {
      loadError = e;
    } finally {
      loading = false;
    }
  }

  function schedulePoll() {
    if (pollTimer) { clearTimeout(pollTimer); pollTimer = null; }
    const busy = list.some((m) => m.status === 'pending' || m.status === 'installing');
    if (!busy) return;
    pollTimer = setTimeout(async () => {
      try {
        const res = await allowedPythonModules.list();
        list = res.data;
      } catch { /* transient — keep polling */ }
      schedulePoll();
    }, 3000);
  }

  function openCreate() {
    form = { import_name: '', source: 'pip', pip_spec: '' };
    formError = null;
    showForm = true;
  }

  async function save(event?: SubmitEvent) {
    event?.preventDefault();
    if (saving) return;
    formError = null;
    if (!form.import_name.trim()) { formError = 'Import name is required'; return; }
    saving = true;
    try {
      const body: CreateAllowedPythonModuleBody = {
        import_name: form.import_name.trim(),
        source: form.source,
        pip_spec: form.source === 'pip' && form.pip_spec.trim() ? form.pip_spec.trim() : undefined,
      };
      const created = await allowedPythonModules.create(body);
      list = [created, ...list];
      toast.success(`Added ${created.import_name}`, {
        description: created.source === 'pip' ? 'Installing in the background…' : undefined,
      });
      showForm = false;
      schedulePoll();
    } catch (e) {
      formError = errorMessage(e);
    } finally {
      saving = false;
    }
  }

  async function remove(m: AllowedPythonModule) {
    const ok = await confirm({
      title: `Remove ${m.import_name}?`,
      message: 'Snippets will no longer be allowed to import it.',
      confirmLabel: 'Remove',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await allowedPythonModules.remove(m.allowed_python_module_id);
      list = list.filter((x) => x.allowed_python_module_id !== m.allowed_python_module_id);
      toast.success('Removed');
    } catch (e) {
      toast.fromError(e, 'Remove failed');
    }
  }

  async function retry(m: AllowedPythonModule) {
    try {
      const updated = await allowedPythonModules.retry(m.allowed_python_module_id);
      list = list.map((x) => (x.allowed_python_module_id === updated.allowed_python_module_id ? updated : x));
      toast.success('Re-queued for install');
      schedulePoll();
    } catch (e) {
      toast.fromError(e, 'Retry failed');
    }
  }

  function statusTone(s: string): 'success' | 'warning' | 'error' | 'info' {
    if (s === 'ready') return 'success';
    if (s === 'failed') return 'error';
    return 'warning'; // pending / installing
  }
</script>

<svelte:head><title>Python packages · FlowWeaver</title></svelte:head>

<div class="p-6 space-y-6">
  <PageHeader
    title="Python packages"
    description="Extra modules python_snippet scripts may import. Adding a pip package installs it on the worker."
  >
    {#snippet actions()}
      <Button variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
      <Button variant="primary" icon={Plus} onclick={openCreate}>Add module</Button>
    {/snippet}
  </PageHeader>

  <Alert tone="warning">
    Allowing a module lets every <code>python_snippet</code> import it.
    Installing a pip package runs <code>pip install</code> on the worker — add only
    packages you trust. The sandbox still blocks <code>exec</code>/<code>eval</code>/<code>open</code>
    and runs with no network (unless the snippet is network-enabled).
  </Alert>

  {#if loading}
    <Spinner size="lg" label="Loading modules…" />
  {:else if loadError}
    <Card padding="none"><ErrorState error={loadError} onRetry={load} /></Card>
  {:else if list.length === 0}
    <Card padding="none">
      <EmptyState
        icon={Package}
        title="No extra modules yet"
        description="The built-in stdlib safe-list is always available. Add a module to allow more imports."
      >
        {#snippet actions()}
          <Button variant="primary" icon={Plus} onclick={openCreate}>Add module</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else}
    <Card padding="none">
      <DataTable caption="Allowed python modules">
        <thead>
          <tr>
            <th>Import name</th>
            <th>Source</th>
            <th>Status</th>
            <th>Installed</th>
            <th class="!text-right">Actions</th>
          </tr>
        </thead>
        <tbody>
          {#each list as m (m.allowed_python_module_id)}
            <tr>
              <td>
                <span class="font-mono font-medium">{m.import_name}</span>
                {#if m.source === 'pip' && m.pip_spec && m.pip_spec !== m.import_name}
                  <span class="text-xs text-surface-500"> · {m.pip_spec}</span>
                {/if}
              </td>
              <td><Badge tone={m.source === 'pip' ? 'primary' : 'neutral'} mono>{m.source}</Badge></td>
              <td>
                <Badge tone={statusTone(m.status)}>{m.status}</Badge>
                {#if m.status === 'failed' && m.error}
                  <div class="text-xs text-error-300 mt-0.5 max-w-md truncate" title={m.error}>{m.error}</div>
                {/if}
              </td>
              <td class="text-surface-500 text-sm">
                {m.installed_version ?? (m.source === 'stdlib' ? 'stdlib' : '—')}
              </td>
              <td class="text-right">
                <div class="inline-flex items-center gap-1">
                  {#if m.source === 'pip' && m.status === 'failed'}
                    <IconButton icon={RotateCw} label={`Retry install of ${m.import_name}`} onclick={() => retry(m)} />
                  {/if}
                  <IconButton icon={Trash2} label={`Remove ${m.import_name}`} variant="danger" onclick={() => remove(m)} />
                </div>
              </td>
            </tr>
          {/each}
        </tbody>
      </DataTable>
    </Card>
  {/if}

  <Card>
    <div class="text-sm font-medium text-surface-900-100 mb-1">Built-in modules (always allowed)</div>
    <p class="text-xs text-surface-500 mb-2">Available to every snippet without adding them here.</p>
    <div class="flex flex-wrap gap-1.5">
      {#each BUILTIN as b (b)}
        <Badge tone="neutral" mono>{b}</Badge>
      {/each}
    </div>
  </Card>
</div>

<!-- Add module -->
<Dialog bind:open={showForm} title="Add python module" size="md">
  <form class="space-y-4 p-1" onsubmit={save}>
    {#if formError}<Alert tone="error">{formError}</Alert>{/if}

    <Input
      label="Import name"
      help="pypackages.import_name"
      placeholder="e.g. django, yaml, bs4"
      bind:value={form.import_name}
      disabled={saving}
    />
    <p class="text-xs text-surface-500 -mt-2">
      Exactly what the script writes: <code>import &lt;name&gt;</code>. Top-level module only.
    </p>

    <Select label="Source" help="pypackages.source" bind:value={form.source} disabled={saving}>
      <option value="pip">pip — install from PyPI</option>
      <option value="stdlib">stdlib — already on the interpreter</option>
    </Select>

    {#if form.source === 'pip'}
      <Input
        label="PyPI package (optional)"
        help="pypackages.pip_spec"
        placeholder="defaults to the import name — e.g. Django==5.0, beautifulsoup4"
        bind:value={form.pip_spec}
        disabled={saving}
      />
      <p class="text-xs text-surface-500 -mt-2">
        The pip requirement to install. Set this when it differs from the import name
        (e.g. import <code>bs4</code> → install <code>beautifulsoup4</code>) or to pin a version.
      </p>
    {/if}
  </form>
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showForm = false)} disabled={saving}>Cancel</Button>
    <Button variant="primary" onclick={() => save()} loading={saving}>Add</Button>
  {/snippet}
</Dialog>
