<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { snippets, errorMessage, type Snippet } from '$lib/api/client';
  import {
    PageHeader, Card, Button, Input, Select, Alert, toast,
  } from '$lib/components/ui';

  type SnippetType =
    | 'python_snippet'
    | 'ansible_playbook'
    | 'rest_call'
    | 'transform'
    | 'jmespath'
    | 'ping'
    | 'integration_action'
    | 'git'
    | 'slack_message'
    | 'email_send';
  type TargetMode = 'per_device' | 'once';

  let name = $state('');
  let description = $state('');
  let type = $state<SnippetType>('python_snippet');
  let targetMode = $state<TargetMode>('per_device');
  let creating = $state(false);
  let createError = $state('');

  const STARTER_CODE: Record<SnippetType, string> = {
    python_snippet:
      'from flowweaver_runtime import get_input, set_output\n\n' +
      '# run(ctx) is auto-invoked by flowweaver_runtime on exit.\n' +
      '# Do not add a trailing run(None) call.\n' +
      'def run(ctx):\n    inp = get_input()\n    set_output({"hello": "world", "echo": inp})\n',
    ansible_playbook:
      '---\n- name: Example play\n  hosts: all\n  gather_facts: no\n  tasks:\n    - name: Ping\n      ansible.builtin.ping:\n',
    rest_call: '{"url": "https://example.com/api", "method": "GET", "headers": {}}',
    transform: '@',
    jmespath: '@',
    ping: '{"count": 4, "timeout": 5}',
    integration_action: '{}',
    // The git handler reads its operation + parameters from each
    // workflow node's config_overrides, NOT from this `code` field.
    // The starter is documentation: it shows the shape users will
    // paste into the node dialog when adding this snippet to a graph.
    git:
      '{\n  "operation": "read_file",\n  "repository_id": "<paste-from-/integrations/git>",\n  "path": "configs/example.cfg",\n  "ref": "main"\n}',
    // slack_message reads channel + text from each node's config_overrides,
    // not from this `code` field. The starter just documents the shape.
    slack_message:
      '{\n  "channel": "#alerts",\n  "text": "Hello from FlowWeaver"\n}',
    // email_send reads recipients + body from each node's config_overrides;
    // host/port/TLS/password live on the channel picked in /email, never here.
    email_send:
      '{\n  "to": "ops@example.com",\n  "subject": "FlowWeaver notification",\n'
      + '  "body": "Hello from FlowWeaver"\n}',
  };

  // The backend REQUIRES a logic_diagram_mermaid for code types
  // (python_snippet / transform / jmespath). Ship a valid starter so
  // "Create" doesn't fail with logic_diagram_invalid; the user refines it
  // in the editor. Other types don't need one.
  const STARTER_MERMAID: Partial<Record<SnippetType, string>> = {
    python_snippet: 'graph TD\n  IN[get_input] --> LOGIC[transform / call]\n  LOGIC --> OUT[set_output]',
    transform: 'graph TD\n  IN[step input] --> EXPR[JMESPath projection] --> OUT[output]',
    jmespath: 'graph TD\n  IN[step input] --> EXPR[JMESPath query] --> OUT[output]',
  };

  const TYPE_LABELS: Record<SnippetType, string> = {
    python_snippet: 'Python snippet',
    ansible_playbook: 'Ansible playbook',
    rest_call: 'REST call',
    transform: 'Transform (JMESPath)',
    jmespath: 'JMESPath query',
    ping: 'Ping',
    integration_action: 'Integration action',
    git: 'Git operation (read/write/commit/push/pull)',
    slack_message: 'Slack message',
    email_send: 'Email (SMTP)',
  };

  // `git` ops are repo-scoped, and `slack_message` / `email_send` deliver once
  // — none are device-scoped — so default them to "once" instead of fanning
  // out across N devices. Switching back to a device-scoped type flips it back.
  const ONCE_BY_DEFAULT: SnippetType[] = ['git', 'slack_message', 'email_send'];
  $effect(() => {
    if (sourceId) return;   // a copy keeps the source's target mode
    targetMode = ONCE_BY_DEFAULT.includes(type) ? 'once' : 'per_device';
  });

  // ── Start from an existing snippet ──────────────────────────────────────
  //
  // The Type picker above lists HANDLER TYPES, and the backend validates against
  // exactly that list — so a seeded baseline like the paramiko SSH primitive can
  // never appear there: it is a `python_snippet` with a particular body, not a
  // type of its own. People still come here looking for it, which is the whole
  // reason this control exists rather than living only as a Duplicate button on
  // the list page.
  let existing = $state<Snippet[]>([]);
  let sourceId = $state('');
  const source = $derived(existing.find((s) => s.id === sourceId) ?? null);

  onMount(async () => {
    try {
      existing = (await snippets.list(500, 0)).data ?? [];
    } catch {
      // Non-fatal: the page still creates a blank snippet, which is what it did
      // before this picker existed. Failing the whole form over an optional
      // convenience would be worse than offering it empty.
      existing = [];
    }
  });

  // A copy keeps the source's type and target mode. Letting either be changed
  // here would file a Python body under `ping` and fail at run time with an error
  // pointing at the script.
  $effect(() => {
    if (!source) return;
    type = source.type as SnippetType;
    targetMode = source.target_mode as TargetMode;
  });

  // The name suggestion is a one-shot on picking, not an effect: an effect that
  // reads `name` to decide whether to write `name` re-runs on every keystroke,
  // and only stops because of its own guard.
  function onSourcePicked() {
    const picked = existing.find((s) => s.id === sourceId);
    if (picked && !name.trim()) name = `${picked.name} (copy)`;
  }

  async function create() {
    createError = '';
    if (!name.trim()) {
      createError = 'Name is required';
      toast.warning('Name is required');
      return;
    }
    creating = true;
    try {
      let created: Snippet;
      if (source) {
        // Description is the one field worth overriding on a copy: everything
        // else (body, schemas, diagram, timeouts, the change/rollback
        // declarations) is what made the source worth copying.
        const overrides = {
          name: name.trim(),
          ...(description.trim() ? { description: description.trim() } : {}),
        };
        try {
          created = await snippets.duplicate(source.id, overrides);
        } catch (e) {
          // network_enabled is admin-gated on create. A non-admin copying the
          // paramiko baseline gets an inert copy and is told so, rather than an
          // error that reads as "creating from a template is broken".
          if (source.network_enabled && /admin role/i.test(errorMessage(e))) {
            created = await snippets.duplicate(source.id, { ...overrides, network_enabled: false });
            toast.warning('Copied without network access', {
              description: 'Enabling it for interactive SSH needs an admin.',
            });
          } else {
            throw e;
          }
        }
      } else {
        created = await snippets.create({
          name: name.trim(),
          description: description.trim() || null,
          type,
          target_mode: targetMode,
          max_parallel: 10,
          timeout_seconds: 300,
          code: STARTER_CODE[type] ?? '',
          logic_diagram_mermaid: STARTER_MERMAID[type] ?? null,
          input_schema: {},
          output_schema: {},
          retry_policy: {},
        });
      }
      toast.success('Snippet created', { description: created.name });
      goto(`/snippets/${created.id}`);
    } catch (e) {
      createError = errorMessage(e);
      toast.fromError(e, 'Couldn\u2019t create snippet', { action: { label: 'Retry', onClick: create } });
    } finally {
      creating = false;
    }
  }
</script>

<svelte:head><title>New snippet · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-2xl mx-auto space-y-5">
  <PageHeader
    title="New snippet"
    description="Define a reusable snippet that workflows can call. A starter template loads automatically."
    breadcrumbs={[{ label: 'Snippets', href: '/snippets' }, { label: 'New' }]}
  />

  <Card>
    <div class="space-y-4">
      <Input label="Name" help="snippets.name" bind:value={name} placeholder="e.g. Nokia LLDP gather" />
      <Input label="Description (optional)" help="snippets.description" bind:value={description} placeholder="Short note about what this snippet does" />

      <Select label="Start from" bind:value={sourceId} onchange={onSourcePicked}>
        <option value="">Blank — generic starter for the type below</option>
        {#each existing as snip (snip.id)}
          <option value={snip.id}>{snip.name} · {snip.type}</option>
        {/each}
      </Select>
      {#if source}
        <p class="text-xs text-surface-500">
          Copies the body, schemas, diagram and timeouts of
          <span class="font-medium">{source.name}</span>. Type and target mode come
          from it — the body is written against them.
          {#if source.network_enabled}
            It is network-enabled (interactive SSH); keeping that on the copy needs
            an admin.
          {/if}
        </p>
      {/if}

      <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <Select label="Type" help="snippets.type" bind:value={type} disabled={!!source}>
          {#each Object.entries(TYPE_LABELS) as [val, label]}
            <option value={val}>{label}</option>
          {/each}
        </Select>
        <Select label="Target mode" help="snippets.target_mode" bind:value={targetMode} disabled={!!source}>
          <option value="per_device">Per device (runs once per target)</option>
          <option value="once">Once (single execution, no devices)</option>
        </Select>
      </div>

      {#if createError}<Alert tone="error">{createError}</Alert>{/if}

      <div class="flex items-center justify-between pt-2">
        <p class="text-xs text-surface-500">
          {source ? 'The copy opens in the editor.' : 'A starter template will be loaded for the selected type.'}
        </p>
        <div class="flex gap-2">
          <Button variant="ghost" href="/snippets">Cancel</Button>
          <Button variant="primary" onclick={create} loading={creating}>Create and edit</Button>
        </div>
      </div>
    </div>
  </Card>
</div>
