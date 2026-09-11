<script lang="ts">
  import { onMount } from 'svelte';
  import { animate, stagger, prefersReducedMotion } from '$lib/anim';

  let container = $state<HTMLSpanElement | null>(null);

  onMount(() => {
    if (!container || prefersReducedMotion()) return;
    const dots = container.querySelectorAll('.dot');
    const anim = animate(dots, {
      translateY: [
        { to: -3, duration: 280, ease: 'inOutQuad' },
        { to: 0, duration: 280, ease: 'inOutQuad' },
      ],
      opacity: [
        { to: 1, duration: 280 },
        { to: 0.55, duration: 280 },
      ],
      delay: stagger(120),
      loop: true,
    });
    return () => anim.pause();
  });
</script>

<span
  bind:this={container}
  class="inline-flex items-center gap-1 px-2 py-1 rounded-full bg-surface-200-800/60"
>
  <span class="dot w-1.5 h-1.5 rounded-full bg-surface-500"></span>
  <span class="dot w-1.5 h-1.5 rounded-full bg-surface-500"></span>
  <span class="dot w-1.5 h-1.5 rounded-full bg-surface-500"></span>
</span>
