// Shared type for Lucide icon components.
//
// lucide-svelte v1.x still ships class-based `SvelteComponentTyped` types,
// which don't match the new `Component<...>` function type. We use the legacy
// `ComponentType<SvelteComponent<...>>` shape so prop typing stays accurate
// across both Svelte 4 and Svelte 5 component models.

import type { ComponentType, SvelteComponent } from 'svelte';

export type IconComponent = ComponentType<SvelteComponent<{
  size?: number | string;
  class?: string;
  color?: string;
  strokeWidth?: number | string;
}>>;
