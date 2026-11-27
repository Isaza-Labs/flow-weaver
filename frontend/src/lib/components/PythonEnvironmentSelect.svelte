<script lang="ts">
  // Picker for an admin-defined Python environment (/admin/python?tab=environments).
  // `value` is the environment id, or '' for the system default (the image's
  // python3 — what every device/snippet used before environments existed).
  // Callers map '' to null on create and to NO_PYTHON_ENVIRONMENT on update.
  import { onMount } from 'svelte';
  import {
    pythonEnvironments,
    pythonEnvironmentLabel,
    type PythonEnvironment,
  } from '$lib/api/client';
  import { Select } from '$lib/components/ui';

  let {
    value = $bindable(''),
    label = 'Python environment',
    hint,
    disabled = false,
  }: {
    value?: string;
    label?: string;
    hint?: string;
    disabled?: boolean;
  } = $props();

  let envs = $state<PythonEnvironment[]>([]);
  let loadFailed = $state(false);

  onMount(async () => {
    try {
      envs = (await pythonEnvironments.list(200, 0)).data;
    } catch {
      // No read access, or the API is down: only "System default" (and the
      // current value, below) can be offered.
      loadFailed = true;
    }
  });

  // Keep a currently-selected id visible even if it is not in the list
  // (deleted, or the list failed to load), so saving doesn't silently drop it.
  const unknownSelected = $derived(
    !!value && !envs.some((e) => e.python_environment_id === value),
  );
</script>

<Select
  {label}
  help="pyenvs.select"
  hint={hint ?? (loadFailed ? 'Could not load the environment list.' : undefined)}
  bind:value
  {disabled}
>
  <option value="">System default (python3)</option>
  {#each envs as e (e.python_environment_id)}
    <option value={e.python_environment_id}>
      {pythonEnvironmentLabel(e)}{e.is_ssh_fallback ? ' · SSH fallback' : ''}
    </option>
  {/each}
  {#if unknownSelected}
    <option value={value}>Unknown environment ({value.slice(0, 8)}…)</option>
  {/if}
</Select>
