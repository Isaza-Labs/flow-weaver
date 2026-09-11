<script lang="ts">
  import { page } from '$app/state';
  import { onMount } from 'svelte';
  import { guideStore } from '$lib/stores/guide.svelte';
  import { findGuideForRoute, type GuideEntry } from '$lib/guides';
  import { streamChat } from '$lib/api/ai-stream';
  import { renderMarkdownSafe } from '$lib/markdown';
  import { animate, prefersReducedMotion } from '$lib/anim';

  let entry = $state<GuideEntry>(findGuideForRoute('/'));
  let currentPath = $state('');
  let questionText = $state('');
  let askingAI = $state(false);
  let aiAnswer = $state('');
  let aiError = $state('');
  let aiErrorCode = $state('');
  let askExpanded = $state(false);
  let containerEl = $state<HTMLDivElement | null>(null);
  let hasCustomPosition = $state(false);
  let posX = $state(0);
  let posY = $state(0);
  let dragging = $state(false);
  let dragMoved = $state(false);
  let dragOffsetX = 0;
  let dragOffsetY = 0;
  let dragPointerId: number | null = null;
  let mascotSvgEl = $state<SVGSVGElement | null>(null);
  let mascotEyesEl = $state<SVGGElement | null>(null);
  let mascotAntennaEl = $state<SVGCircleElement | null>(null);
  let panelEl = $state<HTMLDivElement | null>(null);
  let idleAnims: Array<ReturnType<typeof animate>> = [];

  // Markdown helper now lives in $lib/markdown — shared with the chat
  // bubble renderer so both surfaces escape and sanitize identically.
  const renderMarkdown = renderMarkdownSafe;

  function resetAskState() {
    aiAnswer = '';
    aiError = '';
    aiErrorCode = '';
    askExpanded = false;
    questionText = '';
  }

  // Re-resolve the guide entry whenever the route changes.
  $effect(() => {
    const pathname = page?.url?.pathname ?? '/';
    if (pathname !== currentPath) {
      currentPath = pathname;
      entry = findGuideForRoute(pathname);
      // New page → clear any prior AI answer so it doesn't bleed across.
      resetAskState();
    }
  });

  function dismiss() {
    guideStore.setEnabled(false);
  }

  function togglePanel() {
    guideStore.togglePanel();
  }

  function clampToViewport(x: number, y: number) {
    if (typeof window === 'undefined' || !containerEl) {
      return { x, y };
    }

    const rect = containerEl.getBoundingClientRect();
    const maxX = Math.max(16, window.innerWidth - rect.width - 16);
    const maxY = Math.max(16, window.innerHeight - rect.height - 16);

    return {
      x: Math.min(Math.max(16, x), maxX),
      y: Math.min(Math.max(16, y), maxY),
    };
  }

  function syncCustomPosition() {
    if (!hasCustomPosition || !containerEl) return;
    const clamped = clampToViewport(posX, posY);
    posX = clamped.x;
    posY = clamped.y;
  }

  function startDrag(e: PointerEvent) {
    if (!containerEl) return;

    const rect = containerEl.getBoundingClientRect();
    hasCustomPosition = true;
    posX = rect.left;
    posY = rect.top;
    dragOffsetX = e.clientX - rect.left;
    dragOffsetY = e.clientY - rect.top;
    dragging = true;
    dragMoved = false;
    dragPointerId = e.pointerId;
  }

  function handleMascotClick(event: MouseEvent) {
    if (dragMoved) {
      event.preventDefault();
      dragMoved = false;
      return;
    }
    togglePanel();
  }

  function startIdleAnimations() {
    if (prefersReducedMotion()) return;
    if (mascotSvgEl) {
      idleAnims.push(
        animate(mascotSvgEl, {
          translateY: [0, -2, 0],
          duration: 3500,
          ease: 'inOutSine',
          loop: true,
        }),
      );
    }
    if (mascotEyesEl) {
      idleAnims.push(
        animate(mascotEyesEl, {
          scaleY: [
            { to: 1, duration: 4400 },
            { to: 0.1, duration: 90 },
            { to: 1, duration: 180 },
          ],
          ease: 'inOutQuad',
          loop: true,
        }),
      );
    }
    if (mascotAntennaEl) {
      idleAnims.push(
        animate(mascotAntennaEl, {
          opacity: [0.7, 1, 0.7],
          duration: 2100,
          ease: 'inOutSine',
          loop: true,
        }),
      );
    }
  }

  function stopIdleAnimations() {
    for (const a of idleAnims) a.pause();
    idleAnims = [];
  }

  // Entrance pop the first time the mascot mounts. Cheap delight that
  // signals "I'm here to help" instead of just appearing in the corner.
  $effect(() => {
    if (!guideStore.enabled) {
      stopIdleAnimations();
      return;
    }
    if (!mascotSvgEl) return;
    if (!prefersReducedMotion()) {
      animate(mascotSvgEl.parentElement as HTMLElement, {
        scale: [0.4, 1.1, 1],
        opacity: [0, 1],
        duration: 520,
        ease: 'outBack',
      });
    }
    queueMicrotask(startIdleAnimations);
  });

  // Speech-bubble panel open: scale + fade in from the bottom-right
  // corner so it visually unfolds from the mascot.
  $effect(() => {
    if (!guideStore.panelOpen || !panelEl) return;
    if (prefersReducedMotion()) return;
    animate(panelEl, {
      opacity: [0, 1],
      scale: [0.92, 1],
      translateY: [6, 0],
      duration: 220,
      ease: 'outCubic',
    });
  });

  onMount(() => {
    const handlePointerMove = (e: PointerEvent) => {
      if (!dragging || dragPointerId !== e.pointerId) return;
      const next = clampToViewport(e.clientX - dragOffsetX, e.clientY - dragOffsetY);
      if (Math.abs(next.x - posX) > 2 || Math.abs(next.y - posY) > 2) {
        dragMoved = true;
      }
      posX = next.x;
      posY = next.y;
    };

    const stopDragging = (e?: PointerEvent) => {
      if (e && dragPointerId !== null && e.pointerId !== dragPointerId) return;
      dragging = false;
      dragPointerId = null;
      window.setTimeout(() => {
        dragMoved = false;
      }, 0);
    };

    const handleResize = () => {
      syncCustomPosition();
    };

    window.addEventListener('pointermove', handlePointerMove);
    window.addEventListener('pointerup', stopDragging);
    window.addEventListener('pointercancel', stopDragging);
    window.addEventListener('resize', handleResize);

    return () => {
      window.removeEventListener('pointermove', handlePointerMove);
      window.removeEventListener('pointerup', stopDragging);
      window.removeEventListener('pointercancel', stopDragging);
      window.removeEventListener('resize', handleResize);
    };
  });

  $effect(() => {
    guideStore.panelOpen;
    if (hasCustomPosition) {
      queueMicrotask(() => {
        syncCustomPosition();
      });
    }
  });

  async function askGuide() {
    if (!questionText.trim()) return;
    askingAI = true;
    aiError = '';
    aiErrorCode = '';
    aiAnswer = '';
    try {
      const messageWithContext =
        `[GUIDE CONTEXT — the user is asking from inside the FlowWeaver UI. Their current page is ${page.url.pathname}. ${entry.aiContext} Be concise (2-4 sentences), point to specific buttons or sections by name, and prefer "click X" / "go to Y" instructions over abstract explanations.]\n\nUser question: ${questionText.trim()}`;

      // Streaming endpoint — same agent + tool pipeline the full chat
      // uses. We accumulate the text deltas into aiAnswer and ignore the
      // tool-call events so the guide stays a single text bubble.
      for await (const evt of streamChat({ message: messageWithContext })) {
        if (evt.type === 'text') aiAnswer += evt.content;
        else if (evt.type === 'error') { aiError = evt.message; aiErrorCode = evt.code ?? ''; }
        else if (evt.type === 'timeout') aiAnswer += '\n\n(stream timed out at 25s)';
      }
      if (!aiAnswer && !aiError) aiAnswer = '(no response)';
    } catch (e: any) {
      aiError = e?.message ?? 'Failed to ask the guide';
    } finally {
      askingAI = false;
    }
  }

  function handleAskKeydown(e: KeyboardEvent) {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      askGuide();
    }
  }
</script>

{#if guideStore.enabled}
  <!-- Container holds ONLY the mascot — the panel is absolutely positioned
       relative to it so opening/closing the panel never shifts the mascot. -->
  <div
    bind:this={containerEl}
    class="fixed z-40 w-16 h-16"
    class:bottom-4={!hasCustomPosition}
    class:right-4={!hasCustomPosition}
    style={hasCustomPosition ? `left: ${posX}px; top: ${posY}px;` : ''}
  >
    <!-- Speech-bubble panel (anchored above the mascot, doesn't affect layout) -->
    {#if guideStore.panelOpen}
      <div
        bind:this={panelEl}
        class="absolute bottom-full right-0 mb-2 card border border-surface-300-700 shadow-2xl w-[22rem] max-h-[70vh] flex flex-col origin-bottom-right bg-surface-100-900"
        role="dialog"
        aria-label="FlowWeaver guide"
      >
        <!-- Header -->
        <header
          class="px-4 py-3 border-b border-surface-200-800 flex items-start justify-between gap-2"
        >
          <button
            type="button"
            class="min-w-0 flex-1 text-left cursor-grab active:cursor-grabbing select-none"
            onpointerdown={startDrag}
            aria-label="Drag guide"
            title="Drag guide"
          >
            <div class="text-[10px] uppercase tracking-wider opacity-60">FlowWeaver guide</div>
            <h3 class="h6 truncate">{entry.title}</h3>
          </button>
          <button
            type="button"
            onclick={() => guideStore.closePanel()}
            class="btn-icon btn-icon-sm preset-tonal shrink-0"
            aria-label="Close guide"
          >
            ✕
          </button>
        </header>

        <!-- Body (scrollable) -->
        <div class="overflow-y-auto flex-1 px-4 py-3 space-y-3">
          <p class="text-sm leading-relaxed">{entry.intro}</p>

          {#if entry.tips.length > 0}
            <div>
              <div class="text-[10px] uppercase tracking-wider opacity-60 mb-1.5">What you can do here</div>
              <ul class="space-y-1.5">
                {#each entry.tips as tip}
                  <li class="text-xs flex items-start gap-2">
                    <span class="text-primary-300 shrink-0 leading-tight">▸</span>
                    <span class="opacity-90">{tip}</span>
                  </li>
                {/each}
              </ul>
            </div>
          {/if}

          <!-- Ask the guide (collapsible) -->
          <div class="border-t border-surface-200-800 pt-3">
            <button
              type="button"
              onclick={() => (askExpanded = !askExpanded)}
              class="w-full text-left text-xs uppercase tracking-wider opacity-60 hover:opacity-100 transition-opacity flex items-center justify-between"
            >
              <span>Ask the guide (AI)</span>
              <span class="text-primary-300">{askExpanded ? '▾' : '▸'}</span>
            </button>

            {#if askExpanded}
              <div class="mt-2 space-y-2">
                <textarea
                  bind:value={questionText}
                  placeholder="How do I…?  (Cmd/Ctrl+Enter to send)"
                  rows="2"
                  onkeydown={handleAskKeydown}
                  class="input text-xs"
                ></textarea>
                <div class="flex items-center justify-between gap-2">
                  <span class="text-[10px] opacity-50">
                    Quick Q&A. For multi-turn debugging use the full chat.
                  </span>
                  <button
                    type="button"
                    onclick={askGuide}
                    disabled={askingAI || !questionText.trim()}
                    class="btn btn-sm preset-filled-primary-500"
                  >
                    {askingAI ? 'Asking…' : 'Ask'}
                  </button>
                </div>

                {#if aiError}
                  <aside class="card preset-tonal-error p-2 text-xs space-y-1">
                    {#if aiErrorCode === 'no_provider'}
                      <div class="font-medium">No AI provider is configured.</div>
                      <div class="opacity-80">
                        Add one under <a href="/ai/providers" class="underline">Settings &rsaquo; AI Providers</a>.
                      </div>
                    {:else if aiErrorCode === 'no_agent'}
                      <div class="font-medium">No AI agent is configured.</div>
                      <div class="opacity-80">
                        Create an enabled assistant agent under <a href="/ai/agents" class="underline">Settings &rsaquo; AI Agents</a>.
                      </div>
                    {:else}
                      {aiError}
                    {/if}
                  </aside>
                {/if}

                {#if aiAnswer}
                  <div class="card preset-tonal p-3 text-xs leading-relaxed guide-answer">{@html renderMarkdown(aiAnswer)}</div>
                {/if}

                <!-- Escape hatch: when the user's question grows past a
                     quick-hit the full /ai/chat page carries route context
                     and persists the conversation for later follow-up. -->
                <a
                  href={'/ai/chat?context=' + encodeURIComponent(currentPath) + (questionText.trim() ? '&q=' + encodeURIComponent(questionText.trim()) : '')}
                  class="block text-center text-[11px] text-primary-300 hover:underline pt-1"
                >
                  Open full chat →
                </a>
              </div>
            {/if}
          </div>
        </div>

        <!-- Footer with dismiss -->
        <footer class="px-4 py-2 border-t border-surface-200-800 flex items-center justify-between text-[10px] opacity-60">
          <span>Help on every page · click the mascot</span>
          <button type="button" onclick={dismiss} class="hover:underline">Hide guide</button>
        </footer>

        <!-- Speech bubble tail pointing down to the mascot -->
        <div
          class="absolute -bottom-2 right-9 w-4 h-4 rotate-45 border-r border-b border-surface-300-700 bg-surface-100-900"
          aria-hidden="true"
        ></div>
      </div>
    {/if}

    <!-- Mascot button -->
    <button
      type="button"
      onclick={handleMascotClick}
      onpointerdown={startDrag}
      class="mascot-btn relative w-16 h-16 rounded-full preset-filled-primary-500 shadow-2xl border-2 border-surface-300-700 hover:scale-110 transition-transform focus:outline-none focus:ring-4 focus:ring-primary-500/40"
      aria-label={guideStore.panelOpen ? 'Close guide' : 'Open guide'}
      title={guideStore.panelOpen ? 'Close guide' : 'Open guide'}
    >
      <!-- SVG mascot — a friendly hexagonal-headed character that nods to
           the FlowWeaver hex/geometric brand. Idle: gentle bob + blink.
           Hover/active: handled by parent .mascot-btn class. -->
      <svg
        bind:this={mascotSvgEl}
        viewBox="0 0 64 64"
        xmlns="http://www.w3.org/2000/svg"
        class="w-full h-full pointer-events-none mascot-svg"
        aria-hidden="true"
      >
        <!-- Antenna -->
        <line x1="32" y1="6" x2="32" y2="14" stroke="currentColor" stroke-width="2" stroke-linecap="round" />
        <circle bind:this={mascotAntennaEl} cx="32" cy="5" r="2.5" fill="currentColor" />

        <!-- Hex head outline (matches the FlowWeaver ⬡ icon) -->
        <polygon
          points="32,12 50,22 50,42 32,52 14,42 14,22"
          fill="rgba(255,255,255,0.18)"
          stroke="currentColor"
          stroke-width="2"
          stroke-linejoin="round"
        />

        <!-- Eyes (white background + black pupils that "blink" via JS) -->
        <g bind:this={mascotEyesEl} style="transform-origin: center; transform-box: fill-box;">
          <ellipse cx="25" cy="30" rx="4" ry="5" fill="white" />
          <ellipse cx="39" cy="30" rx="4" ry="5" fill="white" />
          <circle cx="25" cy="31" r="2" fill="#0a0a0a" />
          <circle cx="39" cy="31" r="2" fill="#0a0a0a" />
        </g>

        <!-- Mouth — friendly little arc -->
        <path
          d="M 26 40 Q 32 44 38 40"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
        />
      </svg>
    </button>

    <!-- Tiny × dismiss in the top-right corner. A sibling of the mascot
         button (never nest an interactive element inside another); it's
         positioned against the fixed container, which the mascot fills. -->
    <button
      type="button"
      onclick={(e) => { e.stopPropagation(); dismiss(); }}
      class="absolute -top-1 -right-1 z-10 w-5 h-5 rounded-full bg-surface-50-950 text-surface-950-50 text-[10px] font-bold flex items-center justify-center shadow border border-surface-300-700 opacity-70 hover:opacity-100 cursor-pointer"
      aria-label="Dismiss guide permanently"
      title="Dismiss permanently"
    >×</button>
  </div>
{/if}

<style>
  /* Idle bob, blink, and antenna pulse are driven by anime.js in the
     script block. We keep this style block for the markdown answer
     formatting only. */

  :global(.guide-answer p:first-child) {
    margin-top: 0;
  }

  :global(.guide-answer p:last-child) {
    margin-bottom: 0;
  }

  :global(.guide-answer p),
  :global(.guide-answer ul),
  :global(.guide-answer ol),
  :global(.guide-answer pre),
  :global(.guide-answer blockquote) {
    margin: 0 0 0.75rem 0;
  }

  :global(.guide-answer ul),
  :global(.guide-answer ol) {
    padding-left: 1.1rem;
  }

  :global(.guide-answer li + li) {
    margin-top: 0.25rem;
  }

  :global(.guide-answer code) {
    background: rgba(255, 255, 255, 0.08);
    border-radius: 0.25rem;
    padding: 0.08rem 0.3rem;
    font-size: 0.95em;
  }

  :global(.guide-answer pre) {
    overflow-x: auto;
    border-radius: 0.5rem;
    padding: 0.75rem;
    background: rgba(0, 0, 0, 0.25);
  }

  :global(.guide-answer pre code) {
    background: transparent;
    padding: 0;
  }
</style>
