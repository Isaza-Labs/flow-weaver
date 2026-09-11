<script lang="ts">
  import { page } from '$app/state';
  import { onMount } from 'svelte';
  import { authStore } from '$lib/stores/auth.svelte';
  import { animate, stagger, prefersReducedMotion } from '$lib/anim';
  import {
    LayoutDashboard,
    Workflow,
    CalendarClock,
    Server,
    Activity,
    Settings2,
    Cable,
    Bot,
    ShieldCheck,
    FlaskConical,
    Network,
    KeyRound,
    KeySquare,
    BookOpen,
    Terminal,
    GitBranch,
    Layers,
    MessageSquare,
    Mail,
    Package,
    Plug,
    Files,
  } from 'lucide-svelte';
  import type { IconComponent } from '$lib/components/ui';

  let { collapsed = false }: { collapsed?: boolean } = $props();

  // `featured` lifts an entry out of the uniform nav rhythm — a tinted,
  // ringed row instead of a plain one. Reserved for the surface we want
  // people to reach for first; using it twice would cancel it out.
  type NavItem = {
    href: string;
    label: string;
    icon: IconComponent;
    adminOnly?: boolean;
    featured?: boolean;
  };
  type NavGroup = { label?: string; items: NavItem[]; adminOnly?: boolean };

  const groups: NavGroup[] = [
    {
      items: [{ href: '/', label: 'Dashboard', icon: LayoutDashboard }],
    },
    {
      // Sits above Build on purpose: the agent is the entry point to most of
      // what follows, not a specialised tool at the bottom of the list.
      label: 'Intelligence',
      items: [
        { href: '/ai', label: 'AI', icon: Bot, featured: true },
      ],
    },
    {
      label: 'Build',
      items: [
        { href: '/workflows', label: 'Workflows', icon: Workflow },
        { href: '/subflows', label: 'Subflows', icon: Layers },
        { href: '/snippets', label: 'Snippets', icon: Settings2 },
        { href: '/integrations/git', label: 'Git repos', icon: GitBranch },
      ],
    },
    {
      // Everything that connects FlowWeaver to a system outside it: HTTP
      // integrations, the vendor-command catalog, MCP servers, chat channels
      // and SMTP. Only /integrations is non-admin — the rest hold credentials.
      label: 'Integrate',
      items: [
        { href: '/integrations', label: 'Integrations', icon: Cable },
        { href: '/vendor-commands', label: 'Vendor commands', icon: Terminal, adminOnly: true },
        { href: '/admin/mcp-servers', label: 'MCP servers', icon: Plug, adminOnly: true },
        { href: '/admin/messaging-channels', label: 'Messaging channels', icon: MessageSquare, adminOnly: true },
        { href: '/email', label: 'Email', icon: Mail, adminOnly: true },
      ],
    },
    {
      label: 'Operate',
      items: [
        { href: '/runs', label: 'Runs', icon: Activity },
        { href: '/schedules', label: 'Schedules', icon: CalendarClock },
        { href: '/devices', label: 'Devices', icon: Server },
        { href: '/device-pools', label: 'Device pools', icon: Network },
        { href: '/credentials', label: 'Credentials', icon: KeyRound },
        { href: '/qa', label: 'QA lab', icon: FlaskConical },
        { href: '/admin/artifacts', label: 'Artifacts', icon: Files, adminOnly: true },
      ],
    },
    {
      label: 'Govern',
      adminOnly: true,
      items: [
        { href: '/policies', label: 'Policies', icon: ShieldCheck, adminOnly: true },
        { href: '/permissions', label: 'Permissions', icon: KeySquare, adminOnly: true },
        { href: '/admin/python-packages', label: 'Python packages', icon: Package, adminOnly: true },
      ],
    },
    {
      label: 'Help',
      items: [
        { href: '/docs', label: 'Documentation', icon: BookOpen },
      ],
    },
  ];

  const isAdmin = $derived(authStore.session?.role === 'admin');

  function visible(item: NavItem): boolean {
    return !item.adminOnly || isAdmin;
  }

  function groupHasVisibleItems(group: NavGroup): boolean {
    return group.items.some(visible);
  }

  const activeHref = $derived.by(() => {
    const path = page.url.pathname;
    let best = '';
    for (const group of groups) {
      for (const item of group.items) {
        if (!visible(item)) continue;
        const match = item.href === '/'
          ? path === '/'
          : path === item.href || path.startsWith(item.href + '/');
        if (match && item.href.length > best.length) best = item.href;
      }
    }
    return best;
  });

  function isActive(href: string): boolean {
    return href === activeHref;
  }

  let navEl = $state<HTMLElement | null>(null);
  onMount(() => {
    if (!navEl || prefersReducedMotion()) return;
    const items = navEl.querySelectorAll<HTMLElement>('.nav-item');
    animate(items, {
      opacity: [0, 1],
      translateX: [-8, 0],
      duration: 320,
      ease: 'outCubic',
      delay: stagger(28),
    });
  });
</script>

<nav bind:this={navEl} class="flex flex-col gap-0.5 px-2 py-3">
  {#each groups as group}
    {#if groupHasVisibleItems(group)}
      {#if group.label && !collapsed}
        <div class="px-2 pt-3 pb-1 text-[10px] font-semibold uppercase tracking-[0.08em] text-surface-500/80">
          {group.label}
        </div>
      {:else if group.label && collapsed}
        <div class="my-1 mx-2 border-t border-surface-200-800/40"></div>
      {/if}
      {#each group.items as item}
        {#if visible(item)}
          {@const active = isActive(item.href)}
          <a
            href={item.href}
            aria-current={active ? 'page' : undefined}
            title={collapsed ? item.label : undefined}
            class="nav-item group relative flex items-center {collapsed ? 'justify-center px-0' : 'gap-2.5 px-2'} h-8 rounded-md text-sm cursor-pointer transition-all duration-150
              {active
                ? 'bg-primary-500/10 text-primary-200 ring-1 ring-inset ring-primary-500/20'
                : item.featured
                  ? 'bg-primary-500/[0.07] text-surface-900-100 ring-1 ring-inset ring-primary-500/25 hover:bg-primary-500/15 hover:ring-primary-500/40'
                  : 'text-surface-600-400 hover:bg-surface-200-800/50 hover:text-surface-900-100 ring-1 ring-inset ring-transparent'}"
          >
            {#if active && !collapsed}
              <span class="absolute left-0 top-1.5 bottom-1.5 w-0.5 rounded-r bg-primary-400" aria-hidden="true"></span>
            {/if}
            <item.icon
              size={15}
              class={active || item.featured
                ? 'text-primary-300'
                : 'text-surface-500 group-hover:text-surface-700-300 transition-colors'}
            />
            {#if !collapsed}
              <span class="{item.featured ? 'font-semibold' : 'font-medium'}">{item.label}</span>
            {/if}
          </a>
        {/if}
      {/each}
    {/if}
  {/each}
</nav>
