import type { IconComponent } from '$lib/components/ui';
import {
  LayoutDashboard,
  Workflow,
  Layers,
  Settings2,
  Cable,
  GitBranch,
  Activity,
  CalendarClock,
  Server,
  Network,
  KeyRound,
  KeySquare,
  FlaskConical,
  ShieldCheck,
  Terminal,
  MessageSquare,
  Mail,
  Package,
  Plug,
  Bot,
  BookOpen,
  Files,
} from 'lucide-svelte';

export type NavDest = {
  href: string;
  label: string;
  icon: IconComponent;
  group: string;
  adminOnly?: boolean;
};

// Single source of truth for "linkable destinations" — consumed by the
// dashboard quick-access picker. Mirrors the Sidebar groups; keep the two in
// sync when routes change. Icons are component refs (not JSON-serializable),
// so the dashboard persists only the `href` and resolves label + icon here at
// render time via destByHref().
export const NAV_DESTS: NavDest[] = [
  { href: '/', label: 'Dashboard', icon: LayoutDashboard, group: 'General' },

  { href: '/workflows', label: 'Workflows', icon: Workflow, group: 'Build' },
  { href: '/subflows', label: 'Subflows', icon: Layers, group: 'Build' },
  { href: '/snippets', label: 'Snippets', icon: Settings2, group: 'Build' },
  { href: '/integrations/git', label: 'Git repos', icon: GitBranch, group: 'Build' },

  { href: '/integrations', label: 'Integrations', icon: Cable, group: 'Integrate' },
  { href: '/vendor-commands', label: 'Vendor commands', icon: Terminal, group: 'Integrate', adminOnly: true },
  { href: '/admin/mcp-servers', label: 'MCP servers', icon: Plug, group: 'Integrate', adminOnly: true },
  { href: '/admin/messaging-channels', label: 'Messaging channels', icon: MessageSquare, group: 'Integrate', adminOnly: true },
  { href: '/email', label: 'Email', icon: Mail, group: 'Integrate', adminOnly: true },

  { href: '/runs', label: 'Runs', icon: Activity, group: 'Operate' },
  { href: '/schedules', label: 'Schedules', icon: CalendarClock, group: 'Operate' },
  { href: '/devices', label: 'Devices', icon: Server, group: 'Operate' },
  { href: '/device-pools', label: 'Device pools', icon: Network, group: 'Operate' },
  { href: '/credentials', label: 'Credentials', icon: KeyRound, group: 'Operate' },
  { href: '/qa', label: 'QA lab', icon: FlaskConical, group: 'Operate' },
  { href: '/admin/artifacts', label: 'Artifacts', icon: Files, group: 'Operate', adminOnly: true },

  { href: '/policies', label: 'Policies', icon: ShieldCheck, group: 'Govern', adminOnly: true },
  { href: '/permissions', label: 'Permissions', icon: KeySquare, group: 'Govern', adminOnly: true },
  { href: '/admin/python-packages', label: 'Python packages', icon: Package, group: 'Govern', adminOnly: true },

  { href: '/ai', label: 'AI', icon: Bot, group: 'Intelligence' },
  { href: '/docs', label: 'Documentation', icon: BookOpen, group: 'Help' },
];

const byHref = new Map(NAV_DESTS.map((d) => [d.href, d]));

export function destByHref(href: string): NavDest | undefined {
  return byHref.get(href);
}
