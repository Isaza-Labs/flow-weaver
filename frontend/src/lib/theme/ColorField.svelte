<script lang="ts">
  // One palette row in the theme editor: swatch picker, hex field, the
  // generated ramp, and a reset.
  //
  // The ramp strip is not decoration — it's the only way to see where the
  // picked colour actually landed. `buildRamp` anchors the base at the shade
  // matching its lightness, so a near-black surface becomes shade 950 rather
  // than 500, and the marker under the strip says which one.
  import { buildRamp, anchorFor, contrastEndFor, contrastRatio, SHADES } from './ramp';
  import { RotateCcw, AlertTriangle } from 'lucide-svelte';

  let {
    label,
    hint,
    value = $bindable(),
    fallback,
    surface = false,
  }: { label: string; hint: string; value: string; fallback: string; surface?: boolean } = $props();

  const ramp = $derived(buildRamp(value));
  const anchor = $derived(anchorFor(value));
  const isDefault = $derived(value.toLowerCase() === fallback.toLowerCase());

  // WCAG check on the combination this palette actually powers. For accents
  // that's the filled button: 500 fill under its generated contrast text
  // (AA wants 4.5:1). For surface it's body text: the two ramp ends carry
  // the page's text-on-background in both modes, and a muted base colour can
  // quietly produce a low-contrast ramp that "looks fine" in the editor.
  // A warning, not a blocker — a decorative theme is allowed to be soft.
  const contrastWarning = $derived.by(() => {
    if (!ramp) return null;
    if (surface) {
      const ratio = contrastRatio(ramp[50], ramp[950]);
      return ratio < 7
        ? `Body text lands at ${ratio.toFixed(1)}:1 between the ramp ends — text will look washed out (7:1 is comfortable).`
        : null;
    }
    const text = contrastEndFor(ramp[500]) === 'dark' ? ramp[950] : ramp[50];
    const ratio = contrastRatio(ramp[500], text);
    return ratio < 4.5
      ? `Button labels land at ${ratio.toFixed(1)}:1 on the 500 fill — below the 4.5:1 AA threshold.`
      : null;
  });

  // The native picker only emits lowercase #rrggbb, but the text field lets
  // people paste anything — keep the swatch on the last valid value instead of
  // letting an in-progress "#5c6" blank it out.
  const swatchValue = $derived(ramp ? value : fallback);

  function onHexInput(e: Event) {
    const raw = (e.currentTarget as HTMLInputElement).value.trim();
    value = raw.startsWith('#') || raw === '' ? raw : `#${raw}`;
  }
</script>

<div class="flex flex-col gap-1.5 py-2.5">
  <div class="flex items-center gap-2.5">
    <input
      type="color"
      value={swatchValue}
      oninput={(e) => (value = (e.currentTarget as HTMLInputElement).value)}
      aria-label="{label} colour"
      class="w-8 h-8 shrink-0 rounded-md border border-surface-300-700 bg-transparent cursor-pointer p-0.5"
    />
    <div class="min-w-0 flex-1">
      <div class="text-xs font-medium text-surface-900-100">{label}</div>
      <div class="text-[10px] text-surface-500 truncate">{hint}</div>
    </div>
    <input
      type="text"
      value={value}
      oninput={onHexInput}
      spellcheck="false"
      aria-label="{label} hex value"
      aria-invalid={ramp ? undefined : 'true'}
      class="w-24 shrink-0 font-mono text-[11px] px-2 py-1.5 rounded-md bg-surface-50-950 border text-surface-900-100
        {ramp ? 'border-surface-300-700' : 'border-error-500'}"
    />
    <button
      type="button"
      onclick={() => (value = fallback)}
      disabled={isDefault}
      title="Reset to the FlowWeaver default"
      aria-label="Reset {label} to the FlowWeaver default"
      class="shrink-0 w-7 h-7 inline-flex items-center justify-center rounded-md text-surface-500 hover:text-surface-900-100 hover:bg-surface-200-800/60 disabled:opacity-30 disabled:pointer-events-none transition-colors cursor-pointer"
    >
      <RotateCcw size={12} />
    </button>
  </div>

  {#if ramp}
    {#if contrastWarning}
      <div class="ml-[2.625rem] flex items-start gap-1 text-[10px] text-warning-700-300">
        <AlertTriangle size={11} class="shrink-0 mt-px" />
        <span>{contrastWarning}</span>
      </div>
    {/if}
    <div class="flex rounded-md overflow-hidden ring-1 ring-surface-200-800 ml-[2.625rem]">
      {#each SHADES as shade (shade)}
        <div
          class="flex-1 h-6 flex items-center justify-center"
          style="background: {ramp[shade]}"
          title="{shade} · {ramp[shade]}"
        >
          {#if shade === anchor}
            <!-- Marks the slot the picked colour occupies verbatim. -->
            <span
              class="text-[8px] font-bold tabular-nums"
              style="color: {contrastEndFor(ramp[shade]) === 'dark' ? ramp[950] : ramp[50]}"
            >
              {shade}
            </span>
          {/if}
        </div>
      {/each}
    </div>
  {:else}
    <div class="ml-[2.625rem] text-[10px] text-error-500">
      Needs a 6-digit hex like <code>#5c69ab</code>.
    </div>
  {/if}
</div>
