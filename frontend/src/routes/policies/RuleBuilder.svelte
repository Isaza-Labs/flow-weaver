<script lang="ts">
  import { Input, Select, IconButton, Button } from '$lib/components/ui';
  import { X, Plus } from 'lucide-svelte';
  import { untrack, onMount } from 'svelte';
  import { devices, devicePools } from '$lib/api/client';
  import OptionChips from '$lib/components/OptionChips.svelte';

  // Two-way JSON-backed form for a policy rule. Parent owns the raw
  // JSON string; this component parses it, exposes typed controls, and
  // re-serializes on every change. Keeping the string as source of
  // truth means the JSON tab and the Visual tab never drift — whatever
  // the JSON says is what we render here and vice versa.
  //
  // The component handles two rule shapes:
  //   • action="deny" — original chip-based matcher (env / snippet_type
  //     / device_role / device_pool / description_contains / action /
  //     ssh_command_regex).
  //   • action="gate" — phase-3 promotion blocker. Edits `on`, `from`,
  //     `to` and a list of `require` items; each item picks a type
  //     (successful_runs / last_successful_run_within /
  //     successful_snippet_runs) and the corresponding parameters.
  // `error` used to be a $bindable, but a record-property bind
  // (`bind:error={errors[id]}`) returns undefined for unseeded keys
  // and Svelte 5 rejects that with props_invalid_value. A callback is
  // strictly one-way (child reports parse errors to parent) so it
  // sidesteps the problem entirely.
  let {
    ruleJson = $bindable(''),
    onError,
  }: {
    ruleJson: string;
    onError?: (msg: string | null) => void;
  } = $props();

  type DenyWhen = {
    env: string[];
    action: string[];
    snippet_type: string[];
    device_role: string[];
    device_pool: string[];
    description_contains: string[];
  };
  type DenyRule = { action: 'deny'; reason: string; when: DenyWhen };

  // State types: every field is concrete (no undefined) so Svelte 5
  // bindings to <Input>/<Select> don't trip props_invalid_value when a
  // policy is loaded without optional fields. Empty-string / zero are
  // the "absent" sentinels — persistGate() drops them on serialize so
  // the stored JSON stays compact.
  type GateRequirement =
    | { type: 'successful_runs'; min: number; within_days: number; scope: 'this_workflow' | 'any_workflow' }
    | { type: 'last_successful_run_within'; days: number; scope: 'this_workflow' | 'any_workflow' }
    | { type: 'successful_snippet_runs'; snippet_ids: string[]; min: number; within_days: number };
  type GateRule = {
    action: 'gate';
    on: 'promote';
    from: string;
    to: string;
    reason: string;
    require: GateRequirement[];
  };

  const envOptions = ['draft', 'qa', 'production'];
  const actionOptions = ['create', 'update', 'run', 'promote'];
  const snippetTypeSuggestions = [
    'ping', 'ssh', 'rest_call', 'python_snippet', 'ansible_playbook',
    'transform', 'jmespath', 'integration_action', 'report', 'subflow', 'git',
    'slack_message', 'email_send', 'mcp_call',
  ];

  let mode = $state<'deny' | 'gate'>('deny');
  let denyRule = $state<DenyRule>(emptyDeny());
  let gateRule = $state<GateRule>(emptyGate());
  let chipDraft = $state<Record<keyof DenyWhen, string>>({
    env: '', action: '', snippet_type: '',
    device_role: '', device_pool: '', description_contains: '',
  });
  let snippetIdDrafts = $state<string[]>([]); // one per requirement

  // Real device role / pool options for the selectors (no free typing).
  type Opt = { value: string; label: string };
  let roleOptions = $state<Opt[]>([]);
  let poolOptions = $state<Opt[]>([]);

  onMount(async () => {
    try {
      const [devs, pools] = await Promise.all([devices.list(500, 0), devicePools.list(200, 0)]);
      const roles = [
        ...new Set(devs.data.map((d) => d.role).filter((r): r is string => !!r && r.trim() !== '')),
      ].sort();
      roleOptions = roles.map((r) => ({ value: r, label: r }));
      poolOptions = pools.data
        .map((p) => ({ value: p.name, label: p.name }))
        .sort((a, b) => a.label.localeCompare(b.label));
    } catch {
      // Leave options empty — the selects show their emptyHint.
    }
  });

  function emptyDeny(): DenyRule {
    return {
      action: 'deny', reason: '',
      when: { env: [], action: [], snippet_type: [], device_role: [], device_pool: [], description_contains: [] },
    };
  }
  function emptyGate(): GateRule {
    return {
      action: 'gate', on: 'promote',
      from: 'qa', to: 'production',
      reason: '',
      require: [{ type: 'last_successful_run_within', days: 2, scope: 'this_workflow' }],
    };
  }

  function asStringArray(v: unknown): string[] {
    if (!Array.isArray(v)) return [];
    return v.filter((x) => typeof x === 'string' && x.trim().length > 0);
  }

  // Cache of the last error string we sent up so we can drop redundant
  // onError() calls (they'd trigger pointless parent re-renders).
  let lastErrorSent: string | null | undefined = undefined;

  // Parse incoming JSON. We pick the editor mode from the rule's
  // action, then hydrate the matching state object.
  //
  // Loop guard: the entire body runs inside `untrack` after a single
  // tracked read of `ruleJson`. Without this, reading the `onError` prop
  // (to invoke it) registered it as a dependency — and the parent
  // creates a fresh arrow each render, so every parent re-render flipped
  // the prop reference, re-ran the effect, called onError, mutated
  // parent state, which re-rendered the parent → effect_update_depth_exceeded.
  $effect(() => {
    const json = ruleJson; // single tracked dep
    untrack(() => parseIncoming(json));
  });

  function parseIncoming(json: string) {
    try {
      const parsed = JSON.parse(json || '{}');
      const action = (parsed.action ?? 'deny') as string;
      if (action === 'gate') {
        mode = 'gate';
        const requireRaw = Array.isArray(parsed.require) ? parsed.require : [];
        const requirements: GateRequirement[] = [];
        for (const r of requireRaw) {
          if (!r || typeof r !== 'object') continue;
          const t = String(r.type ?? '');
          if (t === 'successful_runs') {
            requirements.push({
              type: 'successful_runs',
              min: Number(r.min ?? 1),
              within_days: Number(r.within_days ?? 0),
              scope: r.scope === 'any_workflow' ? 'any_workflow' : 'this_workflow',
            });
          } else if (t === 'last_successful_run_within') {
            requirements.push({
              type: 'last_successful_run_within',
              days: Number(r.days ?? 1),
              scope: r.scope === 'any_workflow' ? 'any_workflow' : 'this_workflow',
            });
          } else if (t === 'successful_snippet_runs') {
            requirements.push({
              type: 'successful_snippet_runs',
              snippet_ids: asStringArray(r.snippet_ids),
              min: Number(r.min ?? 1),
              within_days: Number(r.within_days ?? 0),
            });
          }
        }
        // Build the next require list as a local first so we can also
        // size snippetIdDrafts off it WITHOUT reading gateRule after
        // assigning. Reading gateRule inside this $effect would mark
        // it as a dependency, and the same effect writes to gateRule —
        // Svelte 5 detects that as an update cycle and throws
        // effect_update_depth_exceeded.
        const nextRequire = requirements.length > 0 ? requirements : emptyGate().require;
        gateRule = {
          action: 'gate', on: 'promote',
          from: typeof parsed.from === 'string' ? parsed.from : '',
          to: typeof parsed.to === 'string' ? parsed.to : '',
          reason: typeof parsed.reason === 'string' ? parsed.reason : '',
          require: nextRequire,
        };
        snippetIdDrafts = nextRequire.map(() => '');
      } else {
        mode = 'deny';
        const when = parsed.when || {};
        denyRule = {
          action: 'deny',
          reason: typeof parsed.reason === 'string' ? parsed.reason : '',
          when: {
            env: asStringArray(when.env),
            action: asStringArray(when.action),
            snippet_type: asStringArray(when.snippet_type),
            device_role: asStringArray(when.device_role),
            device_pool: asStringArray(when.device_pool),
            description_contains: asStringArray(when.description_contains),
          },
        };
      }
      reportError(null);
    } catch (e) {
      reportError(`Rule JSON invalid: ${(e as Error).message}`);
    }
  }

  function reportError(msg: string | null) {
    if (lastErrorSent === msg) return; // skip redundant calls
    lastErrorSent = msg;
    onError?.(msg);
  }

  function persistDeny() {
    const when: Record<string, string[]> = {};
    for (const [k, v] of Object.entries(denyRule.when)) {
      if (v.length > 0) when[k] = v;
    }
    const out: Record<string, unknown> = { action: 'deny', reason: denyRule.reason };
    if (Object.keys(when).length > 0) out.when = when;
    ruleJson = JSON.stringify(out, null, 2);
  }

  function persistGate() {
    const out: Record<string, unknown> = {
      action: 'gate',
      on: gateRule.on,
      reason: gateRule.reason,
      require: gateRule.require.map((r) => {
        // Drop empty optional fields so the JSON stays compact.
        const obj: Record<string, unknown> = { type: r.type };
        if (r.type === 'successful_runs') {
          obj.min = r.min;
          if (r.within_days && r.within_days > 0) obj.within_days = r.within_days;
          if (r.scope) obj.scope = r.scope;
        } else if (r.type === 'last_successful_run_within') {
          obj.days = r.days;
          if (r.scope) obj.scope = r.scope;
        } else if (r.type === 'successful_snippet_runs') {
          obj.min = r.min;
          obj.snippet_ids = r.snippet_ids;
          if (r.within_days && r.within_days > 0) obj.within_days = r.within_days;
        }
        return obj;
      }),
    };
    if (gateRule.from) out.from = gateRule.from;
    if (gateRule.to) out.to = gateRule.to;
    ruleJson = JSON.stringify(out, null, 2);
  }

  function persist() { mode === 'deny' ? persistDeny() : persistGate(); }

  function switchMode(next: 'deny' | 'gate') {
    if (mode === next) return;
    mode = next;
    persist();
  }

  // Deny helpers
  function toggleEnum(field: 'env' | 'action', value: string) {
    const list = denyRule.when[field];
    denyRule.when[field] = list.includes(value) ? list.filter((v) => v !== value) : [...list, value];
    persistDeny();
  }
  function addChip(field: keyof DenyWhen) {
    const draft = chipDraft[field].trim();
    if (!draft) return;
    if (denyRule.when[field].includes(draft)) { chipDraft[field] = ''; return; }
    denyRule.when[field] = [...denyRule.when[field], draft];
    chipDraft[field] = '';
    persistDeny();
  }
  function removeChip(field: keyof DenyWhen, value: string) {
    denyRule.when[field] = denyRule.when[field].filter((v) => v !== value);
    persistDeny();
  }

  // Gate helpers
  function setReqType(idx: number, type: GateRequirement['type']) {
    if (type === 'successful_runs') {
      gateRule.require[idx] = { type: 'successful_runs', min: 1, within_days: 7, scope: 'this_workflow' };
    } else if (type === 'last_successful_run_within') {
      gateRule.require[idx] = { type: 'last_successful_run_within', days: 2, scope: 'this_workflow' };
    } else {
      gateRule.require[idx] = { type: 'successful_snippet_runs', snippet_ids: [], min: 1, within_days: 30 };
    }
    persistGate();
  }
  function addRequirement() {
    gateRule.require = [...gateRule.require, { type: 'successful_runs', min: 1, within_days: 7, scope: 'this_workflow' }];
    snippetIdDrafts = [...snippetIdDrafts, ''];
    persistGate();
  }
  function removeRequirement(idx: number) {
    gateRule.require = gateRule.require.filter((_, i) => i !== idx);
    snippetIdDrafts = snippetIdDrafts.filter((_, i) => i !== idx);
    persistGate();
  }
  function addSnippetId(idx: number) {
    const r = gateRule.require[idx];
    if (r.type !== 'successful_snippet_runs') return;
    const v = (snippetIdDrafts[idx] ?? '').trim();
    if (!v || r.snippet_ids.includes(v)) { snippetIdDrafts[idx] = ''; return; }
    r.snippet_ids = [...r.snippet_ids, v];
    snippetIdDrafts[idx] = '';
    persistGate();
  }
  function removeSnippetId(idx: number, id: string) {
    const r = gateRule.require[idx];
    if (r.type !== 'successful_snippet_runs') return;
    r.snippet_ids = r.snippet_ids.filter((x) => x !== id);
    persistGate();
  }
</script>

<div class="space-y-4">
  <!-- Mode toggle -->
  <div class="flex items-center gap-2">
    <span class="text-xs uppercase tracking-wide text-surface-500">Type</span>
    <div class="inline-flex rounded-md bg-surface-100-900 ring-1 ring-surface-300-700 p-0.5">
      <button
        type="button"
        class="px-3 py-1 text-xs font-medium rounded transition-colors"
        class:bg-primary-500={mode === 'deny'}
        class:text-white={mode === 'deny'}
        class:text-surface-600-400={mode !== 'deny'}
        onclick={() => switchMode('deny')}
      >Deny rule</button>
      <button
        type="button"
        class="px-3 py-1 text-xs font-medium rounded transition-colors"
        class:bg-primary-500={mode === 'gate'}
        class:text-white={mode === 'gate'}
        class:text-surface-600-400={mode !== 'gate'}
        onclick={() => switchMode('gate')}
      >Gate (promotion)</button>
    </div>
    <span class="text-[11px] text-surface-500 ml-2">
      {#if mode === 'deny'}
        Block matching create / update / run operations.
      {:else}
        Block a promotion until historical conditions are met.
      {/if}
    </span>
  </div>

  <!-- Common: reason -->
  <div>
    <label class="block text-xs uppercase tracking-wide text-surface-500 mb-1" for="rule-reason">
      Reason (shown when this fires)
    </label>
    <Input
      id="rule-reason"
      value={mode === 'deny' ? denyRule.reason : gateRule.reason}
      oninput={(e: any) => {
        if (mode === 'deny') { denyRule.reason = e.target.value; persistDeny(); }
        else { gateRule.reason = e.target.value; persistGate(); }
      }}
      placeholder={mode === 'deny' ? 'e.g. SSH to production requires a change window' : 'e.g. qa validation required'}
    />
  </div>

  {#if mode === 'deny'}
    <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
      <div>
        <span class="block text-xs uppercase tracking-wide text-surface-500 mb-1">Environment</span>
        <div class="flex flex-wrap gap-1.5">
          {#each envOptions as env}
            {@const selected = denyRule.when.env.includes(env)}
            <button
              type="button"
              class="px-2 py-1 rounded-full text-xs border transition-colors"
              class:bg-primary-500={selected}
              class:text-white={selected}
              class:border-primary-500={selected}
              class:bg-surface-100-900={!selected}
              class:border-surface-300-700={!selected}
              class:text-surface-700-300={!selected}
              onclick={() => toggleEnum('env', env)}
            >{env}</button>
          {/each}
        </div>
      </div>
      <div>
        <span class="block text-xs uppercase tracking-wide text-surface-500 mb-1">Operation</span>
        <div class="flex flex-wrap gap-1.5">
          {#each actionOptions as actionOpt}
            {@const selected = denyRule.when.action.includes(actionOpt)}
            <button
              type="button"
              class="px-2 py-1 rounded-full text-xs border transition-colors"
              class:bg-primary-500={selected}
              class:text-white={selected}
              class:border-primary-500={selected}
              class:bg-surface-100-900={!selected}
              class:border-surface-300-700={!selected}
              class:text-surface-700-300={!selected}
              onclick={() => toggleEnum('action', actionOpt)}
            >{actionOpt}</button>
          {/each}
        </div>
      </div>
    </div>

    {#each [
      { field: 'snippet_type', label: 'Snippet types', placeholder: 'ssh, ping, integration_action…', suggestions: snippetTypeSuggestions },
      { field: 'description_contains', label: 'Description contains (any)', placeholder: 'bgp, reload, shutdown…', suggestions: [] as string[] },
    ] as row}
      {@const f = row.field as keyof DenyWhen}
      <div>
        <span class="block text-xs uppercase tracking-wide text-surface-500 mb-1">{row.label}</span>
        <div class="flex flex-wrap items-center gap-1.5 mb-1">
          {#each denyRule.when[f] as chip}
            <span class="inline-flex items-center gap-1 bg-surface-100-900 border border-surface-300-700 rounded-full px-2 py-0.5 text-xs">
              {chip}
              <button type="button" class="hover:text-error-400" onclick={() => removeChip(f, chip)} aria-label="Remove">
                <X size={10} />
              </button>
            </span>
          {/each}
        </div>
        <div class="flex gap-2">
          <Input
            bind:value={chipDraft[f]}
            placeholder={row.placeholder}
            onkeydown={(e: KeyboardEvent) => { if (e.key === 'Enter') { e.preventDefault(); addChip(f); } }}
          />
          <Button icon={Plus} onclick={() => addChip(f)}>Add</Button>
        </div>
        {#if row.suggestions.length > 0}
          <div class="flex flex-wrap gap-1 mt-1">
            {#each row.suggestions.filter((s) => !denyRule.when[f].includes(s)) as sug}
              <button
                type="button"
                class="text-[10px] px-1.5 py-0.5 rounded bg-surface-100-900 border border-dashed border-surface-300-700 text-surface-500 hover:text-surface-700-300"
                onclick={() => { denyRule.when[f] = [...denyRule.when[f], sug]; persistDeny(); }}
              >+ {sug}</button>
            {/each}
          </div>
        {/if}
      </div>
    {/each}

    <OptionChips
      label="Device roles"
      options={roleOptions}
      bind:values={denyRule.when.device_role}
      onChange={persistDeny}
      emptyHint="No device roles found"
    />
    <OptionChips
      label="Device pools (by name)"
      options={poolOptions}
      bind:values={denyRule.when.device_pool}
      onChange={persistDeny}
      emptyHint="No device pools found"
    />
  {:else}
    <!-- Gate editor -->
    <div class="grid grid-cols-1 md:grid-cols-3 gap-3">
      <div>
        <label class="block text-xs uppercase tracking-wide text-surface-500 mb-1" for="g-on">On</label>
        <Select id="g-on" bind:value={gateRule.on} onchange={persistGate}>
          <option value="promote">promote</option>
        </Select>
      </div>
      <div>
        <label class="block text-xs uppercase tracking-wide text-surface-500 mb-1" for="g-from">From environment</label>
        <Select id="g-from" bind:value={gateRule.from} onchange={persistGate}>
          <option value="">— any —</option>
          <option value="draft">draft</option>
          <option value="qa">qa</option>
          <option value="production">production</option>
        </Select>
      </div>
      <div>
        <label class="block text-xs uppercase tracking-wide text-surface-500 mb-1" for="g-to">To environment</label>
        <Select id="g-to" bind:value={gateRule.to} onchange={persistGate}>
          <option value="">— any —</option>
          <option value="draft">draft</option>
          <option value="qa">qa</option>
          <option value="production">production</option>
        </Select>
      </div>
    </div>

    <div>
      <div class="flex items-center justify-between mb-2">
        <span class="block text-xs uppercase tracking-wide text-surface-500">Requirements (all must pass)</span>
        <Button size="xs" icon={Plus} onclick={addRequirement}>Add requirement</Button>
      </div>
      <div class="space-y-3">
        {#each gateRule.require as req, idx}
          <div class="rounded-md border border-surface-300-700 bg-surface-100-900/50 p-3">
            <div class="flex items-center gap-2 mb-2">
              <Select value={req.type} onchange={(e: any) => setReqType(idx, e.target.value)}>
                <option value="successful_runs">N successful runs</option>
                <option value="last_successful_run_within">Last successful run within X days</option>
                <option value="successful_snippet_runs">N successful runs of specific snippets</option>
              </Select>
              <IconButton icon={X} label="Remove" onclick={() => removeRequirement(idx)} />
            </div>

            {#if req.type === 'successful_runs'}
              <div class="grid grid-cols-1 md:grid-cols-3 gap-2">
                <div>
                  <span class="block text-[11px] text-surface-500 mb-1">Min runs</span>
                  <Input type="number" min="1" bind:value={req.min} oninput={persistGate} />
                </div>
                <div>
                  <span class="block text-[11px] text-surface-500 mb-1">Within (days, 0 = any time)</span>
                  <Input type="number" min="0" bind:value={req.within_days} oninput={persistGate} />
                </div>
                <div>
                  <span class="block text-[11px] text-surface-500 mb-1">Scope</span>
                  <Select bind:value={req.scope} onchange={persistGate}>
                    <option value="this_workflow">This workflow</option>
                    <option value="any_workflow">Any workflow</option>
                  </Select>
                </div>
              </div>
            {:else if req.type === 'last_successful_run_within'}
              <div class="grid grid-cols-1 md:grid-cols-2 gap-2">
                <div>
                  <span class="block text-[11px] text-surface-500 mb-1">Within (days)</span>
                  <Input type="number" min="1" bind:value={req.days} oninput={persistGate} />
                </div>
                <div>
                  <span class="block text-[11px] text-surface-500 mb-1">Scope</span>
                  <Select bind:value={req.scope} onchange={persistGate}>
                    <option value="this_workflow">This workflow</option>
                    <option value="any_workflow">Any workflow</option>
                  </Select>
                </div>
              </div>
            {:else}
              <div class="grid grid-cols-1 md:grid-cols-2 gap-2">
                <div>
                  <span class="block text-[11px] text-surface-500 mb-1">Min runs</span>
                  <Input type="number" min="1" bind:value={req.min} oninput={persistGate} />
                </div>
                <div>
                  <span class="block text-[11px] text-surface-500 mb-1">Within (days, 0 = any time)</span>
                  <Input type="number" min="0" bind:value={req.within_days} oninput={persistGate} />
                </div>
              </div>
              <div class="mt-2">
                <span class="block text-[11px] text-surface-500 mb-1">Snippet IDs</span>
                <div class="flex flex-wrap items-center gap-1.5 mb-1">
                  {#each req.snippet_ids as sid}
                    <span class="inline-flex items-center gap-1 bg-surface-100-900 border border-surface-300-700 rounded-full px-2 py-0.5 text-[11px] font-mono">
                      {sid.slice(0, 8)}
                      <button type="button" class="hover:text-error-400" onclick={() => removeSnippetId(idx, sid)} aria-label="Remove">
                        <X size={10} />
                      </button>
                    </span>
                  {/each}
                </div>
                <div class="flex gap-2">
                  <Input
                    bind:value={snippetIdDrafts[idx]}
                    placeholder="snippet UUID"
                    onkeydown={(e: KeyboardEvent) => { if (e.key === 'Enter') { e.preventDefault(); addSnippetId(idx); } }}
                  />
                  <Button icon={Plus} onclick={() => addSnippetId(idx)}>Add</Button>
                </div>
              </div>
            {/if}
          </div>
        {/each}
      </div>
    </div>
  {/if}

  <div class="rounded-md border border-surface-300-700 bg-surface-100-900/50 p-3">
    <div class="text-[10px] uppercase tracking-wide text-surface-500 mb-1">Preview</div>
    <pre class="text-xs font-mono overflow-auto">{ruleJson}</pre>
  </div>
</div>
