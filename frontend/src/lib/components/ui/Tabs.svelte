<script lang="ts" generics="T extends string">
  let {
    value = $bindable(),
    tabs,
    onChange,
    label = 'Tabs',
  }: {
    value: T;
    tabs: { value: T; label: string; count?: number }[];
    onChange?: (v: T) => void;
    label?: string;
  } = $props();

  let tablistEl = $state<HTMLDivElement | null>(null);

  function select(v: T) {
    value = v;
    onChange?.(v);
  }

  // Roving-tabindex keyboard support per the WAI-ARIA tabs pattern: Arrow
  // keys move between tabs (and select), Home/End jump to ends.
  function onKeydown(e: KeyboardEvent) {
    const idx = tabs.findIndex((t) => t.value === value);
    if (idx < 0) return;
    let next = idx;
    if (e.key === 'ArrowRight' || e.key === 'ArrowDown') next = (idx + 1) % tabs.length;
    else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') next = (idx - 1 + tabs.length) % tabs.length;
    else if (e.key === 'Home') next = 0;
    else if (e.key === 'End') next = tabs.length - 1;
    else return;
    e.preventDefault();
    select(tabs[next].value);
    tablistEl?.querySelectorAll<HTMLButtonElement>('[role="tab"]')[next]?.focus();
  }
</script>

<div
  bind:this={tablistEl}
  role="tablist"
  tabindex="-1"
  aria-label={label}
  onkeydown={onKeydown}
  class="inline-flex items-center gap-0.5 p-0.5 bg-surface-200-800/60 border border-surface-300-700/60 rounded-md"
>
  {#each tabs as tab (tab.value)}
    <button
      type="button"
      role="tab"
      aria-selected={value === tab.value}
      tabindex={value === tab.value ? 0 : -1}
      onclick={() => select(tab.value)}
      class="px-3 h-7 text-xs font-medium rounded transition-colors {value === tab.value ? 'bg-surface-100-900 text-surface-900-100 shadow-sm' : 'text-surface-600-400 hover:text-surface-800-200'}"
    >
      {tab.label}
      {#if tab.count !== undefined}
        <span class="ml-1 tabular-nums opacity-60">{tab.count}</span>
      {/if}
    </button>
  {/each}
</div>
