<script lang="ts">
  // A miniature of the app painted with the theme being edited. The colours
  // are applied as inline custom properties on this wrapper, so the preview
  // is scoped to its own subtree — the page around it keeps whatever theme
  // the user is actually running, and nothing is injected globally until the
  // theme is saved.
  //
  // It deliberately shows the combinations that break first when a palette is
  // off: text on a surface, a filled button (contrast tokens), a badge row,
  // and table borders.
  import { themeStyleAttr, type ThemeColors, type ThemeSettings } from './customTheme';
  import { CheckCircle2, AlertTriangle, XCircle } from 'lucide-svelte';

  let {
    colors,
    settings,
    mode = 'dark',
  }: { colors: ThemeColors; settings?: ThemeSettings; mode?: 'light' | 'dark' } = $props();

  // The body font is asserted explicitly: the app sets it on <html> from
  // --font-sans, and this wrapper has to re-anchor it so the preview shows
  // the theme's stack instead of inheriting the page's. Radius overrides
  // need nothing extra — `rounded-*` utilities resolve to the --radius-*
  // custom properties this wrapper redefines.
  const style = $derived(
    `${themeStyleAttr(colors, settings)};font-family:var(--font-sans)`,
  );
</script>

<!-- data-mode on the wrapper flips the `surface-100-900` style pair utilities
     inside, so one preview can show either mode without touching the page. -->
<div
  data-mode={mode}
  data-theme-preview="true"
  style={style}
  class="rounded-lg overflow-hidden ring-1 ring-surface-300-700"
>
  <div class="bg-surface-50-950 p-4 space-y-3">
    <div class="flex items-center justify-between gap-3">
      <div>
        <!-- Styled through the heading tokens, so the heading font and
             weight knobs are visible here before saving. -->
        <div
          class="text-sm text-surface-900-100"
          style="font-family: var(--heading-font-family, inherit); font-weight: var(--heading-font-weight, 650)"
        >
          Reachability check
        </div>
        <div class="text-[11px] text-surface-500">12 devices · production</div>
      </div>
      <div class="flex items-center gap-1.5">
        <span class="inline-flex items-center gap-1 rounded-full bg-success-500/15 text-success-700-300 px-2 py-0.5 text-[10px] font-medium ring-1 ring-success-500/30">
          <CheckCircle2 size={10} /> 10 ok
        </span>
        <span class="inline-flex items-center gap-1 rounded-full bg-warning-500/15 text-warning-700-300 px-2 py-0.5 text-[10px] font-medium ring-1 ring-warning-500/30">
          <AlertTriangle size={10} /> 1 slow
        </span>
        <span class="inline-flex items-center gap-1 rounded-full bg-error-500/15 text-error-700-300 px-2 py-0.5 text-[10px] font-medium ring-1 ring-error-500/30">
          <XCircle size={10} /> 1 down
        </span>
      </div>
    </div>

    <div class="bg-surface-100-900 ring-1 ring-surface-200-800 rounded-md p-3 space-y-2">
      <p class="text-xs text-surface-700-300">
        Body text on a card, with an
        <a href="#preview" class="text-primary-600-300 underline underline-offset-2">inline link</a>
        and <code class="text-[11px] px-1 py-0.5 rounded bg-surface-200-800 text-tertiary-700-300">inline code</code>.
      </p>
      <table class="w-full text-[11px]">
        <thead>
          <tr class="text-surface-500 text-left">
            <th class="font-medium py-1 border-b border-surface-200-800">Device</th>
            <th class="font-medium py-1 border-b border-surface-200-800">Status</th>
          </tr>
        </thead>
        <tbody class="text-surface-700-300">
          <tr><td class="py-1 border-b border-surface-200-800">core-rtr-1</td><td class="py-1 border-b border-surface-200-800 text-success-600-400">reachable</td></tr>
          <tr><td class="py-1">edge-sw-4</td><td class="py-1 text-error-600-400">timeout</td></tr>
        </tbody>
      </table>
    </div>

    <div class="flex flex-wrap items-center gap-2">
      <!-- Filled buttons are the real contrast test: the label colour comes
           from the generated contrast tokens, not from a hardcoded white. -->
      <button type="button" class="rounded-md bg-primary-500 text-primary-contrast-500 px-3 py-1.5 text-xs font-medium">
        Run now
      </button>
      <button type="button" class="rounded-md bg-secondary-500 text-secondary-contrast-500 px-3 py-1.5 text-xs font-medium">
        Schedule
      </button>
      <button type="button" class="rounded-md bg-error-500 text-error-contrast-500 px-3 py-1.5 text-xs font-medium">
        Delete
      </button>
      <button type="button" class="rounded-md ring-1 ring-surface-300-700 text-surface-700-300 px-3 py-1.5 text-xs font-medium">
        Cancel
      </button>
    </div>
  </div>
</div>
