<script lang="ts">
  import { onMount } from 'svelte';
  import {
    adminSettings, type AdminSettingsBody,
    errorMessage,
  } from '$lib/api/client';
  import {
    PageHeader, Card, Button, Alert, Spinner, Input, Select,
    toast, confirm, FieldHint,
  } from '$lib/components/ui';
  import { Save, ShieldCheck, AlertTriangle, Wand2, KeySquare } from 'lucide-svelte';

  let settings = $state<AdminSettingsBody | null>(null);
  let granular = $state(false);
  let rbacMode = $state<'legacy' | 'granular'>('legacy');
  let fuzzyThreshold = $state(0.8);
  let fuzzyGap = $state(0.1);
  let loading = $state(true);
  let saving = $state(false);
  let loadError = $state<string | null>(null);
  let formError = $state<string | null>(null);

  onMount(load);

  async function load() {
    loading = true;
    loadError = null;
    try {
      settings = await adminSettings.get();
      granular = settings.permissions_granular_gating_enabled;
      rbacMode = settings.rbac_mode;
      fuzzyThreshold = settings.import_fuzzy_match_threshold;
      fuzzyGap = settings.import_fuzzy_match_gap;
    } catch (e) {
      loadError = errorMessage(e);
      toast.fromError(e, "Couldn't load settings");
    } finally {
      loading = false;
    }
  }

  const dirty = $derived(
    settings !== null
      && (
        granular !== settings.permissions_granular_gating_enabled
        || rbacMode !== settings.rbac_mode
        || Math.abs(fuzzyThreshold - settings.import_fuzzy_match_threshold) > 1e-9
        || Math.abs(fuzzyGap - settings.import_fuzzy_match_gap) > 1e-9
      ),
  );

  // Client-side clamps. Backend re-clamps too — these just keep the
  // input from showing nonsensical values mid-edit.
  function clampThreshold(v: number) { return Math.min(0.99, Math.max(0.5, v)); }
  function clampGap(v: number) { return Math.min(0.5, Math.max(0, v)); }

  async function save() {
    if (saving || !dirty) return;

    // Turning the granular overlay on from a previously-disabled state can
    // immediately strip write access from operators who lack per-resource
    // grants, so confirm before saving that transition.
    if (settings && granular && !settings.permissions_granular_gating_enabled) {
      const ok = await confirm({
        title: 'Enable per-resource grants?',
        message:
          'Operators without an explicit Editor or Owner grant will lose write ' +
          'access to existing workflows and integrations until you grant it under ' +
          "each resource's Permissions menu.",
        confirmLabel: 'Enable',
      });
      if (!ok) return;
    }

    saving = true;
    formError = null;
    try {
      const updated = await adminSettings.update({
        permissions_granular_gating_enabled: granular,
        rbac_mode: rbacMode,
        import_fuzzy_match_threshold: clampThreshold(fuzzyThreshold),
        import_fuzzy_match_gap: clampGap(fuzzyGap),
      });
      settings = updated;
      granular = updated.permissions_granular_gating_enabled;
      rbacMode = updated.rbac_mode;
      fuzzyThreshold = updated.import_fuzzy_match_threshold;
      fuzzyGap = updated.import_fuzzy_match_gap;
      toast.success('Settings saved');
    } catch (e) {
      formError = errorMessage(e);
      toast.fromError(e, "Couldn't save settings");
    } finally {
      saving = false;
    }
  }

  function discard() {
    if (!settings) return;
    granular = settings.permissions_granular_gating_enabled;
    rbacMode = settings.rbac_mode;
    fuzzyThreshold = settings.import_fuzzy_match_threshold;
    fuzzyGap = settings.import_fuzzy_match_gap;
    formError = null;
  }
</script>

<svelte:head><title>Settings · Admin · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-3xl mx-auto space-y-4">
  <PageHeader
    title="Settings"
    description="Global toggles. Changes propagate within a minute (server-side cache TTL)."
    breadcrumbs={[{ label: 'Admin', href: '/admin' }, { label: 'Settings' }]}
  />

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" label="Loading…" /></div>
  {:else if loadError}
    <Alert tone="error">{loadError}</Alert>
  {:else if settings}
    <Card>
      <div class="p-5 space-y-4">
        <div class="flex items-start gap-3">
          <div class="w-10 h-10 rounded-md bg-primary-500/10 text-primary-300 flex items-center justify-center shrink-0 ring-1 ring-primary-500/30">
            <ShieldCheck size={18} />
          </div>
          <div class="min-w-0 flex-1">
            <div class="text-sm font-semibold text-surface-900-100">Per-resource grants (legacy overlay)</div>
            <p class="text-xs text-surface-500 mt-1">
              The older overlay: when enabled, mutations on workflows and integrations also
              consult the per-resource
              <code class="font-mono text-[11px]">owner / editor / runner</code> grants set on
              each resource's <strong>Permissions</strong> tab. This is separate from the
              capability-based permission grants (see “RBAC mode” below) — leave it off unless
              you already use it.
            </p>
          </div>
        </div>

        {#if formError}
          <Alert tone="error">{formError}</Alert>
        {/if}

        <label class="flex items-start gap-3 cursor-pointer bg-surface-100-900/40 ring-1 ring-surface-200-800/80 rounded-md px-3 py-2.5" for="settings-granular">
          <FieldHint id="settings.granular_toggle" />
          <input
            id="settings-granular"
            type="checkbox"
            bind:checked={granular}
            disabled={saving}
            class="accent-primary-500 mt-0.5"
          />
          <span class="text-sm text-surface-900-100">
            Require per-resource Editor / Owner grants for workflow and integration writes
            <span class="block text-[11px] text-surface-500 mt-0.5">
              Operators without an explicit grant on a row will receive 403
              <span class="font-mono">missing_editor_grant</span> /
              <span class="font-mono">missing_owner_grant</span>.
            </span>
          </span>
        </label>

        {#if dirty && granular && !settings.permissions_granular_gating_enabled}
          <div class="inline-flex items-start gap-1.5 text-[11px] text-warning-300 bg-warning-500/10 ring-1 ring-warning-500/30 rounded px-2 py-1.5">
            <AlertTriangle size={12} class="mt-0.5 shrink-0" />
            <span>
              Existing workflows / integrations don't have grants yet. Operators may
              lose write access on save — grant them Editor or Owner under each
              resource's <strong>Permissions</strong> menu first.
            </span>
          </div>
        {/if}
      </div>
    </Card>

    <Card>
      <div class="p-5 space-y-4">
        <div class="flex items-start gap-3">
          <div class="w-10 h-10 rounded-md bg-primary-500/10 text-primary-300 flex items-center justify-center shrink-0 ring-1 ring-primary-500/30">
            <KeySquare size={18} />
          </div>
          <div class="min-w-0 flex-1">
            <div class="text-sm font-semibold text-surface-900-100">RBAC mode (granular permissions)</div>
            <p class="text-xs text-surface-500 mt-1">
              This is the capability-based system. <code class="font-mono text-[11px]">legacy</code>
              keeps the flat Admin / Operator / Viewer tiers.
              <code class="font-mono text-[11px]">granular</code> makes the
              <a href="/permissions" class="text-primary-300 hover:underline">permission grants</a>
              you create under <strong>Govern → Permissions</strong> the source of truth for
              capability checks.
            </p>
          </div>
        </div>

        <div class="max-w-xs">
          <Select label="Mode" help="settings.rbac_mode" bind:value={rbacMode} disabled={saving}>
            <option value="legacy">legacy — role tiers</option>
            <option value="granular">granular — permission grants</option>
          </Select>
        </div>

        {#if dirty && rbacMode === 'granular' && settings.rbac_mode !== 'granular'}
          <div class="inline-flex items-start gap-1.5 text-[11px] text-warning-300 bg-warning-500/10 ring-1 ring-warning-500/30 rounded px-2 py-1.5">
            <AlertTriangle size={12} class="mt-0.5 shrink-0" />
            <span>
              Switching to <span class="font-mono">granular</span> makes permission grants authoritative.
              Confirm the grants under <a href="/permissions" class="underline">Permissions</a> cover the
              access your operators need before saving.
            </span>
          </div>
        {/if}
      </div>
    </Card>

    <!--
      Import wizard tuning. Two thresholds drive the fuzzy action-name
      matcher: how high a candidate must score, and how much it must
      lead the runner-up. Defaults are conservative; admins with very
      similar action catalogues (multiple `getX` variants per integration)
      may want to loosen them, and security-critical deployments may want
      to tighten them so the matcher is always explicit.
    -->
    <Card>
      <div class="p-5 space-y-4">
        <div class="flex items-start gap-3">
          <div class="w-10 h-10 rounded-md bg-primary-500/10 text-primary-300 flex items-center justify-center shrink-0 ring-1 ring-primary-500/30">
            <Wand2 size={18} />
          </div>
          <div class="min-w-0 flex-1">
            <div class="text-sm font-semibold text-surface-900-100">Import wizard fuzzy match</div>
            <p class="text-xs text-surface-500 mt-1">
              When the importer can't find an exact <code class="font-mono text-[11px]">action_name</code>
              match, it scores every action under the same integration and auto-applies the top candidate
              if it clears <strong>both</strong> thresholds. Otherwise the node stays unresolved and the user
              picks from the editor's action dropdown.
            </p>
          </div>
        </div>

        <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
          <label class="block" for="settings-fuzzy-threshold">
            <span class="block text-[11px] uppercase tracking-wider text-surface-500 mb-1">
              Minimum score <FieldHint id="settings.import_fuzzy_threshold" />
            </span>
            <input
              id="settings-fuzzy-threshold"
              type="number"
              min="0.5"
              max="0.99"
              step="0.05"
              bind:value={fuzzyThreshold}
              disabled={saving}
              class="w-full px-3 py-1.5 text-sm bg-surface-100-900 ring-1 ring-surface-200-800 rounded-md focus:outline-none focus:ring-primary-500"
            />
            <span class="block text-[11px] text-surface-500 mt-1">
              Default <code class="font-mono">0.80</code>. Range 0.50–0.99. Lower → more
              auto-applies (also more wrong guesses).
            </span>
          </label>
          <label class="block" for="settings-fuzzy-gap">
            <span class="block text-[11px] uppercase tracking-wider text-surface-500 mb-1">
              Margin over runner-up <FieldHint id="settings.import_fuzzy_gap" />
            </span>
            <input
              id="settings-fuzzy-gap"
              type="number"
              min="0"
              max="0.5"
              step="0.05"
              bind:value={fuzzyGap}
              disabled={saving}
              class="w-full px-3 py-1.5 text-sm bg-surface-100-900 ring-1 ring-surface-200-800 rounded-md focus:outline-none focus:ring-primary-500"
            />
            <span class="block text-[11px] text-surface-500 mt-1">
              Default <code class="font-mono">0.10</code>. Range 0.00–0.50. Higher → matcher
              refuses to pick between near-ties.
            </span>
          </label>
        </div>
      </div>
    </Card>

    <div class="flex items-center justify-end gap-2 pt-2 border-t border-surface-200-800">
      <Button variant="ghost" onclick={discard} disabled={saving || !dirty}>
        Discard
      </Button>
      <Button variant="primary" icon={Save} onclick={save} loading={saving} disabled={!dirty}>
        Save changes
      </Button>
    </div>
  {/if}
</div>
