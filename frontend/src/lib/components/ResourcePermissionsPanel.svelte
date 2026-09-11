<script lang="ts">
  import {
    permissions, users,
    type ResourcePermission, type ResourceRole, type ResourceType, type User,
  } from '$lib/api/client';
  import { authStore } from '$lib/stores/auth.svelte';
  import {
    Card, Button, Select, Spinner, EmptyState, Alert, Badge,
    formatDateTime, toast, confirm,
  } from '$lib/components/ui';
  import { Shield, Trash2, UserPlus } from 'lucide-svelte';
  import { onMount } from 'svelte';

  // Embeddable in workflow/integration detail screens. Owner + role
  // semantics mirror Services/Permission/ResourceRoles.cs:
  //   owner > editor > runner > viewer.

  interface Props {
    resourceType: ResourceType;
    resourceId: string;
    title?: string;
  }

  let { resourceType, resourceId, title = 'Permissions' }: Props = $props();

  const ROLES: ResourceRole[] = ['owner', 'editor', 'runner', 'viewer'];

  // Mutating grants requires the global Admin tier (matches the API
  // policy on POST/DELETE). Hide the form for everyone else so they
  // don't get a 403 after typing.
  let isAdmin = $derived(authStore.session?.role === 'admin');

  let rows = $state<ResourcePermission[]>([]);
  let userList = $state<User[]>([]);
  let loading = $state(true);
  let saving = $state(false);
  let selectedUserId = $state('');
  let selectedRole = $state<ResourceRole>('viewer');

  onMount(async () => {
    await Promise.all([load(), loadUsers()]);
  });

  async function load() {
    loading = true;
    try {
      rows = await permissions.list(resourceType, resourceId);
    } catch (e) {
      toast.fromError(e, 'Failed to load permissions');
    } finally {
      loading = false;
    }
  }

  async function loadUsers() {
    try {
      userList = await users.list();
    } catch {
      // Non-fatal: the grant form will still work with raw user IDs.
    }
  }

  async function onGrant() {
    if (!selectedUserId || saving) return;
    saving = true;
    try {
      await permissions.grant(resourceType, resourceId, {
        subject_type: 'user',
        subject_id: selectedUserId,
        role: selectedRole,
      });
      selectedUserId = '';
      selectedRole = 'viewer';
      await load();
      toast.success('Permission granted');
    } catch (e) {
      toast.fromError(e, 'Grant failed');
    } finally {
      saving = false;
    }
  }

  async function onRevoke(row: ResourcePermission) {
    const ok = await confirm({
      title: 'Revoke access?',
      message: `Remove the ${row.role} grant from ${row.subject_username ?? row.subject_id}?`,
      confirmLabel: 'Revoke',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await permissions.revoke(resourceType, resourceId, row.resource_permission_id);
      await load();
      toast.success('Permission revoked');
    } catch (e) {
      toast.fromError(e, 'Revoke failed');
    }
  }

  // Map the role hierarchy to design-system Badge tones so it tracks the
  // active theme (was hardcoded bg-amber/blue/emerald palette colors).
  function roleTone(role: ResourceRole): 'warning' | 'primary' | 'success' | 'neutral' {
    switch (role) {
      case 'owner': return 'warning';
      case 'editor': return 'primary';
      case 'runner': return 'success';
      case 'viewer':
      default: return 'neutral';
    }
  }
</script>

<Card>
  <div class="p-4 space-y-4">
    <div class="flex items-center gap-2">
      <Shield size={16} class="text-surface-500" />
      <h2 class="text-sm font-semibold">{title}</h2>
      <span class="text-xs text-surface-500">resource_type={resourceType}</span>
    </div>

    <!-- Grant form. Admin-only on the API and in this UI. Non-admins
         see a read-only banner instead of a form they can't submit. -->
    {#if isAdmin}
      <div class="grid grid-cols-1 md:grid-cols-3 gap-2 items-end">
        <Select label="User" help="resource_perm.user" bind:value={selectedUserId}>
          <option value="">—</option>
          {#each userList as u}
            <option value={u.user_id}>{u.username}</option>
          {/each}
        </Select>
        <Select label="Role" help="resource_perm.role" bind:value={selectedRole}>
          {#each ROLES as r}
            <option value={r}>{r}</option>
          {/each}
        </Select>
        <Button
          variant="primary"
          icon={UserPlus}
          loading={saving}
          disabled={!selectedUserId}
          onclick={onGrant}
        >Grant</Button>
      </div>
    {:else}
      <Alert tone="info">Granting and revoking permissions is restricted to administrators.</Alert>
    {/if}

    {#if loading}
      <div class="py-8 flex justify-center"><Spinner size="md" label="Loading permissions…" /></div>
    {:else if rows.length === 0}
      <EmptyState
        icon={Shield}
        title="No per-resource grants"
        description={isAdmin
          ? "Global RBAC (admin/operator/viewer) still applies. Add a grant above to raise a specific user's privilege on this resource."
          : 'Global RBAC (admin/operator/viewer) still applies. Per-resource grants are managed by administrators.'}
      />
    {:else}
      <table class="w-full text-sm">
        <thead class="border-b border-surface-200-800">
          <tr class="text-left text-[11px] uppercase tracking-wide text-surface-500">
            <th class="px-2 py-1.5">User</th>
            <th class="px-2 py-1.5">Role</th>
            <th class="px-2 py-1.5">Granted</th>
            <th class="px-2 py-1.5">By</th>
            <th class="px-2 py-1.5"></th>
          </tr>
        </thead>
        <tbody>
          {#each rows as row (row.resource_permission_id)}
            <tr class="border-b border-surface-200-800/50">
              <td class="px-2 py-2 font-medium">{row.subject_username ?? row.subject_id}</td>
              <td class="px-2 py-2">
                <Badge tone={roleTone(row.role)}>{row.role}</Badge>
              </td>
              <td class="px-2 py-2 text-surface-500 text-[11px] tabular-nums">{formatDateTime(row.granted_at)}</td>
              <td class="px-2 py-2 text-surface-500 text-[11px]">{row.granted_by_username ?? '—'}</td>
              <td class="px-2 py-2 text-right">
                {#if isAdmin}
                  <Button size="sm" variant="ghost" icon={Trash2} onclick={() => onRevoke(row)}>Revoke</Button>
                {/if}
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    {/if}
  </div>
</Card>
