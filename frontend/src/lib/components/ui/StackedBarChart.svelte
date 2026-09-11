<script lang="ts">
  import { onMount } from 'svelte';
  import { animate, stagger, prefersReducedMotion } from '$lib/anim';
  // Lightweight stacked-bar chart in pure SVG. Built for the admin
  // dashboard's small-multiple panels — no axis ticks beyond min/mid/max,
  // no tooltip library, no external deps (saves ~150KB vs chart.js). If we
  // ever need interactive zoom or per-point rich tooltips, swap for
  // layercake.
  //
  // Each rect has a <title> child so the native browser tooltip surfaces
  // "{label} · {series}: {count}" — good enough for v1.

  interface Entry {
    label: string;
    counts: Record<string, number>;
  }

  interface Segment {
    key: string;
    value: number;
    x: number;
    y: number;
    width: number;
    height: number;
    label: string;
  }

  let {
    data,
    series,
    colorFor,
    labelFor,
    height = 180,
    ariaLabel = 'Stacked bar chart',
  }: {
    data: Entry[];
    series: string[];
    colorFor: (key: string) => string;
    labelFor?: (entry: Entry) => string;
    height?: number;
    ariaLabel?: string;
  } = $props();

  const width = 640;
  const padL = 32;
  const padR = 12;
  const padT = 12;
  const padB = 28;

  const innerW = $derived(width - padL - padR);
  const innerH = $derived(height - padT - padB);

  const totals = $derived(data.map((d) => series.reduce((sum, k) => sum + (d.counts[k] || 0), 0)));
  const maxTotal = $derived(Math.max(1, ...totals));

  // Tick marks on the Y axis: 0, mid, max. Enough to orient the eye in a
  // 180px panel without cluttering it. Deduped because when maxTotal is
  // small (1 or 2, which includes the clamped empty state), 0 / mid / max
  // collapse to repeated values and the keyed `{#each}` below would throw
  // `each_key_duplicate`.
  const yTicks = $derived(
    Array.from(new Set([0, Math.ceil(maxTotal / 2), maxTotal])),
  );

  // Keep bars readable for small N — wider gap when few buckets.
  const barGap = $derived(data.length > 14 ? 2 : 4);
  const barW = $derived(Math.max(4, (innerW - barGap * (data.length - 1)) / Math.max(1, data.length)));

  function yAt(v: number): number {
    return padT + innerH - (v / maxTotal) * innerH;
  }

  // Flatten the bucket grid into a segment list so the template can
  // render with a single {#each}. Segments are ordered bottom → top,
  // matching the draw order expected by the SVG painter.
  const segments = $derived.by<Segment[]>(() => {
    const out: Segment[] = [];
    for (let i = 0; i < data.length; i++) {
      const entry = data[i];
      const label = labelFor ? labelFor(entry) : entry.label;
      const xCoord = padL + i * (barW + barGap);
      let yOffset = padT + innerH;
      for (const key of series) {
        const v = entry.counts[key] || 0;
        if (v === 0) continue;
        const h = (v / maxTotal) * innerH;
        yOffset -= h;
        out.push({
          key,
          value: v,
          x: xCoord,
          y: yOffset,
          width: barW,
          height: h,
          label,
        });
      }
    }
    return out;
  });

  function segmentKey(s: Segment, i: number): string {
    return `${i}-${s.key}-${s.label}`;
  }

  let svgEl = $state<SVGSVGElement | null>(null);
  // Bars grow from the baseline (full height) up to their actual
  // height. We animate scaleY on each <rect>, anchored at its bottom
  // edge via transform-origin set on the same element. The visible
  // segments snapshot is captured so the animation runs once per
  // dataset change rather than on every segments recomputation.
  let lastSig = '';
  $effect(() => {
    const segs = segments;
    const sig = `${segs.length}|${maxTotal}`;
    if (sig === lastSig) return;
    lastSig = sig;
    if (!svgEl || prefersReducedMotion()) return;
    const rects = svgEl.querySelectorAll<SVGRectElement>('rect.bar-seg');
    if (rects.length === 0) return;
    animate(rects, {
      scaleY: [0, 1],
      duration: 520,
      ease: 'outCubic',
      delay: stagger(20),
    });
  });
</script>

<svg
  bind:this={svgEl}
  viewBox="0 0 {width} {height}"
  role="img"
  aria-label={ariaLabel}
  preserveAspectRatio="none"
  class="w-full h-full"
>
  <!-- Y-axis grid + tick labels -->
  {#each yTicks as tick (tick)}
    <line x1={padL} x2={width - padR} y1={yAt(tick)} y2={yAt(tick)}
          stroke="currentColor" class="text-surface-300-700" stroke-width="0.5" stroke-dasharray="2 3" />
    <text x={padL - 6} y={yAt(tick) + 3} text-anchor="end" class="fill-surface-500" style="font-size: 10px;">
      {tick}
    </text>
  {/each}

  <!-- Stacked bar segments -->
  {#each segments as seg, i (segmentKey(seg, i))}
    <rect
      class="bar-seg"
      x={seg.x}
      y={seg.y}
      width={seg.width}
      height={seg.height}
      fill={colorFor(seg.key)}
      opacity="0.85"
      style="transform-origin: {seg.x + seg.width / 2}px {seg.y + seg.height}px; transform-box: view-box;"
    >
      <title>{seg.label} · {seg.key}: {seg.value}</title>
    </rect>
  {/each}

  <!-- x-axis labels, one per bucket -->
  {#each data as entry, i (i)}
    <text
      x={padL + i * (barW + barGap) + barW / 2}
      y={height - 10}
      text-anchor="middle"
      class="fill-surface-500"
      style="font-size: 10px;"
    >
      {labelFor ? labelFor(entry) : entry.label}
    </text>
  {/each}
</svg>
