<script lang="ts">
  import { Handle, Position } from '@xyflow/svelte';
  import { onDestroy } from 'svelte';
  import { animate, prefersReducedMotion } from '$lib/anim';
  import { ShieldAlert, AlertTriangle } from 'lucide-svelte';

  let { data } = $props();

  // Shared between /workflows (editor) and /runs/[id]/monitor. Both
  // views want nodeId + serviceName + serviceType stacked inside the
  // box; the monitor additionally wraps the node in a status-colored
  // frame via `node.style` which SvelteFlow applies outside this
  // component. Using `currentColor` + opacity lets the text inherit
  // that wrapper color (green on success, red on failed, etc.) without
  // this component knowing about status.
  //
  // `data.variant === 'editor'` → render our own themed box (bg + border
  // from CSS vars that swap per data-mode). The default xyflow wrapper is
  // neutralized in app.css so we don't see a white/gray frame around our
  // box in either mode. Monitor omits the variant flag so the outer
  // wrapper continues to carry the status color.
  const nodeId = $derived<string>(data.nodeId ?? data.label ?? 'Node');
  const serviceName = $derived<string>(data.serviceName ?? '');
  const serviceType = $derived<string>(data.serviceType ?? '');
  const showServiceRow = $derived(
    serviceType !== 'start' && serviceType !== 'end' && (serviceName || serviceType),
  );
  const isEditor = $derived(data.variant === 'editor');
  const status = $derived<string>(data.status ?? '');

  // S13.6: effective rollback policy of this node's snippet. Surfaced
  // only in the editor — the monitor view already uses node colour
  // for runtime status and a second indicator would compete visually.
  // Sentinels pass through (no badge). `compensated` is a special
  // marker the parent sets when a `requires_compensation` node already
  // has a failure edge feeding a revert step; we treat it as quiet.
  const idempotency = $derived<string>(data.idempotency ?? '');
  const showRollbackHint = $derived(
    isEditor
      && (idempotency === 'non_reversible' || idempotency === 'requires_compensation')
      && data.serviceType !== 'start'
      && data.serviceType !== 'end',
  );

  // Pulsing halo for running steps in the monitor view. The wrapper
  // applies a static blue ring; this layer animates an additional
  // glow so the eye locks onto whatever is in flight without us
  // animating layout.
  let pulseEl = $state<HTMLDivElement | null>(null);
  let pulseAnim: ReturnType<typeof animate> | null = null;
  $effect(() => {
    if (!pulseEl) return;
    if (pulseAnim) { pulseAnim.pause(); pulseAnim = null; }
    if (status !== 'running' || prefersReducedMotion()) {
      pulseEl.style.opacity = '0';
      return;
    }
    pulseEl.style.opacity = '1';
    pulseAnim = animate(pulseEl, {
      scale: [1, 1.08],
      opacity: [0.55, 0],
      duration: 1400,
      ease: 'outQuad',
      loop: true,
    });
  });
  onDestroy(() => { if (pulseAnim) pulseAnim.pause(); });

  // Theme-reactive box styles used when the consumer is the editor. The
  // vars come from app.css; we pass them via `style` instead of Tailwind
  // classes so we don't fight Skeleton's dual-mode utility compilation.
  const editorBoxStyle =
    'background: var(--fw-node-bg); border: 1px solid var(--fw-node-border); color: var(--fw-node-text); border-radius: 8px;';
</script>

<!-- Four handles — one per side. Each is both source and target via two
     stacked <Handle> elements with unique ids. Combined with the parent
     SvelteFlow running in `ConnectionMode.Loose`, the user can drag a
     connection from any side of a node to any side of another. The handle
     dots stay invisible until hover (see CSS below) so the box doesn't
     look studded with dots. -->
<Handle id="t-top" type="target" position={Position.Top} />
<Handle id="s-top" type="source" position={Position.Top} />
<Handle id="t-right" type="target" position={Position.Right} />
<Handle id="s-right" type="source" position={Position.Right} />
<Handle id="t-bottom" type="target" position={Position.Bottom} />
<Handle id="s-bottom" type="source" position={Position.Bottom} />
<Handle id="t-left" type="target" position={Position.Left} />
<Handle id="s-left" type="source" position={Position.Left} />

<div
  class="relative px-3 py-2 w-full overflow-visible"
  style={isEditor ? editorBoxStyle : ''}
>
  <div
    bind:this={pulseEl}
    aria-hidden="true"
    class="pointer-events-none absolute -inset-1 rounded-[10px] opacity-0"
    style="box-shadow: 0 0 0 4px rgb(59 130 246 / 0.45);"
  ></div>
  <div class="text-xs font-semibold leading-tight truncate pr-5">
    {nodeId}
  </div>

  {#if showRollbackHint}
    <!-- Top-right corner badge. Red ShieldAlert = non-reversible step
         that will block rollback; yellow AlertTriangle = requires a
         compensation edge to roll back cleanly. The tooltip points
         authors at the dialog where they can override or wire a
         failure edge. -->
    <div
      class="absolute top-1.5 right-1.5 leading-none pointer-events-auto"
      title={idempotency === 'non_reversible'
        ? 'Non-reversible: rollback will be blocked. Click the node to override the policy or design a forward-fix path.'
        : 'Requires compensation: wire a failure edge to a revert step so rollback can succeed.'}
    >
      {#if idempotency === 'non_reversible'}
        <ShieldAlert size={12} class="text-error-400" />
      {:else}
        <AlertTriangle size={12} class="text-warning-400" />
      {/if}
    </div>
  {/if}

  {#if showServiceRow}
    <div class="mt-1 pt-1 border-t border-current/20 space-y-0.5">
      {#if serviceName}
        <div class="text-[11px] opacity-90 leading-tight truncate">
          {serviceName}
        </div>
      {/if}
      {#if serviceType}
        <div class="text-[10px] opacity-70 leading-tight font-mono truncate">
          {serviceType}
        </div>
      {/if}
    </div>
  {/if}

  {#if data.subtitle}
    <div class="text-[10px] opacity-60 mt-1 truncate leading-tight">
      {data.subtitle}
    </div>
  {/if}
</div>
