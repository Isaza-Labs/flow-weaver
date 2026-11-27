<script lang="ts">
  // The Environments tab of /admin/python: admin-defined uv virtualenvs. It was
  // the whole /admin/python-environments page, which now redirects to
  // /admin/python?tab=environments.
  //
  // `included_packages` are the pip specs Packages-tab rows target at an
  // environment; they are installed with its requirements and shown here
  // read-only (edited on the Packages tab).
  import { onMount, onDestroy } from 'svelte';
  import {
    pythonEnvironments,
    errorMessage,
    type PythonEnvironment,
    type CreatePythonEnvironmentBody,
    type UpdatePythonEnvironmentBody,
  } from '$lib/api/client';
  import {
    Hint, FieldHint, Card, DataTable, Badge, Button, IconButton, Dialog,
    Input, Textarea, Checkbox, Alert, Spinner, EmptyState, ErrorState, toast, confirm, formatDateTime,
  } from '$lib/components/ui';
  import { PYTHON_ENVIRONMENTS_GUIDE } from '$lib/guides';
  import { Plus, Trash2, RefreshCw, Boxes, RotateCw, Pencil, ChevronDown, ChevronRight } from 'lucide-svelte';

  // Mirrors the backend rule (PythonEnvironmentService): lowercase slug, 3–50 chars.
  const NAME_RE = /^[a-z0-9][a-z0-9-]{1,48}[a-z0-9]$/;

  let list = $state<PythonEnvironment[]>([]);
  let loading = $state(true);
  let loadError = $state<unknown>(null);

  // Row ids whose resolved packages / last error are expanded.
  let expanded = $state<Record<string, boolean>>({});

  type Form = {
    name: string;
    description: string;
    python_version: string;
    requirements: string;
    is_ssh_fallback: boolean;
  };
  const emptyForm = (): Form => ({
    name: '', description: '', python_version: '', requirements: '', is_ssh_fallback: false,
  });

  let showForm = $state(false);
  let editing = $state<PythonEnvironment | null>(null);
  let form = $state<Form>(emptyForm());
  let saving = $state(false);
  let formError = $state<string | null>(null);

  // Quiet poll while anything is still pending/building.
  let pollTimer: ReturnType<typeof setTimeout> | null = null;
  let destroyed = false;

  onMount(load);
  onDestroy(() => {
    destroyed = true;
    if (pollTimer) clearTimeout(pollTimer);
  });

  async function load() {
    loading = true;
    loadError = null;
    try {
      list = (await pythonEnvironments.list()).data;
      schedulePoll();
    } catch (e) {
      loadError = e;
    } finally {
      loading = false;
    }
  }

  function isBusy(e: PythonEnvironment) {
    return e.status === 'pending' || e.status === 'building';
  }

  function schedulePoll() {
    if (pollTimer) { clearTimeout(pollTimer); pollTimer = null; }
    if (destroyed || !list.some(isBusy)) return;
    pollTimer = setTimeout(async () => {
      try {
        list = (await pythonEnvironments.list()).data;
      } catch { /* transient — keep polling */ }
      schedulePoll();
    }, 4000);
  }

  function replace(updated: PythonEnvironment) {
    list = list.map((x) => (x.python_environment_id === updated.python_environment_id ? updated : x));
  }

  function openCreate() {
    editing = null;
    form = emptyForm();
    formError = null;
    showForm = true;
  }

  function openEdit(e: PythonEnvironment) {
    editing = e;
    form = {
      name: e.name,
      description: e.description ?? '',
      python_version: e.python_version ?? '',
      requirements: e.requirements ?? '',
      is_ssh_fallback: e.is_ssh_fallback,
    };
    formError = null;
    showForm = true;
  }

  async function save(event?: SubmitEvent) {
    event?.preventDefault();
    if (saving) return;
    formError = null;
    const name = form.name.trim();
    if (!NAME_RE.test(name)) {
      formError = 'Name must be 3–50 lowercase letters, digits or dashes, starting and ending with a letter or digit.';
      return;
    }
    if (name === 'system') { formError = '"system" is reserved for the default interpreter.'; return; }
    if (!form.requirements.trim()) { formError = 'Requirements are required'; return; }

    saving = true;
    try {
      if (editing) {
        // Requirements are sent back as shown: masked credential lines left
        // untouched are restored server-side from the stored text.
        const body: UpdatePythonEnvironmentBody = {
          name,
          description: form.description.trim(),
          python_version: form.python_version.trim(), // "" clears → image python3
          requirements: form.requirements,
          is_ssh_fallback: form.is_ssh_fallback,
        };
        const updated = await pythonEnvironments.update(editing.python_environment_id, body);
        replace(updated);
        toast.success(`Saved ${updated.name}`, {
          description: isBusy(updated) ? 'Rebuilding in the background…' : undefined,
        });
      } else {
        const body: CreatePythonEnvironmentBody = {
          name,
          description: form.description.trim() || null,
          python_version: form.python_version.trim() || null,
          requirements: form.requirements,
          is_ssh_fallback: form.is_ssh_fallback,
        };
        const created = await pythonEnvironments.create(body);
        list = [created, ...list];
        toast.success(`Added ${created.name}`, { description: 'Building in the background…' });
      }
      showForm = false;
      schedulePoll();
    } catch (e) {
      formError = errorMessage(e);
    } finally {
      saving = false;
    }
  }

  async function rebuild(e: PythonEnvironment) {
    try {
      replace(await pythonEnvironments.rebuild(e.python_environment_id));
      toast.success(`Rebuilding ${e.name}`);
      schedulePoll();
    } catch (err) {
      toast.fromError(err, 'Rebuild failed');
    }
  }

  async function remove(e: PythonEnvironment) {
    const ok = await confirm({
      title: `Delete ${e.name}?`,
      message: e.is_ssh_fallback
        ? 'This is the SSH fallback environment — SSH steps will no longer retry with it.'
        : 'Devices and snippets must not reference it any more. Packages that install into it stop doing so.',
      confirmLabel: 'Delete',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await pythonEnvironments.remove(e.python_environment_id);
      list = list.filter((x) => x.python_environment_id !== e.python_environment_id);
      toast.success('Deleted');
    } catch (err) {
      toast.fromError(err, 'Delete failed');
    }
  }

  function toggle(id: string) {
    expanded = { ...expanded, [id]: !expanded[id] };
  }

  function statusTone(s: string): 'success' | 'warning' | 'error' | 'info' {
    if (s === 'ready') return 'success';
    if (s === 'failed') return 'error';
    if (s === 'building') return 'info';
    return 'warning'; // pending
  }
</script>

<div class="space-y-6">
  <!-- The tab's own description, ⓘ hint and actions. Not a PageHeader: the
       /admin/python page already renders the one <h1>. -->
  <div class="flex items-start justify-between gap-4 flex-wrap">
    <div class="min-w-0 flex-1 flex items-start gap-1.5">
      <p class="text-sm text-surface-600-400">Pinned virtualenvs built with uv on the worker. Devices (SSH), Python snippets and Ansible playbooks can choose one instead of the system python3.</p>
      <Hint entry={PYTHON_ENVIRONMENTS_GUIDE} label="About Python environments" />
    </div>
    <div class="flex items-center gap-2 flex-wrap justify-end">
      <Button variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
      <Button variant="primary" icon={Plus} onclick={openCreate}>Add environment</Button>
    </div>
  </div>

  <Alert tone="warning">
    Building an environment runs <code>uv pip install</code> on the worker, which executes
    third-party packaging code — add only packages you trust. Python snippets keep every
    sandbox rule (import allow-list, isolation, timeouts) whatever environment they use.
  </Alert>

  {#if loading}
    <Spinner size="lg" label="Loading environments…" />
  {:else if loadError}
    <Card padding="none"><ErrorState error={loadError} onRetry={load} /></Card>
  {:else if list.length === 0}
    <Card padding="none">
      <EmptyState
        icon={Boxes}
        title="No Python environments"
        description="Everything runs with the system python3. Add an environment to pin different package versions for some devices or snippets."
      >
        {#snippet actions()}
          <Button variant="primary" icon={Plus} onclick={openCreate}>Add environment</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else}
    <Card padding="none">
      <DataTable caption="Python environments">
        <thead>
          <tr>
            <th>Name</th>
            <th>Python</th>
            <th>Status</th>
            <th>Built</th>
            <th class="!text-right">Actions</th>
          </tr>
        </thead>
        <tbody>
          {#each list as e (e.python_environment_id)}
            <tr>
              <td>
                <button
                  type="button"
                  class="inline-flex items-center gap-1 text-left"
                  aria-expanded={!!expanded[e.python_environment_id]}
                  onclick={() => toggle(e.python_environment_id)}
                >
                  {#if expanded[e.python_environment_id]}
                    <ChevronDown class="size-3.5 text-surface-500" />
                  {:else}
                    <ChevronRight class="size-3.5 text-surface-500" />
                  {/if}
                  <span class="font-mono font-medium">{e.name}</span>
                </button>
                {#if e.is_ssh_fallback}<Badge tone="primary">SSH fallback</Badge>{/if}
                {#if e.description}
                  <div class="text-xs text-surface-500 mt-0.5 max-w-md truncate" title={e.description}>{e.description}</div>
                {/if}
              </td>
              <td class="text-surface-500 text-sm font-mono">{e.python_version ?? 'system'}</td>
              <td>
                <Badge tone={statusTone(e.status)}>{e.status}</Badge>
                {#if isBusy(e)}<Spinner size="sm" />{/if}
                {#if e.status === 'failed' && e.last_error}
                  <div class="text-xs text-error-300 mt-0.5 max-w-md truncate" title={e.last_error}>{e.last_error}</div>
                {/if}
              </td>
              <td class="text-surface-500 text-sm">{e.built_at ? formatDateTime(e.built_at) : '—'}</td>
              <td class="text-right">
                <div class="inline-flex items-center gap-1">
                  <IconButton icon={Pencil} label={`Edit ${e.name}`} onclick={() => openEdit(e)} />
                  <IconButton
                    icon={RotateCw}
                    label={`Rebuild ${e.name}`}
                    disabled={isBusy(e)}
                    onclick={() => rebuild(e)}
                  />
                  <IconButton icon={Trash2} label={`Delete ${e.name}`} variant="danger" onclick={() => remove(e)} />
                </div>
              </td>
            </tr>
            {#if expanded[e.python_environment_id]}
              <tr>
                <td colspan="5">
                  <div class="grid gap-4 md:grid-cols-2 py-2">
                    <div class="space-y-3">
                      <div>
                        <div class="text-xs font-medium text-surface-600-400 mb-1">Requirements</div>
                        <pre class="text-xs font-mono whitespace-pre-wrap bg-surface-100-900 rounded p-2 max-h-64 overflow-auto">{e.requirements}</pre>
                      </div>
                      {@render included(e)}
                    </div>
                    <div>
                      <div class="text-xs font-medium text-surface-600-400 mb-1">
                        Resolved packages {#if e.requirements_hash}<span class="font-mono text-surface-500">· {e.requirements_hash.slice(0, 12)}</span>{/if}
                      </div>
                      {#if e.resolved_packages}
                        <pre class="text-xs font-mono whitespace-pre-wrap bg-surface-100-900 rounded p-2 max-h-64 overflow-auto">{e.resolved_packages}</pre>
                      {:else}
                        <p class="text-xs text-surface-500">Not built yet.</p>
                      {/if}
                    </div>
                    {#if e.last_error}
                      <div class="md:col-span-2">
                        <div class="text-xs font-medium text-surface-600-400 mb-1">Last error</div>
                        <pre class="text-xs font-mono whitespace-pre-wrap text-error-300 bg-surface-100-900 rounded p-2 max-h-64 overflow-auto">{e.last_error}</pre>
                      </div>
                    {/if}
                  </div>
                </td>
              </tr>
            {/if}
          {/each}
        </tbody>
      </DataTable>
    </Card>
  {/if}
</div>

<!-- Read-only: the specs the Packages tab targets at this environment. -->
{#snippet included(e: PythonEnvironment)}
  <div>
    <div class="text-xs font-medium text-surface-600-400 mb-1 inline-flex items-center gap-1">
      Included from Packages <FieldHint id="pyenvs.included_packages" />
    </div>
    {#if e.included_packages.length > 0}
      <div class="flex flex-wrap gap-1.5">
        {#each e.included_packages as spec (spec)}
          <Badge tone="neutral" mono>{spec}</Badge>
        {/each}
      </div>
    {:else}
      <p class="text-xs text-surface-500">None.</p>
    {/if}
    <p class="text-xs text-surface-500 mt-1">
      Managed on the <a class="underline" href="/admin/python?tab=packages">Packages</a> tab (Install in).
    </p>
  </div>
{/snippet}

<Dialog bind:open={showForm} title={editing ? `Edit ${editing.name}` : 'Add Python environment'} size="lg">
  <form class="space-y-4 p-1" onsubmit={save}>
    {#if formError}<Alert tone="error">{formError}</Alert>{/if}

    <Input
      label="Name"
      help="pyenvs.name"
      placeholder="e.g. ssh-legacy"
      bind:value={form.name}
      disabled={saving}
    />
    <Input
      label="Description (optional)"
      help="pyenvs.description"
      placeholder="Why this environment exists"
      bind:value={form.description}
      disabled={saving}
    />
    <Input
      label="Python version (optional)"
      help="pyenvs.python_version"
      placeholder="empty = the image's python3, e.g. 3.11"
      bind:value={form.python_version}
      disabled={saving}
    />
    <Textarea
      label="Requirements"
      help="pyenvs.requirements"
      rows={8}
      class="font-mono text-xs"
      placeholder={'netmiko>=4,<5\nparamiko>=3.5,<5'}
      bind:value={form.requirements}
      disabled={saving}
    />
    {#if editing}
      <p class="text-xs text-surface-500 -mt-2">
        Changing the requirements or the Python version rebuilds the environment. Credentials in
        URLs are shown as <code>***</code>; leave those lines as they are to keep them.
      </p>
      {@render included(editing)}
    {/if}
    <Checkbox
      label="SSH fallback — retry SSH steps with this environment on “no acceptable host key”"
      help="pyenvs.is_ssh_fallback"
      bind:checked={form.is_ssh_fallback}
      disabled={saving}
    />
  </form>
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showForm = false)} disabled={saving}>Cancel</Button>
    <Button variant="primary" onclick={() => save()} loading={saving}>{editing ? 'Save' : 'Add'}</Button>
  {/snippet}
</Dialog>
