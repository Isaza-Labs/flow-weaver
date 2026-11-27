<script lang="ts">
  // Python admin: the snippet import allowlist (pip) and the uv virtualenvs on
  // one screen, one tab each. The tab lives in the URL (?tab=environments; no
  // param means packages) so links, F5 and Back land on the same tab. Switching
  // pushes a history entry on purpose: Back returns to the previous tab.
  // The old /admin/python-packages and /admin/python-environments pages
  // redirect here.
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import { PageHeader, Tabs } from '$lib/components/ui';
  import PythonPackagesPanel from '$lib/components/python/PythonPackagesPanel.svelte';
  import PythonEnvironmentsPanel from '$lib/components/python/PythonEnvironmentsPanel.svelte';

  type Tab = 'packages' | 'environments';

  const TABS: { value: Tab; label: string }[] = [
    { value: 'packages', label: 'Packages' },
    { value: 'environments', label: 'Environments' },
  ];

  // Derived from the URL, not copied into state, so Back/Forward switch tabs.
  const tab = $derived<Tab>(page.url.searchParams.get('tab') === 'environments' ? 'environments' : 'packages');

  function onTabChange(v: Tab) {
    if (v === tab) return;
    const url = new URL(page.url);
    url.searchParams.set('tab', v);
    void goto(url.pathname + url.search + url.hash, { keepFocus: true, noScroll: true });
  }
</script>

<svelte:head>
  <title>{tab === 'environments' ? 'Python environments' : 'Python packages'} · FlowWeaver</title>
</svelte:head>

<div class="p-6 space-y-6">
  <PageHeader
    title="Python"
    description="What python_snippet scripts may import, and the uv environments devices, snippets and playbooks can run with."
  />

  <Tabs value={tab} tabs={TABS} label="Python" onChange={onTabChange} />

  <div role="tabpanel" aria-label={tab === 'environments' ? 'Environments' : 'Packages'}>
    {#if tab === 'environments'}
      <PythonEnvironmentsPanel />
    {:else}
      <PythonPackagesPanel />
    {/if}
  </div>
</div>
