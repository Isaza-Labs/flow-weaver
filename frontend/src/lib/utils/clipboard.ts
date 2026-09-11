/**
 * Copies text to the clipboard, working in both secure and insecure contexts.
 *
 * `navigator.clipboard` only exists in secure contexts (HTTPS or localhost),
 * so when the app is served over plain HTTP (e.g. a LAN IP) every copy button
 * used to fail. This falls back to the legacy hidden-textarea +
 * `document.execCommand('copy')` path, which still works everywhere.
 *
 * Returns true when the text made it to the clipboard.
 */
export async function copyText(text: string): Promise<boolean> {
  // Preferred path: async Clipboard API (secure contexts only).
  if (typeof navigator !== 'undefined' && navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      // Permission denied or transient failure — try the legacy path below.
    }
  }

  // Legacy path: off-screen textarea + execCommand. Deprecated but the only
  // option on insecure origins.
  try {
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.setAttribute('readonly', '');
    ta.style.position = 'fixed';
    ta.style.top = '-1000px';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    const selection = document.getSelection();
    const prevRange = selection && selection.rangeCount > 0 ? selection.getRangeAt(0) : null;
    ta.select();
    ta.setSelectionRange(0, ta.value.length); // iOS Safari needs the explicit range
    const ok = document.execCommand('copy');
    document.body.removeChild(ta);
    // Restore whatever the user had selected before we hijacked the selection.
    if (prevRange && selection) {
      selection.removeAllRanges();
      selection.addRange(prevRange);
    }
    return ok;
  } catch {
    return false;
  }
}
