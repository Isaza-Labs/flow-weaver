// Badge tone for a pip install / uv environment build status, shared by the
// Packages tab and the environment multi-picker so both colour a status the
// same way.
export function installStatusTone(s: string): 'success' | 'warning' | 'error' | 'info' {
  if (s === 'ready') return 'success';
  if (s === 'failed') return 'error';
  if (s === 'building' || s === 'installing') return 'info';
  return 'warning'; // pending
}
