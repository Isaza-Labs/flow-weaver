<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { authStore } from '$lib/stores/auth.svelte';
  import { themeStore } from '$lib/stores/theme.svelte';
  import { prefsStore } from '$lib/stores/prefs.svelte';
  import { registerShortcut } from '$lib/utils/shortcuts';
  import {
    LayoutDashboard, Workflow, Activity, Server, Bot, KeyRound,
    CalendarClock, FlaskConical, Cable, BookOpen, Settings2, Network,
    ShieldCheck, Sun, Moon, Layers, Search, Files,
  } from 'lucide-svelte';
  import type { IconComponent } from '$lib/components/ui';

  type Command = {
    id: string;
    label: string;
    keywords?: string;
    icon?: IconComponent;
    section: 'Navigate' | 'Actions' | 'Appearance' | 'Admin';
    adminOnly?: boolean;
    run: () => void;
  };

  let open = $state(false);
  let query = $state('');
  let cursor = $state(0);
  let inputEl = $state<HTMLInputElement | null>(null);
  // Element focused before the palette opened, restored on close.
  let prevFocus: HTMLElement | null = null;

  function show() {
    prevFocus = (document.activeElement as HTMLElement) ?? null;
    open = true;
    query = '';
    cursor = 0;
    queueMicrotask(() => inputEl?.focus());
  }
  function hide() {
    open = false;
    // Return focus to wherever the user was, unless a command moved it.
    prevFocus?.focus?.();
    prevFocus = null;
  }

  // Single source of truth — kept here so we don't have to wire the same
  // route lists in two places (Sidebar + palette).
  const commands = $derived.by<Command[]>(() => {
    const list: Command[] = [
      { id: 'go-dashboard', label: 'Go to Dashboard', section: 'Navigate', icon: LayoutDashboard, run: () => goto('/') },
      { id: 'go-workflows', label: 'Go to Workflows', section: 'Navigate', icon: Workflow, run: () => goto('/workflows') },
      { id: 'go-subflows', label: 'Go to Subflows', section: 'Navigate', icon: Layers, run: () => goto('/subflows') },
      { id: 'go-snippets', label: 'Go to Snippets', section: 'Navigate', icon: Settings2, run: () => goto('/snippets') },
      { id: 'go-integrations', label: 'Go to Integrations', section: 'Navigate', icon: Cable, run: () => goto('/integrations') },
      { id: 'go-runs', label: 'Go to Runs', section: 'Navigate', icon: Activity, run: () => goto('/runs') },
      { id: 'go-schedules', label: 'Go to Schedules', section: 'Navigate', icon: CalendarClock, run: () => goto('/schedules') },
      { id: 'go-devices', label: 'Go to Devices', section: 'Navigate', icon: Server, run: () => goto('/devices') },
      { id: 'go-pools', label: 'Go to Device pools', section: 'Navigate', icon: Network, run: () => goto('/device-pools') },
      { id: 'go-credentials', label: 'Go to Credentials', section: 'Navigate', icon: KeyRound, run: () => goto('/credentials') },
      { id: 'go-qa', label: 'Go to QA lab', section: 'Navigate', icon: FlaskConical, run: () => goto('/qa') },
      { id: 'go-ai', label: 'Go to AI', section: 'Navigate', icon: Bot, run: () => goto('/ai') },
      { id: 'go-docs', label: 'Go to Documentation', section: 'Navigate', icon: BookOpen, run: () => goto('/docs') },
      { id: 'new-workflow', label: 'New workflow', section: 'Actions', icon: Workflow, run: () => goto('/workflows?new=1') },
      { id: 'theme-toggle', label: themeStore.mode === 'dark' ? 'Switch to light mode' : 'Switch to dark mode', section: 'Appearance', icon: themeStore.mode === 'dark' ? Sun : Moon, run: () => themeStore.toggleMode() },
      { id: 'sidebar-collapse', label: prefsStore.sidebarCollapsed ? 'Expand sidebar' : 'Collapse sidebar', section: 'Appearance', run: () => prefsStore.toggleSidebar() },
      { id: 'density-toggle', label: prefsStore.density === 'compact' ? 'Comfortable table density' : 'Compact table density', section: 'Appearance', run: () => prefsStore.toggleDensity() },
      { id: 'admin-dashboard', label: 'Admin dashboard', section: 'Admin', adminOnly: true, icon: ShieldCheck, run: () => goto('/admin') },
      { id: 'admin-artifacts', label: 'Artifacts', keywords: 'reports exports documents', section: 'Admin', adminOnly: true, icon: Files, run: () => goto('/admin/artifacts') },
      { id: 'admin-policies', label: 'Policies', section: 'Admin', adminOnly: true, icon: ShieldCheck, run: () => goto('/policies') },
    ];
    const isAdmin = authStore.session?.role === 'admin';
    return list.filter((c) => !c.adminOnly || isAdmin);
  });

  const filtered = $derived.by(() => {
    const q = query.trim().toLowerCase();
    if (!q) return commands;
    return commands.filter((c) =>
      c.label.toLowerCase().includes(q) || (c.keywords ?? '').toLowerCase().includes(q),
    );
  });

  // Keep cursor in range whenever the filtered list shrinks.
  $effect(() => {
    if (cursor >= filtered.length) cursor = Math.max(0, filtered.length - 1);
  });

  const activeId = $derived(filtered[cursor]?.id);

  // Keep the highlighted row in view as the cursor moves with the keyboard.
  $effect(() => {
    if (!open || !activeId) return;
    queueMicrotask(() =>
      document.getElementById(`palette-opt-${activeId}`)?.scrollIntoView({ block: 'nearest' }),
    );
  });

  function runAt(index: number) {
    const cmd = filtered[index];
    if (!cmd) return;
    // Closing restores focus to prevFocus; null it first so a navigating
    // command isn't yanked back to the old element.
    prevFocus = null;
    open = false;
    queueMicrotask(() => cmd.run());
  }

  function handleKey(e: KeyboardEvent) {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      cursor = Math.min(cursor + 1, filtered.length - 1);
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      cursor = Math.max(cursor - 1, 0);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      runAt(cursor);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      hide();
    } else if (e.key === 'Tab') {
      // Single-input modal — keep focus on the search field.
      e.preventDefault();
    }
  }

  onMount(() => {
    const offCmdK = registerShortcut({
      id: 'palette-open-cmdk',
      label: 'Open command palette',
      key: 'k',
      ctrlOrMeta: true,
      allowInInput: true,
      handler: () => (open ? hide() : show()),
    });
    const offSlash = registerShortcut({
      id: 'palette-open-slash',
      label: 'Quick search',
      key: '/',
      handler: () => show(),
    });
    return () => {
      offCmdK();
      offSlash();
    };
  });
</script>

{#if open}
  <div
    class="fixed inset-0 z-[70] flex items-start justify-center pt-[15vh] px-4 bg-surface-950/70 backdrop-blur-sm"
    role="presentation"
    onclick={(e) => { if (e.target === e.currentTarget) hide(); }}
  >
    <div
      class="w-full max-w-lg bg-surface-100-900 border border-surface-300-700 rounded-xl shadow-2xl overflow-hidden"
      role="dialog"
      aria-modal="true"
      aria-label="Command palette"
    >
      <div class="flex items-center gap-2 px-3 h-11 border-b border-surface-200-800/60">
        <Search size={14} class="text-surface-500" />
        <input
          bind:this={inputEl}
          bind:value={query}
          onkeydown={handleKey}
          placeholder="Type a command or search…"
          class="flex-1 bg-transparent outline-none text-sm placeholder:text-surface-500"
          aria-label="Command search"
          role="combobox"
          aria-expanded="true"
          aria-controls="palette-listbox"
          aria-activedescendant={activeId ? `palette-opt-${activeId}` : undefined}
        />
        <span class="text-[10px] font-mono text-surface-500 px-1.5 py-0.5 rounded bg-surface-200-800/50">ESC</span>
      </div>
      <div id="palette-listbox" role="listbox" aria-label="Commands" class="max-h-[50vh] overflow-y-auto py-1.5">
        {#if filtered.length === 0}
          <div class="px-4 py-8 text-center text-sm text-surface-500">No commands match.</div>
        {:else}
          {#each filtered as cmd, i (cmd.id)}
            {@const sectionChanged = i === 0 || filtered[i - 1].section !== cmd.section}
            {#if sectionChanged}
              <div class="px-3 pt-2 pb-1 text-[10px] font-semibold uppercase tracking-wider text-surface-500">{cmd.section}</div>
            {/if}
            <button
              type="button"
              id="palette-opt-{cmd.id}"
              role="option"
              aria-selected={cursor === i}
              tabindex="-1"
              onmousemove={() => (cursor = i)}
              onclick={() => runAt(i)}
              class="w-full flex items-center gap-2.5 px-3 h-8 text-sm text-left cursor-pointer
                {cursor === i ? 'bg-primary-500/15 text-primary-100' : 'text-surface-700-300 hover:bg-surface-200-800/40'}"
            >
              {#if cmd.icon}
                <cmd.icon size={14} class={cursor === i ? 'text-primary-300' : 'text-surface-500'} />
              {/if}
              <span class="flex-1 truncate">{cmd.label}</span>
            </button>
          {/each}
        {/if}
      </div>
      <div class="flex items-center justify-between gap-3 px-3 py-1.5 text-[10px] text-surface-500 border-t border-surface-200-800/60 bg-surface-200-800/20">
        <span>↑↓ navigate · ↵ run</span>
        <span>⌘K / Ctrl+K to toggle</span>
      </div>
    </div>
  </div>
{/if}
