<script lang="ts">
  import { Input } from '$lib/components/ui';
  import { untrack, onMount } from 'svelte';
  import { devices, devicePools } from '$lib/api/client';
  import OptionChips from '$lib/components/OptionChips.svelte';

  // Two-way JSON-backed editor for a permission grant's `conditions`
  // (the ABAC scoping). The parent owns the raw JSON string; this
  // component parses it, exposes typed chip / pill controls, and
  // re-serializes on every change. Keeping the string as the single
  // source of truth means the Visual tab and the JSON tab never drift.
  //
  // An empty object ("{}") means "any context" — the grant applies
  // everywhere. Empty arrays and blank resource fields are dropped on
  // serialize so the stored JSON stays compact.
  //
  // `onError` is a one-way callback (child → parent) rather than a
  // $bindable: a record-property bind returns undefined for unseeded keys
  // and Svelte 5 rejects that with props_invalid_value.
  let {
    conditionsJson = $bindable('{}'),
    onError,
  }: {
    conditionsJson: string;
    onError?: (msg: string | null) => void;
  } = $props();

  type Conditions = {
    environment: string[];
    device_role: string[];
    device_pool: string[];
    device_ids: string[];
    resource_type: string;
    resource_id: string;
  };

  const envOptions = ['draft', 'qa', 'production'];

  let cond = $state<Conditions>(empty());

  // Real options for the device selectors, loaded once from the API so the
  // user picks existing roles / pools / devices instead of typing them.
  type Opt = { value: string; label: string };
  let roleOptions = $state<Opt[]>([]);
  let poolOptions = $state<Opt[]>([]);
  let deviceOptions = $state<Opt[]>([]);

  onMount(async () => {
    try {
      const [devs, pools] = await Promise.all([devices.list(500, 0), devicePools.list(200, 0)]);
      const roles = [
        ...new Set(devs.data.map((d) => d.role).filter((r): r is string => !!r && r.trim() !== '')),
      ].sort();
      roleOptions = roles.map((r) => ({ value: r, label: r }));
      deviceOptions = devs.data
        .map((d) => ({ value: d.id, label: d.name }))
        .sort((a, b) => a.label.localeCompare(b.label));
      poolOptions = pools.data
        .map((p) => ({ value: p.name, label: p.name }))
        .sort((a, b) => a.label.localeCompare(b.label));
    } catch {
      // Leave options empty — the selects show their emptyHint.
    }
  });

  function empty(): Conditions {
    return {
      environment: [],
      device_role: [],
      device_pool: [],
      device_ids: [],
      resource_type: '',
      resource_id: '',
    };
  }

  function asStringArray(v: unknown): string[] {
    if (!Array.isArray(v)) return [];
    return v.filter((x) => typeof x === 'string' && x.trim().length > 0);
  }

  // Cache of the last error we sent so redundant onError() calls (which
  // would trigger pointless parent re-renders) are dropped.
  let lastErrorSent: string | null | undefined = undefined;

  // Parse incoming JSON. Loop guard mirrors RuleBuilder: the whole body
  // runs inside `untrack` after a single tracked read of conditionsJson,
  // so reading/invoking the `onError` prop (a fresh arrow from the parent
  // each render) never registers as a dependency and can't drive an
  // effect_update_depth_exceeded cycle.
  $effect(() => {
    const json = conditionsJson; // single tracked dep
    untrack(() => parseIncoming(json));
  });

  function parseIncoming(json: string) {
    try {
      const parsed = JSON.parse(json || '{}');
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
        throw new Error('conditions must be a JSON object');
      }
      const resource =
        parsed.resource && typeof parsed.resource === 'object' && !Array.isArray(parsed.resource)
          ? (parsed.resource as Record<string, unknown>)
          : {};
      cond = {
        environment: asStringArray(parsed.environment),
        device_role: asStringArray(parsed.device_role),
        device_pool: asStringArray(parsed.device_pool),
        device_ids: asStringArray(parsed.device_ids),
        resource_type: typeof resource.type === 'string' ? resource.type : '',
        resource_id: typeof resource.id === 'string' ? resource.id : '',
      };
      reportError(null);
    } catch (e) {
      reportError(`Conditions JSON invalid: ${(e as Error).message}`);
    }
  }

  function reportError(msg: string | null) {
    if (lastErrorSent === msg) return; // skip redundant calls
    lastErrorSent = msg;
    onError?.(msg);
  }

  function persist() {
    const out: Record<string, unknown> = {};
    if (cond.environment.length > 0) out.environment = cond.environment;
    if (cond.device_role.length > 0) out.device_role = cond.device_role;
    if (cond.device_pool.length > 0) out.device_pool = cond.device_pool;
    if (cond.device_ids.length > 0) out.device_ids = cond.device_ids;
    const rt = cond.resource_type.trim();
    const rid = cond.resource_id.trim();
    if (rt || rid) {
      const resource: Record<string, string> = {};
      if (rt) resource.type = rt;
      if (rid) resource.id = rid;
      out.resource = resource;
    }
    conditionsJson = JSON.stringify(out, null, 2);
  }

  function toggleEnv(value: string) {
    cond.environment = cond.environment.includes(value)
      ? cond.environment.filter((v) => v !== value)
      : [...cond.environment, value];
    persist();
  }

  const isEmpty = $derived(
    cond.environment.length === 0 &&
      cond.device_role.length === 0 &&
      cond.device_pool.length === 0 &&
      cond.device_ids.length === 0 &&
      cond.resource_type.trim() === '' &&
      cond.resource_id.trim() === '',
  );
</script>

<div class="space-y-4">
  <!-- Environment -->
  <div>
    <span class="block text-xs uppercase tracking-wide text-surface-500 mb-1">Environment</span>
    <div class="flex flex-wrap gap-1.5">
      {#each envOptions as env}
        {@const selected = cond.environment.includes(env)}
        <button
          type="button"
          class="px-2 py-1 rounded-full text-xs border transition-colors"
          class:bg-primary-500={selected}
          class:text-white={selected}
          class:border-primary-500={selected}
          class:bg-surface-100-900={!selected}
          class:border-surface-300-700={!selected}
          class:text-surface-700-300={!selected}
          onclick={() => toggleEnv(env)}
        >{env}</button>
      {/each}
    </div>
  </div>

  <!-- Device matchers: pick from real devices / pools (no free typing) -->
  <OptionChips
    label="Device roles"
    options={roleOptions}
    bind:values={cond.device_role}
    onChange={persist}
    emptyHint="No device roles found"
  />
  <OptionChips
    label="Device pools (by name)"
    options={poolOptions}
    bind:values={cond.device_pool}
    onChange={persist}
    emptyHint="No device pools found"
  />
  <OptionChips
    label="Device IDs"
    options={deviceOptions}
    bind:values={cond.device_ids}
    onChange={persist}
    mono
    emptyHint="No devices found"
  />

  <!-- Optional single resource matcher -->
  <div>
    <span class="block text-xs uppercase tracking-wide text-surface-500 mb-1">Resource (optional)</span>
    <div class="grid grid-cols-1 md:grid-cols-2 gap-2">
      <Input
        bind:value={cond.resource_type}
        placeholder="type — workflow, integration…"
        oninput={persist}
      />
      <Input bind:value={cond.resource_id} placeholder="resource UUID" oninput={persist} />
    </div>
  </div>

  <div class="rounded-md border border-surface-300-700 bg-surface-100-900/50 p-3">
    <div class="text-[10px] uppercase tracking-wide text-surface-500 mb-1">Preview</div>
    {#if isEmpty}
      <p class="text-xs text-surface-500">Empty — this grant applies in <strong>any</strong> context.</p>
    {:else}
      <pre class="text-xs font-mono overflow-auto">{conditionsJson}</pre>
    {/if}
  </div>
</div>
