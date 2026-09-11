// Editor-wide "unsaved changes" flag. Idempotent setters so callers can
// re-affirm the current state without triggering downstream reactivity
// loops — important because the workflow editor's snapshot-comparison
// effect calls these on every node/edge change.
class DirtyStore {
  dirty = $state(false);
  markDirty() {
    if (!this.dirty) this.dirty = true;
  }
  markClean() {
    if (this.dirty) this.dirty = false;
  }
}

export const dirtyStore = new DirtyStore();
