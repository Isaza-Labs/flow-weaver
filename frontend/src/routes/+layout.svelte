<script lang="ts">
  import '../app.css';
  import type { Snippet } from 'svelte';
  import { onMount } from 'svelte';
  import { goto, beforeNavigate } from '$app/navigation';
  import { page } from '$app/state';
  import ThemeToggle from '$lib/components/ThemeToggle.svelte';
  import GuideMascot from '$lib/components/GuideMascot.svelte';
  import Sidebar from '$lib/components/Sidebar.svelte';
  import UserMenu from '$lib/components/UserMenu.svelte';
  import ConnectionBanner from '$lib/components/ConnectionBanner.svelte';
  import CommandPalette from '$lib/components/CommandPalette.svelte';
  import { ConfirmHost, Toaster, confirm } from '$lib/components/ui';
  import { guideStore } from '$lib/stores/guide.svelte';
  import { authStore } from '$lib/stores/auth.svelte';
  import { dirtyStore } from '$lib/stores/dirty.svelte';
  import { prefsStore } from '$lib/stores/prefs.svelte';
  import { themeStore } from '$lib/stores/theme.svelte';
  import { installShortcutListener } from '$lib/utils/shortcuts';
  import { focusTrap } from '$lib/utils/focusTrap';
  import { APP_VERSION } from '$lib/utils/version';
  import { HelpCircle, Menu, PanelLeftClose, PanelLeftOpen } from 'lucide-svelte';

  let { children }: { children: Snippet } = $props();

  // `/link` (account linking) is public so the layout guard doesn't strip the
  // ?token= query on redirect; the page does its own auth, preserving the full
  // URL through login.
  const PUBLIC_PATHS = ['/login', '/logout', '/link'];
  const isPublicPath = (path: string) => PUBLIC_PATHS.some((p) => path === p || path.startsWith(p + '/'));

  const ADMIN_PATHS = ['/admin'];
  const isAdminPath = (path: string) => ADMIN_PATHS.some((p) => path === p || path.startsWith(p + '/'));

  const isPublic = $derived(isPublicPath(page.url.pathname));

  function guard(targetPath: string): boolean {
    if (isPublicPath(targetPath)) return true;
    if (!authStore.isAuthenticated) {
      const redirect = encodeURIComponent(targetPath === '/' ? '/' : targetPath);
      goto(`/login?redirect=${redirect}`);
      return false;
    }
    if (isAdminPath(targetPath) && authStore.session?.role !== 'admin') {
      goto('/');
      return false;
    }
    return true;
  }

  // Mobile drawer state — independent from the desktop collapsed pref so
  // that closing the drawer on a phone doesn't also shrink the sidebar
  // back on desktop.
  let mobileNavOpen = $state(false);

  // Track the mobile breakpoint so the off-canvas drawer is only made
  // `inert`/focus-trapped on phones — on desktop the same <aside> is the
  // persistent sidebar and must stay fully interactive.
  let isMobileViewport = $state(false);
  $effect(() => {
    if (typeof window === 'undefined') return;
    const mq = window.matchMedia('(max-width: 767px)');
    const sync = () => (isMobileViewport = mq.matches);
    sync();
    mq.addEventListener('change', sync);
    return () => mq.removeEventListener('change', sync);
  });
  // When closed on mobile the drawer sits off-screen but would otherwise stay
  // tabbable; `inert` removes it from the tab order and pointer events there.
  const drawerInert = $derived(isMobileViewport && !mobileNavOpen);

  // Escape closes the mobile drawer (focus is restored by the focusTrap).
  $effect(() => {
    if (typeof window === 'undefined' || !mobileNavOpen) return;
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') mobileNavOpen = false;
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });

  onMount(() => {
    guard(page.url.pathname);
    // User-authored themes live in the API, so their CSS can only be built
    // once we're authenticated. app.html has already replayed the cached CSS
    // pre-paint; this refreshes it and catches themes added elsewhere.
    if (authStore.isAuthenticated) void themeStore.loadCustom();
    const offShortcuts = installShortcutListener();
    return () => offShortcuts();
  });

  // Tracks the target while the unsaved-changes modal is open. After the
  // user confirms, we replay the navigation through goto(); the second
  // beforeNavigate call recognises this URL and skips the dialog so we
  // don't get caught in a loop. Pathname-only match because URL.href can
  // pick up subtle differences (trailing slash, port, hash) that strict
  // === would reject — the dialog must always recognise its own replay.
  let leavingTo: { pathname: string; search: string; hash: string } | null = null;
  let modalOpen = false;

  beforeNavigate((nav) => {
    if (!nav.to) return;
    const isReplay =
      leavingTo !== null
      && nav.to.url.pathname === leavingTo.pathname
      && nav.to.url.search === leavingTo.search;
    if (isReplay) {
      leavingTo = null;
      mobileNavOpen = false;
      return;
    }
    // Don't double-open the modal if SvelteKit fires beforeNavigate again
    // (e.g. user clicks another link while the dialog is still up).
    if (modalOpen) {
      nav.cancel();
      return;
    }
    if (dirtyStore.dirty && nav.to.url.pathname !== page.url.pathname) {
      const target = {
        pathname: nav.to.url.pathname,
        search: nav.to.url.search,
        hash: nav.to.url.hash,
      };
      nav.cancel();
      leavingTo = target;
      modalOpen = true;
      void confirm({
        title: 'Leave with unsaved changes?',
        message: 'Your changes to this workflow will be lost.',
        confirmLabel: 'Leave anyway',
        cancelLabel: 'Stay',
        tone: 'danger',
      }).then((ok) => {
        modalOpen = false;
        if (ok) {
          // markClean BEFORE goto so the replayed beforeNavigate skips the
          // dirty check cleanly. The pathname-replay guard above is the
          // belt; this is the suspenders.
          dirtyStore.markClean();
          void goto(target.pathname + target.search + target.hash);
        } else {
          // User chose Stay — drop the pending target so future navs prompt
          // again.
          leavingTo = null;
        }
      });
      return;
    }
    if (!guard(nav.to.url.pathname)) nav.cancel();
    mobileNavOpen = false;
  });

  // Warn before the browser tab closes too — beforeNavigate only fires
  // for in-app navigation.
  $effect(() => {
    if (typeof window === 'undefined') return;
    function onBeforeUnload(e: BeforeUnloadEvent) {
      if (dirtyStore.dirty) {
        e.preventDefault();
        e.returnValue = '';
      }
    }
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  });

  const sidebarCollapsed = $derived(prefsStore.sidebarCollapsed);
</script>

{#if isPublic}
  {@render children()}
{:else}
  <ConnectionBanner />
  <div class="flex h-screen text-surface-900-100 antialiased overflow-hidden">
    <!-- Mobile drawer backdrop -->
    {#if mobileNavOpen}
      <button
        type="button"
        aria-label="Close navigation"
        class="md:hidden fixed inset-0 z-30 bg-surface-950/60 backdrop-blur-sm"
        onclick={() => (mobileNavOpen = false)}
      ></button>
    {/if}

    <!-- Sidebar: hidden by default on mobile, slides in when mobileNavOpen.
         Width controlled by prefsStore on desktop. Tailwind v4 only sees
         literal class names at build time, so each width variant is spelled
         out (no `md:{dynamic}` interpolation). -->
    <aside
      class="shrink-0 flex flex-col bg-surface-100-900/60 backdrop-blur-xl border-r border-surface-200-800/60 z-40
             fixed inset-y-0 left-0 w-60 md:static md:inset-auto
             transition-[width,transform] duration-200
             {mobileNavOpen ? 'translate-x-0' : '-translate-x-full md:translate-x-0'}
             {sidebarCollapsed ? 'md:w-14' : 'md:w-60'}"
      aria-label="Main navigation"
      tabindex="-1"
      inert={drawerInert || undefined}
      use:focusTrap={mobileNavOpen && isMobileViewport}
    >
      <!-- Brand — collapsed shows the W mark only (SVG); expanded uses
           the horizontal wordmark PNG. The PNG has ~30% internal padding,
           so we lock the brand container to h-24 (96 px) and zoom the
           image 1.7× with `transform: scale()`. The transform doesn't
           affect layout — the header stays h-24 — but the visible logo
           fills the window with the whitespace cropped on all sides. -->
      <a href="/" class="flex items-center justify-center {sidebarCollapsed ? 'px-1 h-20' : 'px-3 h-24'} border-b border-surface-200-800/60 shrink-0 hover:bg-surface-200-800/30 transition-colors overflow-hidden">
        {#if sidebarCollapsed}
          <img
            src="/logo.svg"
            alt="FlowWeaver"
            class="w-14 h-14 object-contain shrink-0"
            width="56"
            height="56"
          />
        {:else}
          <img
            src="/FlowWeaver_Logo_1.png"
            alt="FlowWeaver"
            class="h-full w-auto"
            style="transform: scale(1.7); transform-origin: center;"
          />
        {/if}
      </a>

      <div class="flex-1 overflow-y-auto relative scroll-fade">
        <Sidebar collapsed={sidebarCollapsed} />
      </div>

      {#if authStore.session}
        <div class="border-t border-surface-200-800/60 px-3 py-2 flex items-center gap-2 shrink-0 bg-surface-100-900/40">
          <UserMenu />
          {#if !sidebarCollapsed}
            <div class="min-w-0 flex-1">
              <div class="text-xs font-medium text-surface-900-100 truncate leading-tight">
                {authStore.session.username}
              </div>
              <div class="text-[10px] text-surface-500 truncate leading-tight capitalize">
                {authStore.session.role}
              </div>
            </div>
          {/if}
        </div>
      {/if}

      <div class="border-t border-surface-200-800/60 px-2 py-2 flex items-center justify-between gap-2 shrink-0 bg-surface-100-900/60">
        <div class="flex items-center gap-1">
          {#if !sidebarCollapsed && !guideStore.enabled}
            <button
              type="button"
              onclick={() => guideStore.setEnabled(true)}
              class="inline-flex items-center justify-center w-7 h-7 rounded-md text-surface-600-400 hover:text-surface-900-100 hover:bg-surface-200-800/60 cursor-pointer transition-colors"
              title="Show guide mascot"
              aria-label="Show guide mascot"
            >
              <HelpCircle size={14} />
            </button>
          {/if}
          {#if !sidebarCollapsed}
            <ThemeToggle />
          {/if}
          <button
            type="button"
            onclick={() => prefsStore.toggleSidebar()}
            class="hidden md:inline-flex items-center justify-center w-7 h-7 rounded-md text-surface-600-400 hover:text-surface-900-100 hover:bg-surface-200-800/60 cursor-pointer transition-colors"
            title={sidebarCollapsed ? 'Expand sidebar' : 'Collapse sidebar'}
            aria-label={sidebarCollapsed ? 'Expand sidebar' : 'Collapse sidebar'}
            aria-pressed={sidebarCollapsed}
          >
            {#if sidebarCollapsed}
              <PanelLeftOpen size={14} />
            {:else}
              <PanelLeftClose size={14} />
            {/if}
          </button>
        </div>
        {#if !sidebarCollapsed}
          <span class="text-[10px] font-mono text-surface-500 tabular-nums">v{APP_VERSION}</span>
        {/if}
      </div>
    </aside>

    <!-- Main content -->
    <main class="flex-1 overflow-auto min-w-0 relative">
      <!-- Mobile top bar with hamburger. Only renders on <md so it doesn't
           duplicate the desktop sidebar header. -->
      <div class="md:hidden sticky top-0 z-20 flex items-center gap-2 px-3 h-16 bg-surface-100-900/80 backdrop-blur-md border-b border-surface-200-800/60">
        <button
          type="button"
          onclick={() => (mobileNavOpen = true)}
          aria-label="Open navigation"
          class="inline-flex items-center justify-center w-10 h-10 rounded-md text-surface-700-300 hover:bg-surface-200-800/60 shrink-0"
        >
          <Menu size={20} />
        </button>
        <!-- Fixed h-12 container + 2× transform — same crop-via-zoom
             pattern as the sidebar / login, layout box stays compact. -->
        <div class="flex-1 h-12 overflow-hidden flex items-center justify-center">
          <img
            src="/FlowWeaver_Logo_1.png"
            alt="FlowWeaver"
            class="h-full w-auto"
            style="transform: scale(2); transform-origin: center;"
          />
        </div>
      </div>
      {@render children()}
    </main>
  </div>

  <GuideMascot />
  <CommandPalette />
{/if}

<ConfirmHost />
<Toaster />

<style>
  .scroll-fade {
    mask-image: linear-gradient(to bottom, transparent 0, black 12px, black calc(100% - 12px), transparent 100%);
    -webkit-mask-image: linear-gradient(to bottom, transparent 0, black 12px, black calc(100% - 12px), transparent 100%);
  }
</style>
