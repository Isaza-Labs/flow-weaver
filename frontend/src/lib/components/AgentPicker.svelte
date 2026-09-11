<script lang="ts">
  import { Bot } from 'lucide-svelte';

  // Which agent answers this conversation, as a compact control that sits in the
  // composer rather than as a labelled form field in the sidebar.
  //
  // The shape is deliberate: the visible label is a styled span, and the native
  // <select> is laid transparent on top of it. That keeps the whole of the
  // browser's keyboard and touch behaviour — type-ahead, Escape, the platform
  // picker sheet on mobile — while letting the label be truncated and coloured,
  // which a styled <option> list cannot be.
  let {
    agents = [],
    value = $bindable(''),
    disabled = false
  }: {
    agents?: Array<{ id: string; name: string }>;
    /** Agent id, or '' for the default assistant agent. */
    value?: string;
    disabled?: boolean;
  } = $props();

  const selected = $derived(value ? agents.find((a) => a.id === value) : undefined);

  // A conversation can hold an agent that has since been deleted or disabled.
  // Saying so beats snapping the label to "Default", which would be a lie about
  // which agent the next turn goes to.
  const orphaned = $derived(!!value && agents.length > 0 && !selected);

  const label = $derived(
    selected ? selected.name : orphaned ? 'Unavailable agent' : 'Default agent'
  );
</script>

{#if agents.length > 0}
  <div
    class="inline-flex items-center gap-1.5 rounded-lg border border-surface-300-700 bg-surface-100-900 py-1 pl-2 pr-1 text-xs text-surface-700-300 transition focus-within:border-primary-500 hover:bg-surface-200-800 dark:focus-within:border-primary-400"
    class:opacity-60={disabled}
  >
    <Bot size={13} class="shrink-0 text-surface-600-400" />
    <span class="relative">
      <span class="block max-w-[220px] truncate" class:text-warning-600-400={orphaned}>{label}</span>
      <select
        bind:value
        {disabled}
        aria-label="Agent"
        title={orphaned
          ? 'This conversation used an agent that no longer exists. Pick another to continue.'
          : 'Which agent answers this conversation'}
        class="absolute inset-0 w-full cursor-pointer opacity-0 disabled:cursor-not-allowed"
      >
        {#if orphaned}
          <!-- Reachable only while the stored agent is missing; never something to
               switch back to. -->
          <option value={value} disabled>{label}</option>
        {/if}
        <option value="">Default agent</option>
        {#each agents as a (a.id)}
          <option value={a.id}>{a.name}</option>
        {/each}
      </select>
    </span>
  </div>
{/if}
