<script lang="ts">
  // The Packages tab of /admin/python: the python_snippet import allowlist and
  // the pip specs the worker installs. It was the whole /admin/python-packages
  // page, which now redirects to /admin/python?tab=packages.
  import { onMount, onDestroy } from 'svelte';
  //
  // A pip row has install targets: "System Python" (the default) and any of
  // the uv environments. An environment gets the spec by rebuilding with it in
  // its effective requirements, so the target badges show each environment's
  // build status.
  import {
    allowedPythonModules,
    pythonEnvironments,
    errorMessage,
    type AllowedPythonModule,
    type CreateAllowedPythonModuleBody,
    type UpdateAllowedPythonModuleBody,
    type PythonEnvironment,
  } from '$lib/api/client';
  import {
    Hint, FieldHint, Card, DataTable, Badge, Button, IconButton, Dialog,
    Input, Select, Checkbox, Alert, Spinner, EmptyState, ErrorState, toast, confirm,
  } from '$lib/components/ui';
  import { PYTHON_PACKAGES_GUIDE } from '$lib/guides';
  import { Plus, Trash2, RefreshCw, Package, RotateCw, Pencil } from 'lucide-svelte';

  // Built-in safe modules — always importable, shown read-only for context so
  // admins don't re-add them. Mirrors PythonHandler.SafeModules.
  const BUILTIN = [
    'json', 'math', 'datetime', 're', 'ipaddress', 'flowweaver_runtime',
    'csv', 'io', 'base64', 'string', 'collections', 'itertools', 'hashlib', 'time',
  ];

  let list = $state<AllowedPythonModule[]>([]);
  let loading = $state(true);
  let loadError = $state<unknown>(null);

  // Install-target choices. Loaded alongside the list; a failure only hides
  // the environment checkboxes (System Python still works).
  let environments = $state<PythonEnvironment[]>([]);

  type Form = {
    import_name: string;
    source: string;
    pip_spec: string;
    install_in_system: boolean;
    environment_ids: string[];
  };
  const emptyForm = (): Form => ({
    import_name: '', source: 'pip', pip_spec: '', install_in_system: true, environment_ids: [],
  });

  let showForm = $state(false);
  // Null = adding a row; otherwise the pip row being edited.
  let editing = $state<AllowedPythonModule | null>(null);
  let form = $state<Form>(emptyForm());
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
      const [res] = await Promise.all([allowedPythonModules.list(), loadEnvironments()]);
      list = res.data;
      schedulePoll();
    } catch (e) {
      loadError = e;
    } finally {
      loading = false;
    }
  }

  async function loadEnvironments() {
    try {
      environments = (await pythonEnvironments.list()).data;
    } catch {
      environments = [];
    }
  }

  const envBusy = (s: string) => s === 'pending' || s === 'building';

  function schedulePoll() {
    if (pollTimer) { clearTimeout(pollTimer); pollTimer = null; }
    // Busy while a system install runs or an environment a row targets is
    // still (re)building with it.
    const busy = list.some(
      (m) => m.status === 'pending' || m.status === 'installing' || m.environments.some((t) => envBusy(t.status)),
    );
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
    editing = null;
    form = emptyForm();
    formError = null;
    showForm = true;
    void loadEnvironments();
  }

  function openEdit(m: AllowedPythonModule) {
    editing = m;
    form = {
      import_name: m.import_name,
      source: m.source,
      pip_spec: m.pip_spec ?? '',
      install_in_system: m.install_in_system,
      environment_ids: [...m.environment_ids],
    };
    formError = null;
    showForm = true;
    void loadEnvironments();
  }

  function hasEnv(id: string) {
    return form.environment_ids.includes(id);
  }

  function setEnv(id: string, on: boolean) {
    form.environment_ids = on
      ? [...form.environment_ids.filter((x) => x !== id), id]
      : form.environment_ids.filter((x) => x !== id);
  }

  function installSummary(m: AllowedPythonModule): string | undefined {
    const where = [
      ...(m.install_in_system ? ['system Python'] : []),
      ...m.environments.map((t) => t.name),
    ];
    if (where.length === 0) return undefined;
    return `Installing in ${where.join(', ')} in the background…`;
  }

  async function save(event?: SubmitEvent) {
    event?.preventDefault();
    if (saving) return;
    formError = null;
    if (!form.import_name.trim()) { formError = 'Import name is required'; return; }
    saving = true;
    try {
      if (editing) {
        const body: UpdateAllowedPythonModuleBody = {
          pip_spec: form.pip_spec.trim(), // "" → the import name, as on create
          install_in_system: form.install_in_system,
          environment_ids: form.environment_ids,
        };
        const updated = await allowedPythonModules.update(editing.allowed_python_module_id, body);
        list = list.map((x) => (x.allowed_python_module_id === updated.allowed_python_module_id ? updated : x));
        toast.success(`Saved ${updated.import_name}`, { description: installSummary(updated) });
      } else {
        const pip = form.source === 'pip';
        const body: CreateAllowedPythonModuleBody = {
          import_name: form.import_name.trim(),
          source: form.source,
          pip_spec: pip && form.pip_spec.trim() ? form.pip_spec.trim() : undefined,
          install_in_system: pip ? form.install_in_system : undefined,
          environment_ids: pip ? form.environment_ids : undefined,
        };
        const created = await allowedPythonModules.create(body);
        list = [created, ...list];
        toast.success(`Added ${created.import_name}`, {
          description: created.source === 'pip' ? installSummary(created) : undefined,
        });
      }
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
    if (s === 'building' || s === 'installing') return 'info';
    return 'warning'; // pending
  }
</script>

<div class="space-y-6">
  <!-- The tab's own description, ⓘ hint and actions. Not a PageHeader: the
       /admin/python page already renders the one <h1>. -->
  <div class="flex items-start justify-between gap-4 flex-wrap">
    <div class="min-w-0 flex-1 flex items-start gap-1.5">
      <p class="text-sm text-surface-600-400">Extra modules python_snippet scripts may import. Adding a pip package installs it on the worker.</p>
      <Hint entry={PYTHON_PACKAGES_GUIDE} label="About Python packages" />
    </div>
    <div class="flex items-center gap-2 flex-wrap justify-end">
      <Button variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
      <Button variant="primary" icon={Plus} onclick={openCreate}>Add module</Button>
    </div>
  </div>

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
            <th>Install in</th>
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
              <td>
                {#if m.source === 'pip'}
                  <div class="flex flex-wrap gap-1">
                    {#if m.install_in_system}
                      <span title={`System Python: ${m.status}`}>
                        <Badge tone={statusTone(m.status)} mono>system · {m.status}</Badge>
                      </span>
                    {/if}
                    {#each m.environments as t (t.python_environment_id)}
                      <span title={`Environment ${t.name}: ${t.status}`}>
                        <Badge tone={statusTone(t.status)} mono>{t.name} · {t.status}</Badge>
                      </span>
                    {/each}
                    {#if !m.install_in_system && m.environments.length === 0}
                      <span class="text-xs text-surface-500" title="Allowed to import, but not installed anywhere by this row">nowhere</span>
                    {/if}
                  </div>
                {:else}
                  <span class="text-surface-500 text-sm">—</span>
                {/if}
              </td>
              <td class="text-surface-500 text-sm">
                {m.installed_version ?? (m.source === 'stdlib' ? 'stdlib' : '—')}
              </td>
              <td class="text-right">
                <div class="inline-flex items-center gap-1">
                  {#if m.source === 'pip'}
                    <IconButton icon={Pencil} label={`Edit ${m.import_name}`} onclick={() => openEdit(m)} />
                  {/if}
                  {#if m.source === 'pip' && m.install_in_system && m.status === 'failed'}
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

<!-- Add / edit module -->
<Dialog bind:open={showForm} title={editing ? `Edit ${editing.import_name}` : 'Add python module'} size="md">
  <form class="space-y-4 p-1" onsubmit={save}>
    {#if formError}<Alert tone="error">{formError}</Alert>{/if}

    <Input
      label="Import name"
      help="pypackages.import_name"
      placeholder="e.g. django, yaml, bs4"
      bind:value={form.import_name}
      disabled={saving || !!editing}
    />
    <p class="text-xs text-surface-500 -mt-2">
      Exactly what the script writes: <code>import &lt;name&gt;</code>. Top-level module only.
    </p>

    <Select label="Source" help="pypackages.source" bind:value={form.source} disabled={saving || !!editing}>
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

      <!-- Install targets: pip rows only (a stdlib row has nothing to install). -->
      <fieldset class="space-y-2">
        <legend class="label-text inline-flex items-center gap-1 mb-1">
          Install in <FieldHint id="pypackages.install_in" />
        </legend>
        <Checkbox
          label="System Python — the image's python3"
          help="pypackages.install_system"
          bind:checked={form.install_in_system}
          disabled={saving}
        />
        {#each environments as env (env.python_environment_id)}
          <div class="flex items-center gap-2">
            <Checkbox
              label={`Environment ${env.name}`}
              help="pypackages.install_env"
              checked={hasEnv(env.python_environment_id)}
              onChange={(on) => setEnv(env.python_environment_id, on)}
              disabled={saving}
            />
            <Badge tone={statusTone(env.status)} mono>{env.status}</Badge>
          </div>
        {:else}
          <p class="text-xs text-surface-500">
            No uv environments yet — add one on the
            <a class="underline" href="/admin/python?tab=environments">Environments</a> tab to install there too.
          </p>
        {/each}
        {#if editing && editing.install_in_system && !form.install_in_system}
          <Alert tone="info">
            Turning System Python off stops installing it there, but does not uninstall it: files from
            the earlier install stay in the shared package dir until the volume is cleaned.
          </Alert>
        {/if}
        {#if !form.install_in_system && form.environment_ids.length === 0}
          <p class="text-xs text-surface-500">
            Not installed anywhere: scripts may import it, but only an environment whose own
            requirements provide it will have it.
          </p>
        {/if}
        <p class="text-xs text-surface-500">
          Adding, removing or changing a package rebuilds every environment it affects.
        </p>
      </fieldset>
    {/if}
  </form>
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showForm = false)} disabled={saving}>Cancel</Button>
    <Button variant="primary" onclick={() => save()} loading={saving}>{editing ? 'Save' : 'Add'}</Button>
  {/snippet}
</Dialog>
