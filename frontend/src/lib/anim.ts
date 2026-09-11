import { animate, createTimeline, stagger, utils } from 'animejs';
import type { AnimationParams, TargetsParam, TimelineParams } from 'animejs';

export { animate, createTimeline, stagger, utils };
export type { AnimationParams, TargetsParam, TimelineParams };

export function prefersReducedMotion(): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return false;
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

export function safeAnimate(
  targets: TargetsParam,
  params: AnimationParams,
): ReturnType<typeof animate> | null {
  if (typeof window === 'undefined') return null;
  if (prefersReducedMotion()) {
    return animate(targets, { ...params, duration: 0, delay: 0, loop: false });
  }
  return animate(targets, params);
}
