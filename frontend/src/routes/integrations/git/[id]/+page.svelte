<script lang="ts">
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import {
    git, gitWebhooks, workflows as workflowsApi, errorMessage,
    type GitRepository, type GitFileEntry, type GitBranchesResponse,
    type GitWebhook, type GitWebhookDelivery, type Workflow,
  } from '$lib/api/client';
  import {
    PageHeader, Card, Button, IconButton, Badge, Alert, Spinner, Select, Input, Textarea, Dialog,
    toast, formatTime, confirm, FieldHint,
  } from '$lib/components/ui';
  import {
    GitBranch, FolderOpen, FileText, ArrowLeft, Save, Upload, Download,
    RefreshCw, GitCommit, Webhook, Plus, Trash2, Copy, X,
  } from 'lucide-svelte';
  import { highlightCode, languageFor } from '$lib/utils/highlight';
  import { copyText } from '$lib/utils/clipboard';

  let repo = $state<GitRepository | null>(null);
  let branches = $state<GitBranchesResponse | null>(null);
  let currentBranch = $state('');
  let pathStack = $state<string[]>([]); // breadcrumb path segments
  let entries = $state<GitFileEntry[]>([]);
  let loading = $state(true);
  let listing = $state(false);
  let error = $state('');

  // File viewer / editor
  let selectedPath = $state<string | null>(null);
  let fileContent = $state('');
  let fileOriginal = $state('');
  let fileBinary = $state(false);
  let fileLoading = $state(false);
  let editing = $state(false);

  // Commit dialog
  let showCommit = $state(false);
  let commitMessage = $state('');
  let commitPush = $state(true);
  let committing = $state(false);

  // Webhooks
  let hooks = $state<GitWebhook[]>([]);
  let hooksLoading = $state(false);
  let workflowChoices = $state<Workflow[]>([]);
  let showHookForm = $state(false);
  let editingHookId = $state<string | null>(null);
  let hookName = $state('');
  let hookProvider = $state<'github' | 'gitlab' | 'generic'>('github');
  let hookSecret = $state('');
  let hookKeepSecret = $state(true);
  let hookWorkflowId = $state('');
  let hookBranches = $state<string[]>([]);
  let hookBranchDraft = $state('');
  let hookAutoPull = $state(true);
  let hookEnabled = $state(true);
  let hookAllowUnsigned = $state(false);
  let hookFormError = $state('');
  let hookSaving = $state(false);

  // Deliveries panel. `deliveriesOpen` is the bindable Dialog flag —
  // Dialog's internal X / ESC writes back to it, and we clear
  // `deliveriesFor` via onClose. Without a real $state binding the
  // dialog would re-open on every render because the parent's
  // `open=expression` would re-evaluate true.
  let deliveriesOpen = $state(false);
  let deliveriesFor = $state<string | null>(null);
  let deliveries = $state<GitWebhookDelivery[]>([]);
  let deliveriesLoading = $state(false);

  const repoId = $derived(page.params.id ?? '');
  const currentPath = $derived(pathStack.join('/'));
  const isDirty = $derived(editing && fileContent !== fileOriginal);
  const fileLanguage = $derived(selectedPath ? languageFor(selectedPath) : 'plaintext');
  // Cap highlighting at ~512KB — anything larger we render as plain text to
  // keep the main thread responsive. fileContent is empty until a file loads.
  const highlightedHtml = $derived(
    !fileBinary && !editing && fileContent && fileContent.length < 512_000
      ? highlightCode(fileContent, fileLanguage)
      : null,
  );

  onMount(async () => {
    await load();
    await Promise.all([loadWebhooks(), loadWorkflows()]);
  });

  async function load() {
    loading = true;
    error = '';
    try {
      repo = await git.get(repoId);
      currentBranch = repo.default_branch;
      const [b, list] = await Promise.all([
        git.branches(repoId).catch(() => null),
        git.files(repoId, { ref: repo.default_branch }),
      ]);
      branches = b;
      if (b?.current) currentBranch = b.current;
      entries = list.entries;
    } catch (e) {
      error = errorMessage(e);
      toast.fromError(e, 'Couldn’t load repository');
    } finally {
      loading = false;
    }
  }

  async function listAt(path: string) {
    listing = true;
    try {
      const list = await git.files(repoId, { path, ref: currentBranch });
      entries = list.entries;
    } catch (e) {
      toast.fromError(e, 'Couldn’t list files');
    } finally {
      listing = false;
    }
  }

  async function navigate(entry: GitFileEntry) {
    if (entry.type === 'tree') {
      pathStack = entry.path.split('/').filter(Boolean);
      await listAt(currentPath);
    } else {
      await openFile(entry.path);
    }
  }

  function navigateTo(idx: number) {
    pathStack = pathStack.slice(0, idx);
    listAt(currentPath);
  }

  async function openFile(path: string) {
    fileLoading = true;
    selectedPath = path;
    editing = false;
    try {
      const res = await git.readFile(repoId, path, currentBranch);
      fileContent = res.content;
      fileOriginal = res.content;
      fileBinary = res.is_binary;
    } catch (e) {
      toast.fromError(e, 'Couldn’t read file');
      selectedPath = null;
    } finally {
      fileLoading = false;
    }
  }

  async function changeBranch(b: string) {
    currentBranch = b;
    try {
      await git.checkout(repoId, b);
      pathStack = [];
      selectedPath = null;
      await listAt('');
      toast.success(`Checked out ${b}`);
    } catch (e) {
      toast.fromError(e, 'Checkout failed');
    }
  }

  async function pull() {
    try {
      const res = await git.pull(repoId, currentBranch);
      if (res.ok) toast.success(`Pulled · ${res.message}`);
      else toast.warning(`Pull: ${res.message}`);
      await listAt(currentPath);
    } catch (e) {
      toast.fromError(e, 'Pull failed');
    }
  }

  async function push() {
    try {
      const res = await git.push(repoId, currentBranch);
      if (res.ok) toast.success('Pushed');
      else toast.warning(`Push: ${res.message}`);
    } catch (e) {
      toast.fromError(e, 'Push failed');
    }
  }

  function startEdit() {
    if (fileBinary) {
      toast.warning('Binary files are not editable in this view');
      return;
    }
    editing = true;
  }

  function cancelEdit() {
    fileContent = fileOriginal;
    editing = false;
  }

  function openCommit() {
    if (!isDirty || !selectedPath) return;
    commitMessage = `update ${selectedPath}`;
    commitPush = true;
    showCommit = true;
  }

  async function commit() {
    if (!selectedPath || !commitMessage.trim()) return;
    committing = true;
    try {
      const res = await git.writeFile(repoId, {
        path: selectedPath,
        content: fileContent,
        commit_message: commitMessage.trim(),
        branch: currentBranch,
        push: commitPush,
      });
      if (res.ok) {
        toast.success(commitPush ? 'Committed and pushed' : 'Committed');
        fileOriginal = fileContent;
        editing = false;
        showCommit = false;
      } else {
        toast.warning(res.message);
      }
    } catch (e) {
      toast.fromError(e, 'Commit failed');
    } finally {
      committing = false;
    }
  }

  // ─── Webhook handlers ──────────────────────────────────────────

  async function loadWebhooks() {
    hooksLoading = true;
    try {
      const res = await gitWebhooks.list(repoId);
      hooks = res.data;
    } catch (e) {
      toast.fromError(e, 'Couldn’t load webhooks');
    } finally {
      hooksLoading = false;
    }
  }

  async function loadWorkflows() {
    try {
      const res = await workflowsApi.list(200, 0);
      workflowChoices = res.data;
    } catch {
      workflowChoices = [];
    }
  }

  function openHookCreate() {
    editingHookId = null;
    hookName = '';
    hookProvider = 'github';
    hookSecret = '';
    hookKeepSecret = false;
    hookWorkflowId = '';
    hookBranches = [];
    hookBranchDraft = '';
    hookAutoPull = true;
    hookEnabled = true;
    hookAllowUnsigned = false;
    hookFormError = '';
    showHookForm = true;
  }

  function openHookEdit(h: GitWebhook) {
    editingHookId = h.git_webhook_id;
    hookName = h.name;
    hookProvider = h.provider;
    hookSecret = '';
    hookKeepSecret = h.has_secret;
    hookWorkflowId = h.on_push_workflow_id ?? '';
    hookBranches = [...h.on_push_branches];
    hookBranchDraft = '';
    hookAutoPull = h.auto_pull;
    hookEnabled = h.enabled;
    hookAllowUnsigned = h.allow_unsigned;
    hookFormError = '';
    showHookForm = true;
  }

  function addHookBranch() {
    const v = hookBranchDraft.trim();
    if (!v) return;
    if (!hookBranches.includes(v)) hookBranches = [...hookBranches, v];
    hookBranchDraft = '';
  }

  function removeHookBranch(b: string) {
    hookBranches = hookBranches.filter((x) => x !== b);
  }

  async function saveHook() {
    hookFormError = '';
    if (!hookName.trim()) {
      hookFormError = 'Name is required';
      return;
    }
    hookSaving = true;
    try {
      // For create, always send the secret (may be empty for unauthed
      // testing). For edit, send only if user typed a new one OR they
      // explicitly toggled it off via clearing the keep flag.
      const body: Record<string, unknown> = {
        name: hookName.trim(),
        provider: hookProvider,
        on_push_workflow_id: hookWorkflowId || null,
        on_push_branches: hookBranches,
        auto_pull: hookAutoPull,
        enabled: hookEnabled,
        allow_unsigned: hookAllowUnsigned,
      };
      if (editingHookId) {
        if (hookSecret) body.secret = hookSecret;
        else if (!hookKeepSecret) body.secret = '';
        await gitWebhooks.update(repoId, editingHookId, body as Parameters<typeof gitWebhooks.update>[2]);
        toast.success('Webhook updated');
      } else {
        body.secret = hookSecret || '';
        await gitWebhooks.create(repoId, body as Parameters<typeof gitWebhooks.create>[1]);
        toast.success('Webhook created');
      }
      showHookForm = false;
      await loadWebhooks();
    } catch (e) {
      hookFormError = errorMessage(e);
    } finally {
      hookSaving = false;
    }
  }

  async function removeHook(h: GitWebhook) {
    if (!(await confirm({
      title: `Delete webhook "${h.name}"?`,
      message: 'GitHub/GitLab will keep posting to the URL — disable this on their side too.',
      tone: 'danger',
      confirmLabel: 'Delete',
    }))) return;
    try {
      await gitWebhooks.delete(repoId, h.git_webhook_id);
      toast.success('Webhook removed');
      await loadWebhooks();
    } catch (e) {
      toast.fromError(e, 'Delete failed');
    }
  }

  async function copyIngestionUrl(h: GitWebhook) {
    if (!h.ingestion_url) {
      toast.warning('Ingestion URL is not available — reload the page');
      return;
    }
    if (await copyText(h.ingestion_url)) toast.success('Webhook URL copied');
    else toast.warning('Clipboard not available — copy from the field below');
  }

  async function viewDeliveries(h: GitWebhook) {
    deliveriesFor = h.git_webhook_id;
    deliveriesOpen = true;
    deliveriesLoading = true;
    try {
      const res = await gitWebhooks.deliveries(repoId, h.git_webhook_id, 50);
      deliveries = res.data;
    } catch (e) {
      toast.fromError(e, 'Couldn’t load deliveries');
    } finally {
      deliveriesLoading = false;
    }
  }
</script>

<svelte:head><title>{repo?.name ?? 'Repository'} · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
<PageHeader
  title={repo?.name ?? 'Repository'}
  description={repo?.url ?? ''}
  breadcrumbs={[{ label: 'Git repositories', href: '/integrations/git' }, { label: repo?.name ?? '…' }]}
>
  {#snippet actions()}
    <Button variant="ghost" icon={ArrowLeft} href="/integrations/git">Back</Button>
    <Button variant="secondary" icon={Download} onclick={pull} disabled={loading}>Pull</Button>
    <Button variant="secondary" icon={Upload} onclick={push} disabled={loading}>Push</Button>
    <Button variant="ghost" icon={RefreshCw} onclick={load} disabled={loading}>Refresh</Button>
  {/snippet}
</PageHeader>

{#if error}
  <Alert tone="error">{error}</Alert>
{/if}

{#if loading}
  <div class="flex items-center justify-center py-16"><Spinner size="lg" label="Loading repository…" /></div>
{:else if repo}
  <section>
    <h2 class="text-sm font-semibold tracking-tight text-surface-800-200 mb-3 inline-flex items-center gap-1">Files <FieldHint id="git.files_section" /></h2>
    <div class="grid grid-cols-1 md:grid-cols-[280px_minmax(0,1fr)] gap-4">
    <!-- File tree column -->
    <Card>
      <div class="flex items-center gap-2 mb-3">
        <GitBranch size={14} class="text-primary-300 shrink-0" />
        <Select bind:value={currentBranch} onchange={(e: any) => changeBranch(e.target.value)}>
          {#each branches?.branches ?? [repo.default_branch] as b}
            <option value={b}>{b}</option>
          {/each}
        </Select>
      </div>

      <div class="flex items-center gap-1 text-xs text-surface-500 mb-2 flex-wrap">
        <button type="button" class="hover:underline" onclick={() => { pathStack = []; listAt(''); }}>root</button>
        {#each pathStack as seg, i}
          <span>/</span>
          <button type="button" class="hover:underline" onclick={() => navigateTo(i + 1)}>{seg}</button>
        {/each}
      </div>

      {#if listing}
        <div class="py-4 text-center"><Spinner size="sm" /></div>
      {:else if entries.length === 0}
        <div class="py-6 text-center text-xs text-surface-500">empty</div>
      {:else}
        <ul class="space-y-0.5">
          {#each entries as entry (entry.path)}
            <li>
              <button
                type="button"
                onclick={() => navigate(entry)}
                class="w-full flex items-center gap-2 px-2 py-1 rounded hover:bg-surface-200-800/50 text-left {selectedPath === entry.path ? 'bg-primary-500/15' : ''}"
              >
                {#if entry.type === 'tree'}
                  <FolderOpen size={12} class="text-primary-300 shrink-0" />
                {:else}
                  <FileText size={12} class="text-surface-500 shrink-0" />
                {/if}
                <span class="text-xs truncate flex-1">{entry.path.split('/').pop()}</span>
                {#if entry.type === 'blob'}
                  <span class="text-[10px] text-surface-500 tabular-nums">{entry.size}b</span>
                {/if}
              </button>
            </li>
          {/each}
        </ul>
      {/if}
    </Card>

    <!-- File viewer/editor column -->
    <!-- min-w-0: grid items default to min-width:auto, which lets the <pre>'s
         long single-line content (whitespace-pre, no wrap) blow the 1fr track
         out and break the layout. Clamping to 0 lets the <pre>'s own
         overflow-auto take over and scroll horizontally instead. -->
    <Card class="min-w-0">
      {#if !selectedPath}
        <div class="text-center py-16 text-surface-500">
          <FileText size={32} class="mx-auto mb-3 opacity-40" />
          <p class="text-sm">Select a file from the tree to view or edit.</p>
        </div>
      {:else if fileLoading}
        <div class="flex items-center justify-center py-16"><Spinner size="lg" /></div>
      {:else}
        <div class="flex items-center justify-between gap-2 mb-2 flex-wrap">
          <div class="font-mono text-xs text-surface-700-300 truncate">{selectedPath}</div>
          <div class="flex items-center gap-2">
            {#if !fileBinary && !editing}
              <Badge tone="neutral" mono>{fileLanguage}</Badge>
            {/if}
            {#if fileBinary}
              <Badge tone="warning">binary</Badge>
            {/if}
            {#if isDirty}
              <Badge tone="warning">modified</Badge>
            {/if}
            {#if !editing}
              <Button size="sm" variant="secondary" onclick={startEdit} disabled={fileBinary}>Edit</Button>
            {:else}
              <Button size="sm" variant="ghost" onclick={cancelEdit}>Cancel</Button>
              <Button size="sm" icon={GitCommit} onclick={openCommit} disabled={!isDirty}>Commit…</Button>
            {/if}
          </div>
        </div>
        {#if fileBinary}
          <div class="text-xs text-surface-500 italic">
            Binary content (base64, {fileContent.length} chars). Inline editing is disabled.
          </div>
        {:else if editing}
          <Textarea bind:value={fileContent} rows={24} class="font-mono text-xs" />
        {:else}
          <pre class="font-mono text-xs leading-relaxed whitespace-pre bg-surface-50-950 rounded-md p-3 overflow-auto max-h-[60vh] border border-surface-200-800"><code class="hljs language-{fileLanguage}">{#if highlightedHtml !== null}{@html highlightedHtml}{:else}{fileContent}{/if}</code></pre>
        {/if}
      {/if}
    </Card>
    </div>
  </section>

  <section>
    <div class="flex items-center justify-between gap-2 mb-3 flex-wrap">
      <div class="flex items-center gap-2 min-w-0">
        <Webhook size={14} class="text-primary-300 shrink-0" />
        <h2 class="text-sm font-semibold tracking-tight text-surface-800-200 inline-flex items-center gap-1">Inbound webhooks <FieldHint id="git.webhooks_section" /></h2>
        <span class="text-[11px] text-surface-500 truncate">
          GitHub / GitLab can call FlowWeaver when this repo changes.
        </span>
      </div>
      <Button size="sm" icon={Plus} onclick={openHookCreate}>Add webhook</Button>
    </div>
    <Card>
    {#if hooksLoading}
      <div class="py-6 text-center"><Spinner size="sm" /></div>
    {:else if hooks.length === 0}
      <div class="text-center py-8 text-surface-500 text-xs">
        No webhooks configured. Add one and paste the URL into your Git provider's webhook settings.
      </div>
    {:else}
      <ul class="space-y-2">
        {#each hooks as h (h.git_webhook_id)}
          <li class="rounded-md border border-surface-200-800 p-3 bg-surface-50-950/50">
            <div class="flex items-center justify-between gap-2 flex-wrap">
              <div class="min-w-0 flex-1">
                <div class="flex items-center gap-2 flex-wrap">
                  <span class="font-medium text-surface-900-100">{h.name}</span>
                  <Badge tone="neutral">{h.provider}</Badge>
                  {#if h.has_secret}
                    <Badge tone="success">signed</Badge>
                  {:else}
                    <Badge tone="warning">no secret</Badge>
                  {/if}
                  {#if !h.enabled}
                    <Badge tone="neutral">disabled</Badge>
                  {/if}
                  {#if h.on_push_workflow_id}
                    <Badge tone="primary">runs workflow</Badge>
                  {/if}
                  {#if h.auto_pull}
                    <Badge tone="neutral">auto-pull</Badge>
                  {/if}
                </div>
                {#if h.ingestion_url}
                  <div class="mt-1 flex items-center gap-2">
                    <code class="text-[11px] font-mono text-surface-600-400 truncate flex-1">{h.ingestion_url}</code>
                    <button type="button" class="text-surface-500 hover:text-primary-300 shrink-0" onclick={() => copyIngestionUrl(h)} title="Copy URL">
                      <Copy size={12} />
                    </button>
                  </div>
                {/if}
                <div class="text-[11px] text-surface-500 mt-1">
                  {#if h.on_push_branches.length === 0}
                    All branches
                  {:else}
                    Branches: <span class="font-mono">{h.on_push_branches.join(', ')}</span>
                  {/if}
                  {#if h.last_delivery_at}
                    · Last: {formatTime(h.last_delivery_at)} ({h.last_delivery_status})
                  {/if}
                </div>
              </div>
              <div class="flex items-center gap-1 shrink-0">
                <Button size="xs" variant="ghost" onclick={() => viewDeliveries(h)}>Deliveries</Button>
                <Button size="xs" variant="ghost" onclick={() => openHookEdit(h)}>Edit</Button>
                <IconButton icon={Trash2} label="Delete" onclick={() => removeHook(h)} />
              </div>
            </div>
          </li>
        {/each}
      </ul>
    {/if}
    </Card>
  </section>
{/if}
</div>

<Dialog bind:open={showHookForm} title={editingHookId ? 'Edit webhook' : 'Add webhook'} size="md">
  <div class="space-y-3">
    {#if hookFormError}<Alert tone="error">{hookFormError}</Alert>{/if}
    <div>
      <label class="block text-xs uppercase tracking-wide text-surface-500 mb-1" for="h-name">Name</label>
      <Input id="h-name" bind:value={hookName} placeholder="ci-pipeline" />
    </div>
    <div class="grid grid-cols-2 gap-3">
      <div>
        <label class="block text-xs uppercase tracking-wide text-surface-500 mb-1" for="h-prov">Provider</label>
        <Select id="h-prov" bind:value={hookProvider}>
          <option value="github">GitHub (HMAC-SHA256)</option>
          <option value="gitlab">GitLab (token)</option>
          <option value="generic">Generic (HMAC-SHA256)</option>
        </Select>
      </div>
      <div>
        <label class="block text-xs uppercase tracking-wide text-surface-500 mb-1" for="h-wf">Run workflow on push</label>
        <Select id="h-wf" bind:value={hookWorkflowId}>
          <option value="">— don't run a workflow —</option>
          {#each workflowChoices as wf}
            <option value={wf.id}>{wf.name} ({wf.environment})</option>
          {/each}
        </Select>
      </div>
    </div>
    <div>
      <label class="block text-xs uppercase tracking-wide text-surface-500 mb-1" for="h-secret">
        Secret {editingHookId && hookKeepSecret ? '(leave blank to keep existing)' : ''}
      </label>
      <Input id="h-secret" type="password" revealable bind:value={hookSecret} placeholder={hookKeepSecret ? '••••••••' : 'Enter signing secret'} />
      <div class="text-[11px] text-surface-500 mt-1">
        {#if hookProvider === 'github'}
          GitHub computes <code>X-Hub-Signature-256 = HMAC-SHA256(secret, body)</code>.
        {:else if hookProvider === 'gitlab'}
          GitLab passes the secret literally as <code>X-Gitlab-Token</code>.
        {:else}
          Generic emitter uses HMAC-SHA256 in <code>X-FlowWeaver-Signature</code>.
        {/if}
        Leaving the secret empty disables signature verification (NOT recommended).
      </div>
    </div>
    <div>
      <span class="block text-xs uppercase tracking-wide text-surface-500 mb-1">Branch filter (empty = all)</span>
      <div class="flex flex-wrap items-center gap-1.5 mb-1">
        {#each hookBranches as b}
          <span class="inline-flex items-center gap-1 bg-surface-100-900 border border-surface-300-700 rounded-full px-2 py-0.5 text-xs font-mono">
            {b}
            <button type="button" class="hover:text-error-400" onclick={() => removeHookBranch(b)} aria-label="Remove">
              <X size={10} />
            </button>
          </span>
        {/each}
      </div>
      <div class="flex gap-2">
        <Input
          bind:value={hookBranchDraft}
          placeholder="main, release/*"
          onkeydown={(e: KeyboardEvent) => { if (e.key === 'Enter') { e.preventDefault(); addHookBranch(); } }}
        />
        <IconButton icon={Plus} label="Add" onclick={addHookBranch} />
      </div>
    </div>
    <div class="flex flex-wrap items-center gap-4 text-xs">
      <label class="flex items-center gap-2">
        <input type="checkbox" bind:checked={hookAutoPull} class="accent-primary-500" />
        Auto-pull on push <FieldHint id="git.hook_auto_pull" />
      </label>
      <label class="flex items-center gap-2">
        <input type="checkbox" bind:checked={hookEnabled} class="accent-primary-500" />
        Enabled <FieldHint id="git.hook_enabled" />
      </label>
      <label class="flex items-center gap-2" title="When checked, the receiver accepts deliveries with no signature header. Only enable for testing emitters that can't sign.">
        <input type="checkbox" bind:checked={hookAllowUnsigned} class="accent-warning-500" />
        Allow unsigned (testing only) <FieldHint id="git.hook_allow_unsigned" />
      </label>
    </div>
  </div>
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showHookForm = false)}>Cancel</Button>
    <Button onclick={saveHook} icon={Save} disabled={hookSaving}>{hookSaving ? 'Saving…' : 'Save'}</Button>
  {/snippet}
</Dialog>

<Dialog
  bind:open={deliveriesOpen}
  onClose={() => { deliveriesFor = null; }}
  title="Recent deliveries"
  size="lg"
>
  {#if deliveriesLoading}
    <div class="py-6 text-center"><Spinner /></div>
  {:else if deliveries.length === 0}
    <div class="text-center text-xs text-surface-500 py-6">No deliveries recorded yet.</div>
  {:else}
    <ul class="space-y-1.5">
      {#each deliveries as d (d.git_webhook_delivery_id)}
        {@const tone = d.status === 'dispatched'
          ? 'success'
          : d.status === 'verified'
            ? 'primary'
            : d.status === 'rejected' || d.status === 'failed'
              ? 'error'
              : 'neutral'}
        <li class="text-xs flex items-start gap-2 p-2 rounded border border-surface-200-800 bg-surface-50-950/40">
          <Badge {tone}>{d.status}</Badge>
          <div class="flex-1 min-w-0">
            <div class="flex items-center gap-2 flex-wrap">
              <span class="text-surface-500">{formatTime(d.at)}</span>
              {#if d.event}<span class="font-mono text-surface-700-300">{d.event}</span>{/if}
              {#if d.branch}<span class="font-mono text-surface-700-300">{d.branch}</span>{/if}
              {#if d.commit_sha}<span class="font-mono text-surface-500">{d.commit_sha.slice(0, 8)}</span>{/if}
            </div>
            {#if d.workflow_run_id}
              <a href={`/runs/${d.workflow_run_id}/monitor`} class="text-primary-300 hover:underline text-[11px]">
                View run →
              </a>
            {/if}
            {#if d.error}
              <div class="text-error-300 text-[11px] mt-1 break-words">{d.error}</div>
            {/if}
          </div>
        </li>
      {/each}
    </ul>
  {/if}
  {#snippet footer()}
    <Button variant="ghost" onclick={() => { deliveriesOpen = false; deliveriesFor = null; }}>Close</Button>
  {/snippet}
</Dialog>

<Dialog bind:open={showCommit} title="Commit changes" size="md">
  <div class="space-y-3">
    <div>
      <label class="block text-xs uppercase tracking-wide text-surface-500 mb-1" for="c-msg">Commit message</label>
      <Textarea id="c-msg" bind:value={commitMessage} rows={3} />
    </div>
    <div class="flex items-center gap-2 text-xs">
      <input id="c-push" type="checkbox" bind:checked={commitPush} class="accent-primary-500" />
      <label for="c-push">Push to origin/{currentBranch} after commit</label>
      <FieldHint id="git.commit_push" />
    </div>
    <div class="text-[11px] text-surface-500">
      File: <span class="font-mono">{selectedPath}</span> · Branch: <span class="font-mono">{currentBranch}</span>
    </div>
  </div>
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showCommit = false)}>Cancel</Button>
    <Button onclick={commit} icon={Save} disabled={committing || !commitMessage.trim()}>
      {committing ? 'Committing…' : 'Commit'}
    </Button>
  {/snippet}
</Dialog>
