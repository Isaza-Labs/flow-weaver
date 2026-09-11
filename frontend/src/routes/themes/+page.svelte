<script lang="ts">
  // Theme studio. Any user can keep private themes; an admin can publish one
  // to everybody. The list on the left, the editor on the right; saving
  // reloads the store so the picker and the running page update without a
  // reload.
  //
  // The editor covers two layers: the seven palette colours (expanded into
  // full ramps in the browser) and the optional style settings — corner
  // roundness, interface scale, fonts, heading weight — that ride along in
  // the same saved theme.
  import { onMount } from 'svelte';
  import { themes as themesApi, errorMessage, type Theme } from '$lib/api/client';
  import { authStore } from '$lib/stores/auth.svelte';
  import { themeStore } from '$lib/stores/theme.svelte';
  import {
    customThemeId, PALETTES, FONT_BODY_KEYS, FONT_HEADING_KEYS, FONT_MONO_KEYS,
    type Palette, type ThemeColors, type ThemeSettings,
  } from '$lib/theme/customTheme';
  import {
    BRAND_BASE, PALETTE_HINT, PALETTE_LABEL, newThemeColors,
    SETTING_DEFAULTS, newThemeSettings, HEADING_WEIGHTS,
    FONT_BODY_LABEL, FONT_HEADING_LABEL, FONT_MONO_LABEL,
    THEME_PRESETS, randomThemeColors,
  } from '$lib/theme/defaults';
  import { parseHex } from '$lib/theme/ramp';
  import ColorField from '$lib/theme/ColorField.svelte';
  import SettingSlider from '$lib/theme/SettingSlider.svelte';
  import ThemePreview from '$lib/theme/ThemePreview.svelte';
  import {
    PageHeader, Card, Button, Input, Textarea, Select, Alert, Spinner, EmptyState,
    ErrorState, Dialog, toast, confirm, FieldHint,
  } from '$lib/components/ui';
  import {
    Palette as PaletteIcon, Plus, Trash2, Users, Check, Sun, Moon,
    Copy, CopyPlus, ClipboardPaste, Dices,
  } from 'lucide-svelte';

  const isAdmin = $derived(authStore.session?.role === 'admin');

  let list = $state<Theme[]>([]);
  let loading = $state(true);
  let loadError = $state<unknown>(null);
  let saving = $state(false);
  let formError = $state<string | null>(null);

  // null = editing a brand-new theme; otherwise the row being edited.
  let editing = $state<Theme | null>(null);
  let name = $state('');
  let description = $state('');
  let colors = $state<ThemeColors>(newThemeColors());
  // Materialised with every default so each control has a value to bind to;
  // only the keys that differ from the defaults are saved (see save()).
  let settings = $state<Required<ThemeSettings>>({ ...SETTING_DEFAULTS });
  let isShared = $state(false);
  // The preview's own mode, independent of the app's — a theme has to be
  // checked in both, and toggling the whole app to compare is a bad trade.
  let previewMode = $state<'light' | 'dark'>('dark');

  // Import dialog.
  let importOpen = $state(false);
  let importText = $state('');
  let importError = $state<string | null>(null);

  const editable = $derived(editing === null || editing.can_edit);
  const invalidPalettes = $derived(
    PALETTES.filter((p) => colors[p] !== undefined && !parseHex(colors[p]!)),
  );
  const canSave = $derived(
    editable && name.trim().length > 0 && invalidPalettes.length === 0 && !saving,
  );

  // Only overrides travel: a slider parked on the app default means
  // "inherit", not "pin the current default forever".
  const prunedSettings = $derived(
    Object.fromEntries(
      Object.entries(settings).filter(
        ([k, v]) => v !== SETTING_DEFAULTS[k as keyof ThemeSettings],
      ),
    ) as ThemeSettings,
  );

  onMount(load);

  async function load() {
    loading = true;
    loadError = null;
    try {
      list = (await themesApi.list()).data;
    } catch (e) {
      loadError = e;
    } finally {
      loading = false;
    }
  }

  function startNew() {
    editing = null;
    name = '';
    description = '';
    colors = newThemeColors();
    settings = { ...SETTING_DEFAULTS };
    isShared = false;
    formError = null;
  }

  function edit(theme: Theme) {
    editing = theme;
    name = theme.name;
    description = theme.description ?? '';
    // Fill in any palette the saved theme left inherited, so every row has a
    // swatch to drag. Untouched rows still save — they're just brand values.
    colors = { ...BRAND_BASE, ...(theme.colors as ThemeColors) };
    settings = { ...SETTING_DEFAULTS, ...((theme.settings ?? {}) as ThemeSettings) };
    isShared = theme.is_shared;
    formError = null;
  }

  // Keeps whatever is in the form but detaches it from the saved row, so
  // saving creates a new private theme. This is how anyone makes a shared
  // theme — or one of their own — their starting point.
  function duplicateCurrent() {
    editing = null;
    if (name.trim()) name = `${name.trim()} (copy)`;
    isShared = false;
    formError = null;
    toast.success('Editing a copy — save it to create your own theme');
  }

  function duplicateTheme(theme: Theme) {
    edit(theme);
    duplicateCurrent();
  }

  function applyPreset(preset: (typeof THEME_PRESETS)[number]) {
    colors = { ...preset.colors };
    settings = { ...SETTING_DEFAULTS, ...(preset.settings ?? {}) };
  }

  function surpriseMe() {
    colors = randomThemeColors();
  }

  async function save() {
    if (!canSave) return;
    saving = true;
    formError = null;
    try {
      const body = {
        name: name.trim(),
        description: description.trim() || null,
        colors: colors as Record<string, string>,
        // Always sent: on update the map replaces wholesale, so returning
        // every slider to its default genuinely clears the overrides.
        settings: prunedSettings as Record<string, unknown>,
        is_shared: isShared,
      };
      const saved = editing
        ? await themesApi.update(editing.theme_id, body)
        : await themesApi.create(body);
      toast.success(editing ? 'Theme updated' : 'Theme created');
      await load();
      await themeStore.loadCustom();
      edit(saved);
    } catch (e) {
      formError = errorMessage(e);
    } finally {
      saving = false;
    }
  }

  async function remove(theme: Theme) {
    const ok = await confirm({
      title: `Delete "${theme.name}"?`,
      message: theme.is_shared
        ? 'This theme is shared — anyone currently using it falls back to Editorial.'
        : 'Anyone using it falls back to Editorial.',
      confirmLabel: 'Delete',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await themesApi.remove(theme.theme_id);
      toast.success('Theme deleted');
      if (editing?.theme_id === theme.theme_id) startNew();
      await load();
      await themeStore.loadCustom();
    } catch (e) {
      toast.fromError(e, "Couldn't delete the theme");
    }
  }

  // Applies a saved theme to the whole app, same as picking it in the palette
  // menu — the point of the studio is to see it for real.
  function useTheme(theme: Theme) {
    themeStore.setTheme(customThemeId(theme.theme_id));
    toast.success(`Using "${theme.name}"`);
  }

  // ─── Import / export ─────────────────────────────────────────────────
  // A theme as portable JSON — enough to paste into a chat and load on
  // another account or instance. Same shape the API speaks.

  async function exportTheme() {
    const payload: Record<string, unknown> = { name: name.trim() || 'Untitled theme', colors };
    if (description.trim()) payload.description = description.trim();
    if (Object.keys(prunedSettings).length > 0) payload.settings = prunedSettings;
    try {
      await navigator.clipboard.writeText(JSON.stringify(payload, null, 2));
      toast.success('Theme JSON copied to the clipboard');
    } catch {
      toast.error("Couldn't reach the clipboard — copy from the import dialog instead");
      importText = JSON.stringify(payload, null, 2);
      importError = null;
      importOpen = true;
    }
  }

  function runImport() {
    importError = null;
    let parsed: unknown;
    try {
      parsed = JSON.parse(importText);
    } catch {
      importError = 'Not valid JSON.';
      return;
    }
    if (typeof parsed !== 'object' || parsed === null) {
      importError = 'Expected an object like { "name": …, "colors": { … } }.';
      return;
    }
    const raw = parsed as Record<string, unknown>;

    const rawColors = raw.colors;
    if (typeof rawColors !== 'object' || rawColors === null) {
      importError = 'Missing "colors" — an object of palette → hex.';
      return;
    }
    const nextColors: ThemeColors = {};
    for (const [key, value] of Object.entries(rawColors)) {
      if (!(PALETTES as readonly string[]).includes(key)) {
        importError = `Unknown palette "${key}". Allowed: ${PALETTES.join(', ')}.`;
        return;
      }
      if (typeof value !== 'string' || !parseHex(value)) {
        importError = `colors.${key} must be a 6-digit hex like #5c69ab.`;
        return;
      }
      nextColors[key as Palette] = value.toLowerCase();
    }
    if (Object.keys(nextColors).length === 0) {
      importError = 'The "colors" object is empty.';
      return;
    }

    const nextSettings: ThemeSettings = {};
    if (raw.settings !== undefined) {
      if (typeof raw.settings !== 'object' || raw.settings === null) {
        importError = '"settings" must be an object.';
        return;
      }
      for (const [key, value] of Object.entries(raw.settings)) {
        const ok =
          (key === 'roundness' && typeof value === 'number' && value >= 0 && value <= 2) ||
          (key === 'ui_scale' && typeof value === 'number' && value >= 0.85 && value <= 1.15) ||
          (key === 'heading_weight' && typeof value === 'number' && value >= 300 && value <= 900) ||
          (key === 'font_body' && (FONT_BODY_KEYS as readonly unknown[]).includes(value)) ||
          (key === 'font_heading' && (FONT_HEADING_KEYS as readonly unknown[]).includes(value)) ||
          (key === 'font_mono' && (FONT_MONO_KEYS as readonly unknown[]).includes(value));
        if (!ok) {
          importError = `settings.${key} is not a valid setting/value.`;
          return;
        }
        (nextSettings as Record<string, unknown>)[key] = value;
      }
    }

    // Loads as a NEW theme: importing over someone's saved row by accident
    // would be far worse than having to re-open it.
    editing = null;
    name = typeof raw.name === 'string' ? raw.name.slice(0, 60) : '';
    description = typeof raw.description === 'string' ? raw.description.slice(0, 200) : '';
    colors = { ...BRAND_BASE, ...nextColors };
    settings = { ...SETTING_DEFAULTS, ...nextSettings };
    isShared = false;
    formError = null;
    importOpen = false;
    importText = '';
    toast.success('Theme loaded into the editor — save it to keep it');
  }
</script>

<svelte:head><title>Themes · FlowWeaver</title></svelte:head>

<div class="p-6 space-y-5">
  <PageHeader
    title="Themes"
    description="Build a full theme from a handful of base colours plus a few style knobs — corners, fonts, heading weight and interface scale. Each colour expands into an 11-shade palette, and the result lands in the theme picker next to the built-in ones."
  >
    {#snippet actions()}
      <Button
        variant="ghost"
        icon={ClipboardPaste}
        onclick={() => { importText = ''; importError = null; importOpen = true; }}
      >
        Import
      </Button>
      <Button variant="primary" icon={Plus} onclick={startNew}>New theme</Button>
    {/snippet}
  </PageHeader>

  {#if loading}
    <Card><div class="p-8 flex justify-center"><Spinner size="lg" /></div></Card>
  {:else if loadError}
    <Card><ErrorState error={loadError} onRetry={load} /></Card>
  {:else}
    <div class="grid grid-cols-1 lg:grid-cols-[280px_1fr] gap-4 items-start">
      <!-- ─── Saved themes ─────────────────────────────────────────── -->
      <Card>
        <div class="p-3">
          <div class="text-[11px] font-semibold uppercase tracking-[0.08em] text-surface-500 px-1 pb-2">
            Saved themes
          </div>
          {#if list.length === 0}
            <EmptyState
              icon={PaletteIcon}
              title="No themes yet"
              description="Pick some colours on the right and save your first one."
            />
          {:else}
            <div class="flex flex-col gap-1">
              {#each list as theme (theme.theme_id)}
                {@const active = themeStore.theme === customThemeId(theme.theme_id)}
                {@const selected = editing?.theme_id === theme.theme_id}
                <div
                  class="rounded-md p-2 transition-colors ring-1 {selected
                    ? 'bg-primary-500/10 ring-primary-500/30'
                    : 'ring-transparent hover:bg-surface-200-800/50'}"
                >
                  <button type="button" onclick={() => edit(theme)} class="w-full text-left cursor-pointer">
                    <div class="flex items-center gap-2">
                      <!-- Three-swatch fingerprint: enough to tell themes
                           apart in a list without rendering a full ramp. -->
                      <span class="flex shrink-0 rounded overflow-hidden ring-1 ring-surface-300-700">
                        {#each ['primary', 'secondary', 'surface'] as Palette[] as p}
                          <span class="w-3 h-4" style="background: {theme.colors[p] ?? BRAND_BASE[p]}"></span>
                        {/each}
                      </span>
                      <span class="text-xs font-semibold text-surface-900-100 truncate flex-1">{theme.name}</span>
                      {#if theme.is_shared}
                        <Users size={11} class="text-surface-500 shrink-0" />
                      {/if}
                    </div>
                    {#if theme.description}
                      <div class="text-[10px] text-surface-500 mt-1 line-clamp-2">{theme.description}</div>
                    {/if}
                  </button>
                  <div class="flex items-center gap-1 mt-1.5">
                    <button
                      type="button"
                      onclick={() => useTheme(theme)}
                      disabled={active}
                      aria-label={active ? `Using ${theme.name}` : `Use ${theme.name}`}
                      class="text-[10px] px-1.5 py-0.5 rounded inline-flex items-center gap-1 transition-colors cursor-pointer
                        {active
                          ? 'text-primary-600-300 pointer-events-none'
                          : 'text-surface-500 hover:text-surface-900-100 hover:bg-surface-200-800/60'}"
                    >
                      {#if active}<Check size={10} /> In use{:else}Use{/if}
                    </button>
                    <button
                      type="button"
                      onclick={() => duplicateTheme(theme)}
                      aria-label="Duplicate {theme.name}"
                      title="Load a copy into the editor"
                      class="text-[10px] px-1.5 py-0.5 rounded text-surface-500 hover:text-surface-900-100 hover:bg-surface-200-800/60 transition-colors cursor-pointer"
                    >
                      <CopyPlus size={11} />
                    </button>
                    {#if theme.can_edit}
                      <button
                        type="button"
                        onclick={() => remove(theme)}
                        aria-label="Delete {theme.name}"
                        class="ml-auto text-[10px] px-1.5 py-0.5 rounded text-surface-500 hover:text-error-400 hover:bg-error-500/10 transition-colors cursor-pointer"
                      >
                        <Trash2 size={11} />
                      </button>
                    {/if}
                  </div>
                </div>
              {/each}
            </div>
          {/if}
        </div>
      </Card>

      <!-- ─── Editor ───────────────────────────────────────────────── -->
      <div class="space-y-4">
        {#if !editable}
          <Alert tone="info">
            <div class="flex flex-wrap items-center gap-2">
              <span>
                This is a shared theme published by someone else. You can use it, but
                only an admin can change it.
              </span>
              <Button variant="ghost" size="sm" icon={CopyPlus} onclick={duplicateCurrent}>
                Save a copy
              </Button>
            </div>
          </Alert>
        {/if}
        {#if formError}
          <Alert tone="error">{formError}</Alert>
        {/if}

        <Card>
          <div class="p-4 grid grid-cols-1 xl:grid-cols-2 gap-5">
            <div class="space-y-3">
              <Input
                label="Name"
                help="themes.name"
                bind:value={name}
                disabled={!editable}
                placeholder="Midnight ops"
              />
              <Textarea
                label="Description"
                help="themes.description"
                bind:value={description}
                disabled={!editable}
                rows={2}
                placeholder="Shown under the name in the theme picker."
              />

              {#if editable}
                <div>
                  <div class="text-[11px] font-medium text-surface-600-400 inline-flex items-center gap-1 pb-1.5">
                    Start from <FieldHint id="themes.start_from" />
                  </div>
                  <div class="flex flex-wrap gap-1.5">
                    {#each THEME_PRESETS as preset (preset.key)}
                      <button
                        type="button"
                        onclick={() => applyPreset(preset)}
                        title={preset.hint}
                        class="inline-flex items-center gap-1.5 pl-1.5 pr-2 py-1 rounded-md ring-1 ring-surface-300-700 text-[11px] text-surface-700-300 hover:bg-surface-200-800/60 hover:text-surface-900-100 transition-colors cursor-pointer"
                      >
                        <span class="flex rounded-sm overflow-hidden">
                          {#each ['primary', 'secondary', 'surface'] as Palette[] as p}
                            <span class="w-2 h-3" style="background: {preset.colors[p]}"></span>
                          {/each}
                        </span>
                        {preset.label}
                      </button>
                    {/each}
                    <button
                      type="button"
                      onclick={surpriseMe}
                      title="Roll a random — but coherent — palette"
                      class="inline-flex items-center gap-1.5 px-2 py-1 rounded-md ring-1 ring-surface-300-700 text-[11px] text-surface-700-300 hover:bg-surface-200-800/60 hover:text-surface-900-100 transition-colors cursor-pointer"
                    >
                      <Dices size={12} /> Surprise me
                    </button>
                  </div>
                </div>
              {/if}

              <div class="border-t border-surface-200-800 pt-1 divide-y divide-surface-200-800/60">
                {#each PALETTES as palette (palette)}
                  <ColorField
                    label={PALETTE_LABEL[palette]}
                    hint={PALETTE_HINT[palette]}
                    fallback={BRAND_BASE[palette]}
                    surface={palette === 'surface'}
                    bind:value={
                      () => colors[palette] ?? BRAND_BASE[palette],
                      (v) => (colors = { ...colors, [palette]: v })
                    }
                  />
                {/each}
              </div>

              <!-- ─── Style ─────────────────────────────────────────── -->
              <div class="border-t border-surface-200-800 pt-2">
                <div class="text-[11px] font-semibold uppercase tracking-[0.08em] text-surface-500 inline-flex items-center gap-1 pb-1">
                  Style <FieldHint id="themes.style" />
                </div>
                <div class="divide-y divide-surface-200-800/60">
                  <SettingSlider
                    label="Corner roundness"
                    hint="Scales every radius — 0 is square, 2 is very round."
                    min={0}
                    max={2}
                    step={0.05}
                    fallback={SETTING_DEFAULTS.roundness}
                    format={(v) => `${v.toFixed(2)}×`}
                    disabled={!editable}
                    bind:value={settings.roundness}
                  />
                  <SettingSlider
                    label="Interface scale"
                    hint="Grows or shrinks the whole app, text and spacing alike."
                    min={0.85}
                    max={1.15}
                    step={0.01}
                    fallback={SETTING_DEFAULTS.ui_scale}
                    format={(v) => `${Math.round(v * 100)}%`}
                    disabled={!editable}
                    bind:value={settings.ui_scale}
                  />
                </div>
                <div class="grid grid-cols-2 gap-3 pt-2.5">
                  <Select label="Body font" help="themes.fonts" disabled={!editable} bind:value={settings.font_body}>
                    {#each FONT_BODY_KEYS as key (key)}
                      <option value={key}>{FONT_BODY_LABEL[key]}</option>
                    {/each}
                  </Select>
                  <Select label="Heading font" help="themes.font_heading" disabled={!editable} bind:value={settings.font_heading}>
                    {#each FONT_HEADING_KEYS as key (key)}
                      <option value={key}>{FONT_HEADING_LABEL[key]}</option>
                    {/each}
                  </Select>
                  <Select label="Heading weight" help="themes.heading_weight" disabled={!editable} bind:value={settings.heading_weight}>
                    {#each HEADING_WEIGHTS as weight (weight)}
                      <option value={weight}>{weight}{weight === SETTING_DEFAULTS.heading_weight ? ' (default)' : ''}</option>
                    {/each}
                  </Select>
                  <Select label="Code font" help="themes.font_mono" disabled={!editable} bind:value={settings.font_mono}>
                    {#each FONT_MONO_KEYS as key (key)}
                      <option value={key}>{FONT_MONO_LABEL[key]}</option>
                    {/each}
                  </Select>
                </div>
              </div>

              {#if isAdmin}
                <label class="flex items-start gap-2 pt-2 cursor-pointer">
                  <input type="checkbox" bind:checked={isShared} disabled={!editable} class="mt-0.5" />
                  <span class="text-xs text-surface-700-300">
                    <span class="font-medium text-surface-900-100 inline-flex items-center gap-1">
                      Share with everyone <FieldHint id="themes.share" />
                    </span>
                    <span class="block text-[11px] text-surface-500">
                      Puts this theme in every user's picker. Admin-only.
                    </span>
                  </span>
                </label>
              {/if}
            </div>

            <div class="space-y-2">
              <div class="flex items-center justify-between">
                <span class="text-[11px] font-semibold uppercase tracking-[0.08em] text-surface-500">
                  Preview
                </span>
                <div class="flex items-center rounded-md ring-1 ring-surface-300-700 overflow-hidden">
                  {#each [{ m: 'light' as const, icon: Sun }, { m: 'dark' as const, icon: Moon }] as opt}
                    <button
                      type="button"
                      onclick={() => (previewMode = opt.m)}
                      aria-label="Preview in {opt.m} mode"
                      aria-pressed={previewMode === opt.m}
                      class="px-2 py-1 transition-colors cursor-pointer {previewMode === opt.m
                        ? 'bg-primary-500/15 text-primary-600-300'
                        : 'text-surface-500 hover:text-surface-900-100'}"
                    >
                      <opt.icon size={12} />
                    </button>
                  {/each}
                </div>
              </div>
              <ThemePreview {colors} settings={prunedSettings} mode={previewMode} />
              <p class="text-[10px] text-surface-500">
                Both modes come from the same colours — the ramp is shared and the
                light/dark pairs flip, exactly like the built-in themes. Interface
                scale applies to the real app, not this miniature.
              </p>
            </div>
          </div>

          <div class="px-4 py-3 border-t border-surface-200-800 flex flex-wrap items-center gap-2">
            <Button variant="primary" onclick={save} disabled={!canSave} loading={saving}>
              {editing ? 'Save changes' : 'Create theme'}
            </Button>
            {#if editing}
              <Button variant="ghost" icon={CopyPlus} onclick={duplicateCurrent}>Duplicate</Button>
              <Button variant="ghost" onclick={startNew}>New theme</Button>
            {/if}
            <Button variant="ghost" icon={Copy} onclick={exportTheme}>Export JSON</Button>
            {#if invalidPalettes.length > 0}
              <span class="text-[11px] text-error-400">
                Fix the hex value for {invalidPalettes.map((p) => PALETTE_LABEL[p]).join(', ')}.
              </span>
            {/if}
          </div>
        </Card>
      </div>
    </div>
  {/if}
</div>

<!-- ─── Import dialog ──────────────────────────────────────────────── -->
<Dialog
  bind:open={importOpen}
  title="Import a theme"
  description="Paste theme JSON — the same shape Export produces. It loads into the editor as a new theme; nothing is saved until you save."
>
  <div class="space-y-2">
    <textarea
      bind:value={importText}
      rows={10}
      spellcheck="false"
      placeholder={'{\n  "name": "Midnight ops",\n  "colors": { "primary": "#6d8dff", "surface": "#252a3d" },\n  "settings": { "roundness": 1.5 }\n}'}
      class="w-full font-mono text-[11px] px-2.5 py-2 rounded-md bg-surface-50-950 border border-surface-300-700 text-surface-900-100 focus:outline-none focus:ring-2 focus:ring-primary-500/30"
    ></textarea>
    {#if importError}
      <p class="text-xs text-error-400">{importError}</p>
    {/if}
  </div>
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (importOpen = false)}>Cancel</Button>
    <Button variant="primary" onclick={runImport} disabled={!importText.trim()}>
      Load into editor
    </Button>
  {/snippet}
</Dialog>
