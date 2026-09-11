<script lang="ts">
  import type { CapabilityInfo } from '$lib/api/client';

  // Toggle-pill picker for the capabilities a grant confers, grouped by
  // domain (mirrors RuleBuilder's enum-pill styling). The parent owns the
  // selected key list; we only toggle membership. Each pill's title shows
  // the capability description on hover.
  let {
    capabilities = [],
    selected = $bindable([]),
    disabled = false,
  }: {
    capabilities?: CapabilityInfo[];
    selected: string[];
    disabled?: boolean;
  } = $props();

  const grouped = $derived.by(() => {
    const map = new Map<string, CapabilityInfo[]>();
    for (const c of capabilities) {
      const arr = map.get(c.domain) ?? [];
      arr.push(c);
      map.set(c.domain, arr);
    }
    return [...map.entries()]
      .map(([domain, caps]) => ({
        domain,
        caps: caps.slice().sort((a, b) => a.key.localeCompare(b.key)),
      }))
      .sort((a, b) => a.domain.localeCompare(b.domain));
  });

  function toggle(key: string) {
    if (disabled) return;
    selected = selected.includes(key) ? selected.filter((k) => k !== key) : [...selected, key];
  }
</script>

<div class="space-y-3">
  {#if capabilities.length === 0}
    <p class="text-xs text-surface-500">No capabilities available.</p>
  {:else}
    {#each grouped as group (group.domain)}
      <div>
        <span class="block text-xs uppercase tracking-wide text-surface-500 mb-1">{group.domain}</span>
        <div class="flex flex-wrap gap-1.5">
          {#each group.caps as cap (cap.key)}
            {@const on = selected.includes(cap.key)}
            <button
              type="button"
              {disabled}
              title={cap.description}
              class="px-2 py-1 rounded-full text-xs border transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
              class:bg-primary-500={on}
              class:text-white={on}
              class:border-primary-500={on}
              class:bg-surface-100-900={!on}
              class:border-surface-300-700={!on}
              class:text-surface-700-300={!on}
              onclick={() => toggle(cap.key)}
            >{cap.key}</button>
          {/each}
        </div>
      </div>
    {/each}
  {/if}
</div>
