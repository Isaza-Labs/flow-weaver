<script lang="ts">
  // Multi-select picker for uv environments, shown as a small dialog (it can
  // stack on top of another Dialog, e.g. the Packages add/edit form).
  //
  // It edits a private draft of `selected`: Cancel, Esc, the close button or
  // a backdrop click discard it, and only Apply hands the new id list back
  // through `onApply`. The list has a fixed max height and a search box, so
  // the dialog stays the same size however many environments exist.
  //
  // Render it as a sibling of the form's Dialog, not inside its body: each
  // Dialog handles Esc on its own overlay, and nesting would let one Esc
  // close both.
  import { untrack } from 'svelte';
  import { Check } from 'lucide-svelte';
  import type { PythonEnvironment } from '$lib/api/client';
  import { Dialog, Button, Badge, SearchInput } from '$lib/components/ui';
  import { installStatusTone } from './envStatus';

  let {
    open = $bindable(false),
    environments,
    selected,
    title = 'Choose environments',
    description,
    onApply,
  }: {
    open?: boolean;
    environments: PythonEnvironment[];
    /** Ids selected when the picker opens. Not mutated. */
    selected: string[];
    title?: string;
    description?: string;
    onApply: (ids: string[]) => void;
  } = $props();

  let draft = $state<string[]>([]);
  let query = $state('');

  // Fresh draft every time the picker opens; reading `selected` untracked so
  // a parent re-render while it is open cannot wipe the user's edits.
  $effect.pre(() => {
    if (!open) return;
    untrack(() => {
      draft = [...selected];
      query = '';
    });
  });

  const visible = $derived.by(() => {
    const q = query.trim().toLowerCase();
    if (!q) return environments;
    return environments.filter(
      (e) => e.name.toLowerCase().includes(q) || (e.description ?? '').toLowerCase().includes(q),
    );
  });

  const draftSet = $derived(new Set(draft));
  const allVisibleSelected = $derived(
    visible.length > 0 && visible.every((e) => draftSet.has(e.python_environment_id)),
  );

  function toggle(id: string) {
    draft = draftSet.has(id) ? draft.filter((x) => x !== id) : [...draft, id];
  }

  function selectAllVisible() {
    const add = visible.map((e) => e.python_environment_id).filter((id) => !draftSet.has(id));
    draft = [...draft, ...add];
  }

  function clear() {
    draft = [];
  }

  function apply() {
    // Keep ids that are not in the list (deleted since, or the list failed to
    // load) — dropping them silently would change the row on save.
    onApply([...draft]);
    open = false;
  }
</script>

<Dialog bind:open {title} {description} size="sm">
  <div class="space-y-3">
    <SearchInput bind:value={query} placeholder="Search environments…" width="w-full" />

    <div class="flex items-center justify-between gap-2 text-xs">
      <span class="text-surface-600-400" aria-live="polite">
        {draft.length} selected{#if query.trim()} · showing {visible.length} of {environments.length}{/if}
      </span>
      <span class="inline-flex items-center gap-1">
        <Button variant="ghost" size="sm" onclick={selectAllVisible} disabled={visible.length === 0 || allVisibleSelected}>
          Select all visible
        </Button>
        <Button variant="ghost" size="sm" onclick={clear} disabled={draft.length === 0}>Clear</Button>
      </span>
    </div>

    <div
      class="max-h-72 overflow-y-auto rounded-md border border-surface-300-700 bg-surface-50-950"
      role="group"
      aria-label="Environments"
    >
      {#each visible as env (env.python_environment_id)}
        {@const on = draftSet.has(env.python_environment_id)}
        <button
          type="button"
          role="checkbox"
          aria-checked={on}
          onclick={() => toggle(env.python_environment_id)}
          class="group w-full flex items-start gap-3 px-3 py-2 text-left border-b border-surface-200-800 last:border-b-0 transition-colors hover:bg-surface-200-800 focus:outline-none focus-visible:bg-surface-200-800 focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-primary-500/40"
        >
          <span
            class="mt-0.5 inline-flex items-center justify-center w-4 h-4 shrink-0 rounded border transition-colors
              {on
                ? 'bg-primary-500 border-primary-500 text-white'
                : 'bg-surface-50-950 border-surface-300-700 group-hover:border-primary-400'}"
          >
            {#if on}<Check size={11} />{/if}
          </span>
          <span class="min-w-0 flex-1">
            <span class="flex items-center gap-1.5 min-w-0">
              <span class="font-mono text-sm text-surface-900-100 truncate" title={env.name}>{env.name}</span>
              {#if env.is_ssh_fallback}<Badge tone="primary" size="xs">SSH fallback</Badge>{/if}
            </span>
            {#if env.description}
              <span class="block text-xs text-surface-500 truncate" title={env.description}>{env.description}</span>
            {/if}
          </span>
          <span class="shrink-0"><Badge tone={installStatusTone(env.status)} size="xs" mono>{env.status}</Badge></span>
        </button>
      {:else}
        <p class="px-3 py-6 text-center text-xs text-surface-500">
          {environments.length === 0 ? 'No environments.' : `No environments match “${query.trim()}”.`}
        </p>
      {/each}
    </div>
  </div>
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (open = false)}>Cancel</Button>
    <Button variant="primary" onclick={apply}>Apply</Button>
  {/snippet}
</Dialog>
