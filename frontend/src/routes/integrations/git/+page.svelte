<script lang="ts">
  // Registered Git repositories — remotes the platform can clone, edit,
  // commit, and push to. Auth is delegated to the Credentials catalog
  // (Type=git_token for HTTPS+PAT, auth_method=key for SSH).

  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import {
    git, credentials, errorMessage,
    type GitRepository, type Credential,
  } from '$lib/api/client';
  import {
    PageHeader, Card, Button, IconButton, Badge, EmptyState, Alert, Spinner,
    Input, Textarea, Select, Dialog, formatDateTime, toast, confirm,
  } from '$lib/components/ui';
  import {
    GitBranch, Plus, Pencil, Trash2, RefreshCw, ExternalLink, FolderGit2, Download,
  } from 'lucide-svelte';

  let rows = $state<GitRepository[]>([]);
  let creds = $state<Credential[]>([]);
  let loading = $state(true);
  let loadError = $state<string | null>(null);

  // `editing === null` means "create".
  let showDialog = $state(false);
  let editing = $state<GitRepository | null>(null);
  let formName = $state('');
  let formUrl = $state('');
  let formBranch = $state('main');
  let formCredId = $state('');
  let formDescription = $state('');
  let formError = $state<string | null>(null);
  let saving = $state(false);

  onMount(async () => { await Promise.all([load(), loadCreds()]); });

  async function load() {
    loading = true;
    loadError = null;
    try {
      const res = await git.list(100, 0);
      rows = res.data;
    } catch (e) {
      loadError = errorMessage(e);
      toast.fromError(e, "Couldn't load repositories");
    } finally {
      loading = false;
    }
  }

  async function loadCreds() {
    try {
      const res = await credentials.list(200, 0);
      // Show creds suitable for Git auth: token-style for HTTPS (type
      // contains "git") and key-auth rows (auth_method=key, used for
      // SSH URLs). Plain SSH password creds are excluded — Git over
      // SSH always uses keys in our model.
      creds = (res.data ?? []).filter(
        (c) => /git/i.test(c.type) || c.auth_method === 'key',
      );
    } catch {
      creds = [];
    }
  }

  // SSH form (`user@host:path`) and explicit https/ssh schemes are the
  // only allowed shapes; matches what the backend accepts in IsAllowedUrl.
  function isAllowedUrl(url: string): boolean {
    if (/^https?:\/\//i.test(url)) return true;
    if (/^ssh:\/\//i.test(url)) return true;
    if (url.includes('://')) return false;
    const at = url.indexOf('@');
    if (at <= 0) return false;
    const colon = url.indexOf(':', at + 1);
    return colon > at && colon < url.length - 1;
  }

  // SSH URL ⇒ key credential needed; HTTPS ⇒ token credential. Used to
  // soft-validate the form so users don't pair the wrong combination.
  function isSshUrl(url: string): boolean {
    if (!url) return false;
    if (/^ssh:\/\//i.test(url)) return true;
    if (url.includes('://')) return false;
    const at = url.indexOf('@');
    return at > 0 && url.indexOf(':', at + 1) > at;
  }

  function openCreate() {
    editing = null;
    formName = '';
    formUrl = '';
    formBranch = 'main';
    formCredId = '';
    formDescription = '';
    formError = null;
    showDialog = true;
  }

  function openEdit(repo: GitRepository) {
    editing = repo;
    formName = repo.name;
    formUrl = repo.url;
    formBranch = repo.default_branch;
    formCredId = repo.auth_credential_id ?? '';
    formDescription = repo.description ?? '';
    formError = null;
    showDialog = true;
  }

  function closeDialog() {
    if (saving) return;
    showDialog = false;
    editing = null;
  }

  async function save() {
    if (saving) return;
    formError = null;
    if (!formName.trim() || !formUrl.trim()) {
      formError = 'Name and URL are required.';
      return;
    }
    if (!isAllowedUrl(formUrl.trim())) {
      formError = 'URL must be https://… or an SSH form (git@host:path or ssh://…).';
      return;
    }
    saving = true;
    try {
      const body = {
        name: formName.trim(),
        url: formUrl.trim(),
        default_branch: formBranch.trim() || 'main',
        auth_credential_id: formCredId || null,
        description: formDescription || null,
      };
      if (editing) {
        await git.update(editing.git_repository_id, body);
        toast.success('Repository updated');
      } else {
        await git.create(body);
        toast.success('Repository registered');
      }
      showDialog = false;
      await load();
    } catch (e) {
      formError = errorMessage(e);
    } finally {
      saving = false;
    }
  }

  async function del(repo: GitRepository) {
    if (!(await confirm({
      title: `Delete repository "${repo.name}"?`,
      message: 'Workflows that reference it will fail. The on-disk checkout is preserved so you can re-register without re-cloning.',
      tone: 'danger',
      confirmLabel: 'Delete',
    }))) return;
    try {
      await git.delete(repo.git_repository_id);
      toast.success('Repository removed');
      await load();
    } catch (e) {
      toast.fromError(e, 'Delete failed');
    }
  }

  async function pull(repo: GitRepository) {
    try {
      const res = await git.pull(repo.git_repository_id);
      if (res.ok) toast.success(`Pulled · ${res.message}`);
      else toast.warning(`Pull: ${res.message}`);
      await load();
    } catch (e) {
      toast.fromError(e, 'Pull failed');
    }
  }

  // Reactive credential filter that narrows the dropdown to the
  // matching auth shape for the URL the user typed. Empty URL =
  // show every Git-friendly credential (it's still a usable form).
  const matchingCreds = $derived.by(() => {
    if (!formUrl) return creds;
    const ssh = isSshUrl(formUrl.trim());
    return creds.filter((c) => (ssh ? c.auth_method === 'key' : /git/i.test(c.type)));
  });

  const sshFormUrl = $derived(isSshUrl(formUrl));
</script>

<svelte:head><title>Git repositories · Flow Weaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title="Git repositories"
    description="Remotes the platform can clone, edit, commit, and push to. Auth uses the Credentials catalog: HTTPS+PAT for token auth, SSH key for git@/ssh:// URLs."
  >
    {#snippet actions()}
      <Button variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
      <Button variant="primary" icon={Plus} onclick={openCreate}>New repository</Button>
    {/snippet}
  </PageHeader>

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" /></div>
  {:else if loadError}
    <Alert tone="error">{loadError}</Alert>
  {:else if rows.length === 0}
    <Card padding="none">
      <EmptyState
        icon={FolderGit2}
        title="No Git repositories yet"
        description="Register a repo so the platform can clone its files, let workflows update them, and dispatch runs from inbound webhooks."
      >
        {#snippet actions()}
          <Button variant="primary" icon={Plus} onclick={openCreate}>Register first repository</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else}
    <Card padding="none">
      <div class="overflow-x-auto">
      <table class="w-full text-sm table-fixed">
        <colgroup>
          <col class="w-[22%]" />
          <col />
          <col class="w-[8rem]" />
          <col class="w-[6rem]" />
          <col class="w-[10rem]" />
          <col class="w-[7rem]" />
        </colgroup>
        <thead class="border-b border-surface-200-800">
          <tr class="text-left text-[11px] uppercase tracking-wide text-surface-500">
            <th class="px-4 py-2">Name</th>
            <th class="px-4 py-2">URL</th>
            <th class="px-4 py-2">Branch</th>
            <th class="px-4 py-2">Auth</th>
            <th class="px-4 py-2">Last fetched</th>
            <th class="px-4 py-2 text-right">Actions</th>
          </tr>
        </thead>
        <tbody>
          {#each rows as repo (repo.git_repository_id)}
            <tr class="border-b border-surface-200-800/50 hover:bg-surface-100-900/40">
              <td class="px-4 py-2">
                <div class="flex items-center gap-2 min-w-0">
                  <GitBranch size={12} class="text-primary-300 shrink-0" />
                  <button
                    type="button"
                    class="font-medium text-surface-900-100 truncate hover:underline text-left min-w-0"
                    onclick={() => goto(`/integrations/git/${repo.git_repository_id}`)}
                  >{repo.name}</button>
                </div>
                {#if repo.description}
                  <div class="text-[11px] text-surface-500 mt-0.5 ml-5 truncate">{repo.description}</div>
                {/if}
              </td>
              <td class="px-4 py-2">
                <div class="flex items-center gap-1 text-surface-700-300 font-mono text-xs min-w-0">
                  <span class="truncate min-w-0 flex-1" title={repo.url}>{repo.url}</span>
                  {#if /^https?:\/\//i.test(repo.url)}
                    <a href={repo.url} target="_blank" rel="noopener" class="text-surface-500 hover:text-primary-300 shrink-0" title="Open remote">
                      <ExternalLink size={11} />
                    </a>
                  {/if}
                </div>
              </td>
              <td class="px-4 py-2 text-surface-700-300 font-mono text-xs truncate">{repo.default_branch}</td>
              <td class="px-4 py-2">
                {#if repo.auth_credential_id}
                  <Badge tone="success">authed</Badge>
                {:else}
                  <Badge tone="warning">public</Badge>
                {/if}
              </td>
              <td class="px-4 py-2 text-surface-500 text-[11px] tabular-nums whitespace-nowrap">
                {repo.last_fetched_at ? formatDateTime(repo.last_fetched_at) : 'never'}
              </td>
              <td class="px-4 py-2">
                <div class="flex items-center justify-end gap-0.5">
                  <IconButton icon={Download} label="Pull" onclick={() => pull(repo)} />
                  <IconButton icon={Pencil} label="Edit" onclick={() => openEdit(repo)} />
                  <IconButton icon={Trash2} label="Delete" variant="danger" onclick={() => del(repo)} />
                </div>
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
      </div>
    </Card>
    <p class="text-xs text-surface-500">{rows.length} repositor{rows.length === 1 ? 'y' : 'ies'}</p>
  {/if}
</div>

<Dialog bind:open={showDialog} title={editing ? `Edit ${editing.name}` : 'New repository'} size="md">
  <div class="space-y-4 p-1">
    {#if formError}
      <Alert tone="error">{formError}</Alert>
    {/if}
    <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
      <Input label="Name" help="git.name" placeholder="team-configs" bind:value={formName} disabled={saving} />
      <Input label="Default branch" help="git.default_branch" placeholder="main" bind:value={formBranch} disabled={saving} />
    </div>
    <div>
      <Input
        label="URL"
        help="git.url"
        placeholder="https://github.com/org/repo.git or git@github.com:org/repo.git"
        bind:value={formUrl}
        disabled={saving}
      />
      <p class="text-[11px] text-surface-500 mt-1">
        HTTPS uses a token credential (Type=<code>git_token</code>); SSH (<code>git@…</code> or <code>ssh://…</code>) needs a credential with auth_method=<code>key</code>.
      </p>
    </div>
    <div>
      <Select label="Auth credential" help="git.credential" bind:value={formCredId} disabled={saving}>
        <option value="">— public / read-only —</option>
        {#each matchingCreds as c}
          <option value={c.credential_id}>{c.name} ({c.type})</option>
        {/each}
      </Select>
      {#if matchingCreds.length === 0}
        <p class="text-[11px] text-warning-300 mt-1">
          No matching credential found.
          {#if sshFormUrl}
            Create one in <a href="/credentials" class="underline">/credentials</a> with <strong>Type=ssh</strong> + <strong>Auth method=SSH private key</strong>.
          {:else}
            Create one in <a href="/credentials" class="underline">/credentials</a> with <strong>Type=git_token</strong> and the PAT in the password field.
          {/if}
        </p>
      {:else}
        <p class="text-[11px] text-surface-500 mt-1">
          Showing {matchingCreds.length} credential{matchingCreds.length === 1 ? '' : 's'} compatible with this URL.
          Need another? <a href="/credentials" class="underline">Manage credentials</a>.
        </p>
      {/if}
    </div>
    <Textarea label="Description (optional)" help="git.description" rows={2} bind:value={formDescription} disabled={saving} />
  </div>

  {#snippet footer()}
    <Button variant="ghost" onclick={closeDialog} disabled={saving}>Cancel</Button>
    <Button variant="primary" onclick={save} loading={saving}>{editing ? 'Save changes' : 'Register'}</Button>
  {/snippet}
</Dialog>
