<script lang="ts">
  import { onMount } from 'svelte';
  import {
    users,
    errorMessage,
    type User,
    type UserRole,
    type CreateUserBody,
    type UpdateUserBody,
  } from '$lib/api/client';
  import { authStore } from '$lib/stores/auth.svelte';
  import {
    PageHeader,
    Card,
    DataTable,
    Badge,
    Button,
    Dialog,
    Input,
    Select,
    Alert,
    Spinner,
    ErrorState,
    EmptyState,
    IconButton,
    SearchInput,
    Toolbar,
    toast,
    confirm,
    formatDate,
    FieldHint,
  } from '$lib/components/ui';
  import { Users, Plus, Pencil, Trash2, Lock, Shield, ArrowLeft } from 'lucide-svelte';

  const ROLES: UserRole[] = ['admin', 'operator', 'viewer'];

  let list = $state<User[]>([]);
  let loading = $state(true);
  let loadError = $state<unknown>(null);

  // Create dialog state.
  let showCreate = $state(false);
  let createForm = $state<CreateUserBody>({ username: '', email: '', password: '', role: 'viewer' });
  let createBusy = $state(false);
  let createError = $state<string | null>(null);

  // Edit dialog state. `target` is the user currently being edited;
  // the form mirrors its editable fields.
  let showEdit = $state(false);
  let target = $state<User | null>(null);
  let editForm = $state<UpdateUserBody>({});
  let editBusy = $state(false);
  let editError = $state<string | null>(null);

  onMount(load);

  async function load() {
    loading = true;
    loadError = null;
    try {
      list = await users.list();
    } catch (e) {
      loadError = e;
    } finally {
      loading = false;
    }
  }

  function openCreate() {
    createForm = { username: '', email: '', password: '', role: 'viewer' };
    createError = null;
    showCreate = true;
  }

  async function submitCreate(event: SubmitEvent) {
    event.preventDefault();
    if (createBusy) return;
    createError = null;
    if (!createForm.username.trim() || !createForm.email.trim() || !createForm.password) {
      createError = 'Username, email, and password are required';
      return;
    }
    createBusy = true;
    try {
      const created = await users.create(createForm);
      list = [...list, created].sort((a, b) => a.username.localeCompare(b.username));
      showCreate = false;
      toast.success(`User ${created.username} created`);
    } catch (e) {
      createError = errorMessage(e);
    } finally {
      createBusy = false;
    }
  }

  function openEdit(user: User) {
    target = user;
    editForm = { email: user.email, role: user.role, is_active: user.is_active };
    editError = null;
    showEdit = true;
  }

  async function submitEdit(event: SubmitEvent) {
    event.preventDefault();
    if (editBusy || !target) return;
    editError = null;

    // Guard against an admin demoting themselves out of admin access. Only
    // gate when the edited account is the current user AND the new role is
    // no longer 'admin' — otherwise this would lock them out of admin screens.
    if (target.user_id === currentUserId && editForm.role !== 'admin') {
      const ok = await confirm({
        tone: 'danger',
        title: 'Remove your own admin access?',
        message: 'You will lose access to admin screens.',
      });
      if (!ok) return;
    }

    editBusy = true;
    try {
      const updated = await users.update(target.user_id, editForm);
      list = list.map((u) => (u.user_id === updated.user_id ? updated : u));
      showEdit = false;
      target = null;
      toast.success('User updated');
    } catch (e) {
      editError = errorMessage(e);
    } finally {
      editBusy = false;
    }
  }

  async function removeUser(user: User) {
    const ok = await confirm({
      title: `Delete ${user.username}?`,
      message:
        'The account will be soft-deleted. Refresh tokens stay valid until they expire; consider disabling the user first if you need an immediate lockout.',
      confirmLabel: 'Delete',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await users.delete(user.user_id);
      list = list.filter((u) => u.user_id !== user.user_id);
      toast.success(`User ${user.username} deleted`);
    } catch (e) {
      toast.fromError(e, 'Delete failed');
    }
  }

  function roleTone(role: UserRole): 'primary' | 'warning' | 'neutral' {
    if (role === 'admin') return 'primary';
    if (role === 'operator') return 'warning';
    return 'neutral';
  }

  const currentUserId = $derived(authStore.session?.userId ?? '');

  // Client-side filter by username or email. The list is fetched in full
  // (no server pagination), so a simple substring match over `list` is
  // enough and keeps the table responsive as the admin types.
  let query = $state('');
  const filtered = $derived.by(() => {
    const q = query.trim().toLowerCase();
    if (!q) return list;
    return list.filter(
      (u) => u.username.toLowerCase().includes(q) || u.email.toLowerCase().includes(q),
    );
  });
</script>

<svelte:head>
  <title>Admin · Users · FlowWeaver</title>
</svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-6">
  <PageHeader
    title="Users"
    description="Create users, assign roles, and disable accounts."
  >
    {#snippet actions()}
      <div class="flex items-center gap-2">
        <IconButton icon={ArrowLeft} label="Back to Admin" href="/admin" size="md" />
        <Button variant="primary" icon={Plus} onclick={openCreate}>New user</Button>
      </div>
    {/snippet}
  </PageHeader>

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" label="Loading users…" /></div>
  {:else if loadError}
    <Card padding="none"><ErrorState error={loadError} onRetry={load} /></Card>
  {:else if list.length === 0}
    <Card padding="none">
      <EmptyState
        icon={Users}
        title="No users yet"
        description="Create the first account to let someone else in."
      />
    </Card>
  {:else}
    <div class="flex items-center justify-between gap-3 flex-wrap">
      <SearchInput
        bind:value={query}
        placeholder="Filter by username or email…"
      />
      <span class="text-xs text-surface-500 tabular-nums">
        {filtered.length} of {list.length} user{list.length === 1 ? '' : 's'}
      </span>
    </div>
    {#if filtered.length === 0}
      <Card padding="none">
        <EmptyState
          icon={Users}
          title="No matching users"
          description="No user matches your search. Try a different username or email."
        />
      </Card>
    {:else}
    <DataTable caption="All users, with their role, status, and creation date.">
      <thead>
        <tr>
          <th>Username</th>
          <th>Email</th>
          <th>Role</th>
          <th>Status</th>
          <th>Created</th>
          <th class="text-right">Actions</th>
        </tr>
      </thead>
      <tbody>
        {#each filtered as user (user.user_id)}
          {@const isSelf = user.user_id === currentUserId}
          <tr>
            <td class="font-medium text-surface-900-100">
              <div class="flex items-center gap-2">
                <span>{user.username}</span>
                {#if isSelf}
                  <Badge tone="info" size="sm">you</Badge>
                {/if}
              </div>
            </td>
            <td class="text-surface-600-400">{user.email}</td>
            <td>
              <Badge tone={roleTone(user.role)} size="sm">
                <span class="inline-flex items-center gap-1 capitalize">
                  {#if user.role === 'admin'}<Shield size={10} />{/if}
                  {user.role}
                </span>
              </Badge>
            </td>
            <td>
              <div class="flex items-center gap-1.5">
                {#if user.locked}
                  <Badge tone="error" size="sm">
                    <span class="inline-flex items-center gap-1"><Lock size={10} />Locked</span>
                  </Badge>
                {:else if user.is_active}
                  <Badge tone="success" size="sm">Active</Badge>
                {:else}
                  <Badge tone="neutral" size="sm">Disabled</Badge>
                {/if}
              </div>
            </td>
            <td class="text-surface-500 text-xs tabular-nums">{formatDate(user.created_at)}</td>
            <td>
              <div class="flex items-center justify-end gap-1">
                <IconButton
                  icon={Pencil}
                  label={`Edit ${user.username}`}
                  size="sm"
                  onclick={() => openEdit(user)}
                />
                <IconButton
                  icon={Trash2}
                  label={isSelf ? 'Cannot delete yourself' : `Delete ${user.username}`}
                  size="sm"
                  variant="danger"
                  disabled={isSelf}
                  onclick={() => removeUser(user)}
                />
              </div>
            </td>
          </tr>
        {/each}
      </tbody>
    </DataTable>
    {/if}
  {/if}
</div>

<!-- Create dialog ───────────────────────────────────────────────────── -->
<Dialog bind:open={showCreate} title="New user" size="sm">
  <form onsubmit={submitCreate} class="flex flex-col gap-3">
    {#if createError}
      <Alert tone="error">{createError}</Alert>
    {/if}
    <Input
      label="Username"
        help="users.username"
      type="text"
      autocomplete="off"
      bind:value={createForm.username}
      disabled={createBusy}
      placeholder="jdoe"
    />
    <Input
      label="Email"
        help="users.email"
      type="email"
      autocomplete="off"
      bind:value={createForm.email}
      disabled={createBusy}
      placeholder="jdoe@example.com"
    />
    <Input
      label="Password"
        help="users.password"
      type="password"
      autocomplete="new-password"
      bind:value={createForm.password}
      disabled={createBusy}
      hint="12+ chars, mix of case + digit + symbol. The policy rejects common passwords."
    />
    <Select label="Role"
        help="users.role" bind:value={createForm.role} disabled={createBusy}>
      {#each ROLES as role}
        <option value={role}>{role}</option>
      {/each}
    </Select>
  </form>
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showCreate = false)} disabled={createBusy}>Cancel</Button>
    <Button variant="primary" loading={createBusy} onclick={() => document.forms[0]?.requestSubmit()}>
      Create
    </Button>
  {/snippet}
</Dialog>

<!-- Edit dialog ─────────────────────────────────────────────────────── -->
<Dialog bind:open={showEdit} title={target ? `Edit ${target.username}` : 'Edit user'} size="sm">
  {#if target}
    <form onsubmit={submitEdit} class="flex flex-col gap-3">
      {#if editError}
        <Alert tone="error">{editError}</Alert>
      {/if}
      <Input
        label="Username"
        help="users.username"
        type="text"
        value={target.username}
        disabled
        hint="Username cannot be changed after creation."
      />
      <Input
        label="Email"
        help="users.email"
        type="email"
        autocomplete="off"
        bind:value={editForm.email}
        disabled={editBusy}
      />
      <Select label="Role" help="users.role" bind:value={editForm.role} disabled={editBusy}>
        {#each ROLES as role}
          <option value={role}>{role}</option>
        {/each}
      </Select>
      <label class="flex items-center gap-2 text-xs text-surface-700-300 cursor-pointer mt-1" for="user-is-active">
        <FieldHint id="users.is_active" />
        <input
          id="user-is-active"
          type="checkbox"
          bind:checked={editForm.is_active}
          disabled={editBusy}
          class="accent-primary-500"
        />
        <span>Active — unchecking disables sign-in for this user.</span>
      </label>
    </form>
  {/if}
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showEdit = false)} disabled={editBusy}>Cancel</Button>
    <Button variant="primary" loading={editBusy} onclick={() => document.forms[0]?.requestSubmit()}>
      Save
    </Button>
  {/snippet}
</Dialog>
