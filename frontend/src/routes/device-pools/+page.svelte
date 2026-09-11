<script lang="ts">
  import { onMount } from 'svelte';
  import { devicePools, devices, errorMessage, type Device, type DevicePool } from '$lib/api/client';
  import {
    PageHeader, Card, Button, IconButton, Badge, EmptyState, Alert,
    Spinner, Input, confirm, toast, FieldHint,
  } from '$lib/components/ui';
  import { Network, Plus, Trash2, Save, Pencil, Hammer, FlaskConical, Rocket } from 'lucide-svelte';
  import DevicePicker from './DevicePicker.svelte';

  let list = $state<DevicePool[]>([]);
  let loading = $state(true);
  let error = $state('');

  // Device catalog is loaded once so both the create form and every row's
  // inline editor can drive a picker off the same source instead of re-
  // fetching.
  let deviceCatalog = $state<Device[]>([]);
  let loadingDevices = $state(false);

  let showNewForm = $state(false);
  let newName = $state('');
  let newDescription = $state('');
  let newMemberIds = $state<string[]>([]);
  let newEnvs = $state({ allow_draft: true, allow_qa: false, allow_production: true });
  let creating = $state(false);

  let editing = $state<string | null>(null);
  let editBuf = $state<Record<string, { name: string; description: string; memberIds: string[]; allow_draft: boolean; allow_qa: boolean; allow_production: boolean }>>({});
  let saveLoading = $state<Record<string, boolean>>({});

  onMount(() => {
    load();
    loadDevices();
  });

  async function loadDevices() {
    loadingDevices = true;
    try {
      const res = await devices.list(500, 0);
      deviceCatalog = res.data ?? [];
    } catch (e) {
      toast.fromError(e, 'Couldn’t load devices');
    } finally {
      loadingDevices = false;
    }
  }


  async function load() {
    loading = true;
    error = '';
    try {
      const res = await devicePools.list();
      list = res.data;
    } catch (e) {
      error = errorMessage(e);
      toast.fromError(e, 'Couldn’t load pools');
    } finally {
      loading = false;
    }
  }

  async function create() {
    if (!newName.trim()) return;
    creating = true;
    try {
      await devicePools.create({
        name: newName.trim(),
        description: newDescription.trim() || null,
        static_members: newMemberIds,
        ...newEnvs,
      });
      toast.success(`Pool "${newName}" created`);
      newName = '';
      newDescription = '';
      newMemberIds = [];
      newEnvs = { allow_draft: true, allow_qa: false, allow_production: true };
      showNewForm = false;
      await load();
    } catch (e) {
      toast.fromError(e, 'Couldn’t create pool');
    } finally {
      creating = false;
    }
  }

  function openEdit(p: DevicePool) {
    editing = p.device_pool_id;
    editBuf = {
      ...editBuf,
      [p.device_pool_id]: {
        name: p.name,
        description: p.description ?? '',
        memberIds: [...(p.static_members ?? [])],
        allow_draft: p.allow_draft,
        allow_qa: p.allow_qa,
        allow_production: p.allow_production,
      },
    };
  }

  async function save(id: string) {
    const buf = editBuf[id];
    if (!buf) return;
    saveLoading = { ...saveLoading, [id]: true };
    try {
      await devicePools.update(id, {
        name: buf.name.trim(),
        description: buf.description.trim() || null,
        static_members: buf.memberIds,
        allow_draft: buf.allow_draft,
        allow_qa: buf.allow_qa,
        allow_production: buf.allow_production,
      });
      toast.success('Pool saved');
      editing = null;
      await load();
    } catch (e) {
      toast.fromError(e, 'Couldn’t save pool');
    } finally {
      saveLoading = { ...saveLoading, [id]: false };
    }
  }

  // Environments a run may target this pool from. Applied on top of the
  // per-device flags: a run must be allowed by the pool AND by the member
  // it fans out to. Same three icons as the Devices page so the control
  // reads identically in both places.
  const ENVIRONMENTS = [
    { key: 'allow_draft', label: 'draft', icon: Hammer },
    { key: 'allow_qa', label: 'qa', icon: FlaskConical },
    { key: 'allow_production', label: 'production', icon: Rocket },
  ] as const;

  type EnvKey = (typeof ENVIRONMENTS)[number]['key'];
  // Just the three flags. The edit buffer carries name/description/members
  // too, but it is structurally assignable to this, so one snippet drives
  // both forms.
  type EnvFlags = Record<EnvKey, boolean>;

  async function toggleEnvironment(p: DevicePool, key: EnvKey) {
    try {
      await devicePools.update(p.device_pool_id, { [key]: !p[key] });
      await load();
    } catch (e) {
      toast.fromError(e, 'Couldn’t update environments');
    }
  }

  async function remove(p: DevicePool) {
    if (
      !(await confirm({
        title: `Delete pool "${p.name}"?`,
        message: 'Workflows referencing this pool by id will fail to resolve targets until the reference is removed.',
        tone: 'danger',
        confirmLabel: 'Delete',
      }))
    )
      return;
    try {
      await devicePools.delete(p.device_pool_id);
      if (editing === p.device_pool_id) editing = null;
      await load();
    } catch (e) {
      toast.fromError(e, 'Couldn’t delete pool');
    }
  }
</script>

<!-- Icon+label toggle chips, shared by the create and edit forms. Takes the
     mutable flag record so both call sites write straight through to their
     own buffer. -->
{#snippet envToggles(target: EnvFlags)}
  <div class="flex items-center gap-1.5">
    {#each ENVIRONMENTS as env (env.key)}
      {@const on = target[env.key] === true}
      {@const Icon = env.icon}
      <button
        type="button"
        onclick={() => (target[env.key] = !target[env.key])}
        aria-pressed={on}
        class="inline-flex items-center gap-1.5 px-2 py-1 rounded-md border text-sm transition-colors {on
          ? 'border-success-500/40 bg-success-500/15 text-success-400'
          : 'border-surface-300-700 text-surface-500 hover:text-surface-800-200 hover:bg-surface-200-800/60'}"
      >
        <Icon size={14} />
        {env.label}
      </button>
    {/each}
  </div>
{/snippet}

<svelte:head><title>Device pools · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title="Device pools"
    description="Named groups of devices workflows target together. Each pool declares which environments may target it, and a run must be allowed by both the pool and the member device."
  >
    {#snippet actions()}
      <Button size="sm" variant="primary" icon={Plus} onclick={() => (showNewForm = !showNewForm)}>
        New pool
      </Button>
    {/snippet}
  </PageHeader>

  {#if error}<Alert tone="error">{error}</Alert>{/if}

  {#if showNewForm}
    <Card>
      <h3 class="text-xs font-semibold uppercase tracking-wide text-surface-500 mb-3">New pool</h3>
      <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
        <Input label="Name" help="pools.name" bind:value={newName} placeholder="core-routers" />
        <fieldset>
          <legend class="text-xs text-surface-500 mb-1.5 inline-flex items-center gap-1">Environments <FieldHint id="pools.environments" /></legend>
          {@render envToggles(newEnvs)}
        </fieldset>
        <div class="md:col-span-2">
          <Input label="Description (optional)" help="pools.description" bind:value={newDescription} />
        </div>
        <div class="md:col-span-2">
          <DevicePicker
            devices={deviceCatalog}
            loading={loadingDevices}
            selected={newMemberIds}
            onChange={(ids) => (newMemberIds = ids)}
          />
        </div>
      </div>
      <div class="mt-4 flex justify-end gap-2">
        <Button variant="ghost" onclick={() => (showNewForm = false)}>Cancel</Button>
        <Button variant="primary" icon={Save} loading={creating} onclick={create} disabled={!newName.trim()}>Create</Button>
      </div>
    </Card>
  {/if}

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" label="Loading pools…" /></div>
  {:else if list.length === 0}
    <Card padding="none">
      <EmptyState
        icon={Network}
        title="No pools yet"
        description="A pool groups devices so workflows can target a role or site instead of naming devices one by one. Tick the environments each pool may be targeted from to scope it to the right stage."
      >
        {#snippet actions()}
          <Button variant="primary" icon={Plus} onclick={() => (showNewForm = true)}>Add pool</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else}
    <div class="space-y-2">
      {#each list as p (p.device_pool_id)}
        {@const isEditing = editing === p.device_pool_id}
        {@const buf = editBuf[p.device_pool_id]}
        <Card>
          <div class="flex items-start justify-between gap-3">
            <div class="flex-1">
              <div class="flex items-center gap-2">
                <span class="font-semibold">{p.name}</span>
                <!-- No per-environment badge here: the lit icon toggles on
                     the right already carry that state, and saying it twice
                     on one card only invites the two to disagree. -->
                {#if !p.allow_draft && !p.allow_qa && !p.allow_production}
                  <Badge tone="warning">parked</Badge>
                {/if}
                <Badge tone="neutral">{(p.static_members ?? []).length} device(s)</Badge>
              </div>
              {#if p.description}
                <p class="mt-1 text-sm text-surface-600-400">{p.description}</p>
              {/if}
            </div>
            <div class="flex items-center gap-2">
              <div class="inline-flex items-center gap-1">
                {#each ENVIRONMENTS as env (env.key)}
                  {@const on = p[env.key] === true}
                  <IconButton
                    icon={env.icon}
                    label={on
                      ? `Stop ${env.label} runs from targeting ${p.name}`
                      : `Allow ${env.label} runs to target ${p.name}`}
                    variant={on ? 'success' : 'ghost'}
                    onclick={() => toggleEnvironment(p, env.key)}
                  />
                {/each}
              </div>
              <IconButton icon={Pencil} label="Edit" onclick={() => openEdit(p)} />
              <IconButton icon={Trash2} label="Delete" variant="danger" onclick={() => remove(p)} />
            </div>
          </div>
          {#if isEditing && buf}
            <div class="mt-4 pt-4 border-t border-surface-300-700 space-y-3">
              <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
                <Input label="Name" help="pools.name" bind:value={buf.name} />
                <fieldset>
                  <legend class="text-xs text-surface-500 mb-1.5 inline-flex items-center gap-1">Environments <FieldHint id="pools.environments" /></legend>
                  {@render envToggles(buf)}
                </fieldset>
                <div class="md:col-span-2">
                  <Input label="Description" help="pools.description" bind:value={buf.description} />
                </div>
                <div class="md:col-span-2">
                  <DevicePicker
                    devices={deviceCatalog}
                    loading={loadingDevices}
                    selected={buf.memberIds}
                    onChange={(ids) => {
                      editBuf = { ...editBuf, [p.device_pool_id]: { ...buf, memberIds: ids } };
                    }}
                  />
                </div>
              </div>
              <div class="flex justify-end gap-2">
                <Button variant="ghost" onclick={() => (editing = null)}>Cancel</Button>
                <Button variant="primary" icon={Save} loading={saveLoading[p.device_pool_id]} onclick={() => save(p.device_pool_id)}>
                  Save
                </Button>
              </div>
            </div>
          {/if}
        </Card>
      {/each}
    </div>
  {/if}
</div>
