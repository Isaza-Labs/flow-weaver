<script lang="ts">
  import { tick } from 'svelte';
  import { themeStore, type ThemeName } from '$lib/stores/theme.svelte';
  import { Sun, Moon, Palette, Check, Plus, Users } from 'lucide-svelte';
  import { clickOutside } from '$lib/utils/clickOutside';

  let showThemeMenu = $state(false);
  let triggerEl = $state<HTMLButtonElement | null>(null);
  let menuEl = $state<HTMLDivElement | null>(null);

  function pickTheme(theme: ThemeName) {
    themeStore.setTheme(theme);
    closeMenu();
  }

  async function openMenu() {
    showThemeMenu = true;
    await tick();
    menuEl?.querySelector<HTMLElement>('[role="menuitem"]')?.focus();
  }
  function closeMenu(refocus = true) {
    showThemeMenu = false;
    if (refocus) triggerEl?.focus();
  }
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

  const options = $derived(themeStore.available);
  const activeOption = $derived(options.find((t) => t.id === themeStore.theme) ?? options[0]);
</script>

<div class="flex items-center gap-1">
  <button
    type="button"
    onclick={() => themeStore.toggleMode()}
    class="inline-flex items-center justify-center w-8 h-8 rounded-md text-surface-600-400 hover:text-surface-900-100 hover:bg-surface-200-800/60 transition-colors cursor-pointer"
    aria-label={themeStore.mode === 'dark' ? 'Switch to light mode' : 'Switch to dark mode'}
    title={themeStore.mode === 'dark' ? 'Switch to light mode' : 'Switch to dark mode'}
  >
    {#if themeStore.mode === 'dark'}
      <Sun size={16} />
    {:else}
      <Moon size={16} />
    {/if}
  </button>

  <div class="relative" use:clickOutside={() => closeMenu(false)}>
    <button
      bind:this={triggerEl}
      type="button"
      onclick={() => (showThemeMenu ? closeMenu(false) : openMenu())}
      class="inline-flex items-center justify-center w-8 h-8 rounded-md text-surface-600-400 hover:text-surface-900-100 hover:bg-surface-200-800/60 transition-colors cursor-pointer"
      aria-label="Pick a theme"
      aria-haspopup="menu"
      aria-expanded={showThemeMenu}
      title="Theme — {activeOption.label}"
    >
      <Palette size={16} />
    </button>
    {#if showThemeMenu}
      <div
        bind:this={menuEl}
        class="absolute bottom-full left-0 mb-2 w-64 max-h-[70vh] overflow-y-auto bg-surface-100-900 border border-surface-300-700 rounded-lg shadow-2xl shadow-black/40 py-1 z-50 animate-in fade-in"
        role="menu"
        tabindex="-1"
        onkeydown={onMenuKeydown}
      >
        {#each options as option (option.id)}
          {@const active = themeStore.theme === option.id}
          {#if option.custom && !options[options.indexOf(option) - 1]?.custom}
            <!-- One divider where the saved themes start. -->
            <div class="mt-1 mb-1 mx-3 border-t border-surface-200-800"></div>
          {/if}
          <button
            type="button"
            role="menuitem"
            onclick={() => pickTheme(option.id)}
            class="w-full text-left px-3 py-2 transition-colors {active ? 'bg-primary-500/10' : 'hover:bg-surface-200-800/60'}"
          >
            <div class="flex items-center justify-between gap-2">
              <span class="text-xs font-semibold truncate {active ? 'text-primary-600-300' : 'text-surface-900-100'}">
                {option.label}
              </span>
              <span class="flex items-center gap-1 shrink-0">
                {#if option.custom?.isShared}
                  <!-- Distinguishes a theme the whole org sees from a private one. -->
                  <Users size={10} class="text-surface-500" />
                {/if}
                {#if active}<Check size={12} class="text-primary-600-300" />{/if}
              </span>
            </div>
            <div class="text-[10px] text-surface-500 mt-0.5 leading-snug line-clamp-2">
              {option.description}
            </div>
          </button>
        {/each}

        <div class="mt-1 pt-1 mx-3 border-t border-surface-200-800"></div>
        <a
          href="/themes"
          role="menuitem"
          onclick={() => closeMenu(false)}
          class="flex items-center gap-2 px-3 py-2 text-xs font-medium text-surface-700-300 hover:bg-surface-200-800/60 transition-colors"
        >
          <Plus size={12} />
          Create a theme
        </a>
      </div>
    {/if}
  </div>
</div>
