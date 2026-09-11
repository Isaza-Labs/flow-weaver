<script lang="ts">
  import { statusTone, toneBadgeClass, toneDotClass } from './status';

  let {
    status,
    showDot = true,
    label,
    pulse = false,
  }: {
    status: string;
    showDot?: boolean;
    label?: string;
    pulse?: boolean;
  } = $props();

  const tone = $derived(statusTone(status));
  const badgeClass = $derived(toneBadgeClass(tone));
  const dotClass = $derived(toneDotClass(tone));
  const isRunning = $derived(status?.toLowerCase() === 'running' || status?.toLowerCase() === 'in_progress');
</script>

<span class="inline-flex items-center gap-1.5 rounded-full text-[11px] font-medium leading-none px-2 py-1 {badgeClass}">
  {#if showDot}
    <span class="relative flex w-1.5 h-1.5">
      {#if pulse || isRunning}
        <span class="absolute inset-0 rounded-full {dotClass} opacity-60 animate-ping"></span>
      {/if}
      <span class="relative w-1.5 h-1.5 rounded-full {dotClass}"></span>
    </span>
  {/if}
  <span class="capitalize tracking-wide">{label ?? status ?? 'unknown'}</span>
</span>
