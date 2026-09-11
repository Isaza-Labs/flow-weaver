// Persistent state for the in-app guide / mascot.
//
//   enabled   — whether the mascot is shown at all (the small × on the
//               mascot dismisses it; users can re-enable via the sidebar)
//   panelOpen — whether the speech-bubble panel is currently visible

import { browser } from '$app/environment';

const ENABLED_KEY = 'flowweaver:guide:enabled';

function readEnabled(): boolean {
  if (!browser) return true;
  const v = localStorage.getItem(ENABLED_KEY);
  if (v === 'false') return false;
  return true; // default on for new users
}

class GuideStore {
  enabled = $state<boolean>(readEnabled());
  panelOpen = $state<boolean>(false);

  setEnabled(next: boolean) {
    this.enabled = next;
    if (!next) this.panelOpen = false;
    if (browser) {
      try {
        localStorage.setItem(ENABLED_KEY, next ? 'true' : 'false');
      } catch {}
    }
  }

  togglePanel() {
    if (!this.enabled) return;
    this.panelOpen = !this.panelOpen;
  }

  closePanel() {
    this.panelOpen = false;
  }
}

export const guideStore = new GuideStore();
