<script lang="ts">
  import DocLayout from './_components/DocLayout.svelte';
  import Callout from './_components/Callout.svelte';
  import {
    Workflow, Activity, Server, Network, KeyRound, FlaskConical,
    Bot, Shield, Settings2, Cable, CalendarClock, Users,
    LayoutDashboard, BookOpen, Layers, GitBranch, Terminal, KeySquare,
    Package, Webhook, Upload, LifeBuoy, BookA, Table2,
  } from 'lucide-svelte';

  const tiles: Array<{
    href: string;
    label: string;
    description: string;
    icon: typeof Workflow;
  }> = [
    { href: '/docs/getting-started', label: 'Getting started', description: 'Sign in, understand the shell, navigate by role.', icon: BookOpen },
    { href: '/docs/dashboard', label: 'Dashboard', description: 'Fleet stats, job queue, recent run activity.', icon: LayoutDashboard },
    { href: '/docs/workflows', label: 'Workflows', description: 'DAG designer, drafts, promotion, rollback, export.', icon: Workflow },
    { href: '/docs/workflows/import', label: 'Importing workflows', description: 'Bring exports back in, within or across instances.', icon: Upload },
    { href: '/docs/workflows/triggers', label: 'Triggers and webhooks', description: 'Start runs from signed HTTP calls.', icon: Webhook },
    { href: '/docs/subflows', label: 'Subflows', description: 'Call one workflow from another.', icon: Layers },
    { href: '/docs/snippets', label: 'Snippets', description: 'Reusable building blocks: Python, Ansible, REST, transforms.', icon: Settings2 },
    { href: '/docs/git-repos', label: 'Git repositories', description: 'Register repos and read, write, commit and push from workflows.', icon: GitBranch },
    { href: '/docs/integrations', label: 'Integrations', description: 'External systems and their discoverable actions.', icon: Cable },
    { href: '/docs/runs', label: 'Runs', description: 'Execution history, live monitor, step timelines.', icon: Activity },
    { href: '/docs/schedules', label: 'Schedules', description: 'Cron triggers, timezones, calendar view.', icon: CalendarClock },
    { href: '/docs/devices', label: 'Devices', description: 'Managed targets and their properties.', icon: Server },
    { href: '/docs/device-pools', label: 'Device pools', description: 'Named device groups and QA lab scoping.', icon: Network },
    { href: '/docs/credentials', label: 'Credentials', description: 'SSH/NETCONF authentication material.', icon: KeyRound },
    { href: '/docs/qa-lab', label: 'QA lab', description: 'Promotion readiness and QA-scoped runs.', icon: FlaskConical },
    { href: '/docs/ai', label: 'AI', description: 'Chat, providers, agents, skills, API specs.', icon: Bot },
    { href: '/docs/vendor-commands', label: 'Vendor commands', description: 'The CLI catalog that validates SSH commands.', icon: Terminal },
    { href: '/docs/policies', label: 'Policies', description: 'Guardrails and approval gates (authored by admins).', icon: Shield },
    { href: '/docs/permissions', label: 'Permissions', description: 'Capability grants and per-resource access (admin).', icon: KeySquare },
    { href: '/docs/admin/python-packages', label: 'Python packages', description: 'Extra imports for Python snippets (admin).', icon: Package },
    { href: '/docs/admin', label: 'Administration', description: 'Users, audit, traces, artifacts, secrets, SLOs, settings (admin).', icon: Users },
    { href: '/docs/reference', label: 'Screens and roles', description: 'Every screen with its route, minimum role and chapter.', icon: Table2 },
    { href: '/docs/troubleshooting', label: 'Troubleshooting', description: 'Find the fix by symptom.', icon: LifeBuoy },
    { href: '/docs/glossary', label: 'Glossary', description: 'One meaning per term.', icon: BookA },
  ];
</script>

<DocLayout
  title="FlowWeaver user manual"
  lead="A complete reference to every screen, action, and option in the FlowWeaver frontend. Use the left nav to jump to a section, or start below."
>
  <section>
    <h2>What is FlowWeaver?</h2>
    <p>
      FlowWeaver is a network-automation platform that lets your team define, run,
      schedule, and govern workflows across a fleet of devices. A workflow is a
      directed graph of <strong>snippets</strong> (Python, Ansible, REST calls, data
      transforms) that execute against one or more <strong>devices</strong> or
      <strong>device pools</strong>. Runs are tracked end-to-end with live status,
      per-step logs, input/output payloads, and a real-time DAG view.
    </p>
    <p>
      An embedded <strong>AI assistant</strong> can inspect your workflows, propose
      fixes to failed steps, and author changes directly. Administrators get an
      audit log of every meaningful write, an action-trail of every tool dispatch,
      and a global policy layer for guardrails.
    </p>
  </section>

  <section>
    <h2>How this manual is organized</h2>
    <p>
      The left nav follows the app sidebar's groups (Build, Integrate,
      Operate, Govern, Intelligence) and adds administration, account and
      reference sections:
    </p>
    <ul>
      <li><strong>Build</strong> — the pieces you author: workflows (and importing them, triggers, subflows), snippets, Git repositories.</li>
      <li><strong>Integrate</strong> — connections to outside systems: integrations, vendor commands, MCP servers, messaging channels, email.</li>
      <li><strong>Operate</strong> — what you run and against what: runs, schedules, devices, pools, credentials, QA lab.</li>
      <li><strong>Govern</strong> — guardrails: policies, permissions, Python packages; the app's sidebar shows this group to admins only.</li>
      <li><strong>Intelligence</strong> — the AI assistant and its configuration.</li>
      <li><strong>Administration</strong> — admin-only system management: users, audit, traces, artifacts, secrets, SLOs, settings.</li>
      <li><strong>Account</strong> — password, session and themes.</li>
      <li><strong>Reference</strong> — screens and roles, troubleshooting, glossary.</li>
    </ul>
    <p>
      Each page describes, in order: what the screen is for, where it lives in the
      app, the role required to see it, every field and button it exposes, the
      flows you'll run through it, and any gotchas you should know about.
    </p>
  </section>

  <section>
    <h2>Jump to a section</h2>
    <div class="grid grid-cols-1 sm:grid-cols-2 gap-2 not-prose">
      {#each tiles as tile}
        <a
          href={tile.href}
          class="group flex items-start gap-3 p-3 rounded-md border border-surface-300-700 bg-surface-100-900/30 hover:bg-surface-200-800/40 hover:border-primary-500/40 transition-colors"
        >
          <span class="shrink-0 w-8 h-8 rounded-md bg-primary-500/10 text-primary-300 ring-1 ring-primary-500/25 flex items-center justify-center">
            <tile.icon size={15} />
          </span>
          <span class="min-w-0 flex-1">
            <span class="block text-[13px] font-semibold text-surface-900-100 group-hover:text-primary-200 transition-colors">
              {tile.label}
            </span>
            <span class="block text-[12px] text-surface-500-400 mt-0.5 leading-snug">
              {tile.description}
            </span>
          </span>
        </a>
      {/each}
    </div>
  </section>

  <section>
    <h2>Conventions used in this manual</h2>
    <Callout tone="where" title="Where to find it">
      Every page opens with a "Where to find it" box showing the exact URL and how
      to reach it from the sidebar, so you can tab between the manual and the app.
    </Callout>
    <Callout tone="admin" title="Admin only">
      Features protected by the <code>admin</code> role carry this box. If you
      don't have the role, the feature is hidden from your sidebar. Opening an
      <code>/admin/…</code> URL sends you back to the dashboard; on other
      screens the server refuses the actions you aren't allowed to take.
    </Callout>
    <Callout tone="warning" title="Heads up">
      Destructive, irreversible, or high-blast-radius actions are flagged in this
      style. Pay attention before confirming.
    </Callout>
    <Callout tone="success" title="Tip">
      Short-cuts, productivity hints, and power-user tricks use this style.
    </Callout>
  </section>

  <section>
    <h2>Next step</h2>
    <p>
      Start with <a href="/docs/getting-started">Getting started</a> to learn the
      app shell, the role model, and the fastest path from login to running your
      first workflow. Or pick any section from the left nav.
    </p>
  </section>
</DocLayout>
