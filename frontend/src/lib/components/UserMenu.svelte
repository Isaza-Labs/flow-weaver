<script lang="ts">
  import { tick } from 'svelte';
  import { authStore } from '$lib/stores/auth.svelte';
  import { Shield, LogOut, KeyRound } from 'lucide-svelte';
  import { clickOutside } from '$lib/utils/clickOutside';

  let open = $state(false);
  let triggerEl = $state<HTMLButtonElement | null>(null);
  let menuEl = $state<HTMLDivElement | null>(null);

  const session = $derived(authStore.session);
  const initial = $derived((session?.username ?? '?').charAt(0));
  const isAdmin = $derived(session?.role === 'admin');

  async function openMenu() {
    open = true;
    await tick();
    menuEl?.querySelector<HTMLElement>('[role="menuitem"]')?.focus();
  }
  function closeMenu(refocus = true) {
    open = false;
    if (refocus) triggerEl?.focus();
  }
  // Arrow-key roving over the menu items; Escape closes and returns focus.
  function onMenuKeydown(e: KeyboardEvent) {
    if (e.key === 'Escape') {
      e.preventDefault();
      closeMenu();
      return;
    }
    if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
    e.preventDefault();
    const items = Array.from(menuEl?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? []);
    if (items.length === 0) return;
    const idx = items.indexOf(document.activeElement as HTMLElement);
    const next =
      e.key === 'ArrowDown' ? (idx + 1) % items.length : (idx - 1 + items.length) % items.length;
    items[next].focus();
  }
</script>

<div class="relative" use:clickOutside={() => closeMenu(false)}>
  <button
    bind:this={triggerEl}
    type="button"
    onclick={() => (open ? closeMenu(false) : openMenu())}
    class="w-7 h-7 rounded-full bg-primary-500/15 text-primary-200 hover:bg-primary-500/25 hover:text-primary-100 flex items-center justify-center text-[11px] font-semibold shrink-0 uppercase transition-colors cursor-pointer"
    aria-label="Open user menu"
    aria-expanded={open}
    aria-haspopup="menu"
    title={session?.username ?? 'User menu'}
  >
    {initial}
  </button>

  {#if open && session}
    <div
      bind:this={menuEl}
      class="absolute bottom-full left-0 mb-2 w-56 bg-surface-100-900 border border-surface-300-700 rounded-lg shadow-2xl shadow-black/40 py-1 z-50 animate-in fade-in"
      role="menu"
      tabindex="-1"
      onkeydown={onMenuKeydown}
    >
      <!-- Header: identity summary. Not interactive. -->
      <div class="px-3 py-2 border-b border-surface-200-800/60">
        <div class="text-xs font-semibold text-surface-900-100 truncate leading-tight">
          {session.username}
        </div>
        <div class="text-[10px] text-surface-500 truncate leading-tight capitalize mt-0.5">
          {session.role}
        </div>
      </div>

      {#if isAdmin}
        <a
          href="/admin"
          role="menuitem"
          onclick={() => closeMenu(false)}
          class="w-full flex items-center gap-2 px-3 py-1.5 text-xs text-surface-700-300 hover:bg-surface-200-800/60 transition-colors"
        >
          <Shield size={12} class="text-primary-400" />
          <span>Admin dashboard</span>
        </a>
      {/if}

      <a
        href="/settings/password"
        role="menuitem"
        onclick={() => closeMenu(false)}
        class="w-full flex items-center gap-2 px-3 py-1.5 text-xs text-surface-700-300 hover:bg-surface-200-800/60 transition-colors"
      >
        <KeyRound size={12} />
        <span>Change password</span>
      </a>

      <div class="my-1 border-t border-surface-200-800/60"></div>

      <a
        href="/logout"
        role="menuitem"
        onclick={() => closeMenu(false)}
        class="w-full flex items-center gap-2 px-3 py-1.5 text-xs text-surface-700-300 hover:bg-surface-200-800/60 transition-colors"
      >
        <LogOut size={12} />
        <span>Sign out</span>
      </a>
    </div>
  {/if}
</div>
