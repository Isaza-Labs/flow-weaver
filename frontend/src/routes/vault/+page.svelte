<script lang="ts">
  // The Vault: every stored login, key, token and secret value on one screen.
  //
  // It replaces the separate Credentials (/credentials) and Secrets
  // (/admin/secrets) pages. Those were two lists for one question — "where do I
  // keep this sensitive thing?" — and the answer depended on knowing that a
  // credential is auth material devices, Git remotes and integrations link to by
  // id, while a secret is a named value an OpenAPI spec points at. Here the user
  // picks what they are storing and the page talks to the right store. Both stores
  // and both APIs are exactly what they were, so everything that already links to
  // a credential or references a secret keeps working.
  //
  // Who sees what is unchanged too: credentials are for anyone with
  // credential.read (the server says no to anyone else, and the page shows that as
  // a store that could not be read); secret values are admin-only, so for anyone
  // else that store, its type and its rows are simply not here.
  //
  // Nothing sensitive is ever shown: both APIs are write-only for secret material.

  import { onDestroy, onMount, tick, untrack } from 'svelte';
  import { afterNavigate, beforeNavigate, goto } from '$app/navigation';
  import { page } from '$app/state';
  import {
    credentials as credentialsApi,
    secrets as secretsApi,
    devices as devicesApi,
    git as gitApi,
    errorMessage,
    isOfflineError,
    type Credential,
    type Secret,
  } from '$lib/api/client';
  import {
    PageHeader, Card, DataTable, Button, IconButton, Input, Textarea, Select,
    Dialog, Alert, Spinner, EmptyState, SearchInput, StatusBadge, Badge,
    formatDateTime, toast, confirm,
  } from '$lib/components/ui';
  import { copyText } from '$lib/utils/clipboard';
  import { authStore } from '$lib/stores/auth.svelte';
  import {
    VAULT_KINDS, kindInfo, kindNoun, kindOfCredential, defaultTypeOf, isVaultKind, type VaultKind,
  } from '$lib/vault/kinds';
  import { Vault, Plus, Pencil, Trash2, RefreshCw, Copy, ChevronLeft } from 'lucide-svelte';

  // One row of the list, whichever store it came from.
  type Entry = {
    key: string;
    kind: VaultKind;
    name: string;
    usedFor: string;
    detail: string;
    updatedAt: string;
    credential: Credential | null;
    secret: Secret | null;
  };

  // Secret values are admin-only, as /admin/secrets was. Credentials are for
  // everyone who could open /credentials; the server enforces credential.read.
  const showSecrets = $derived(authStore.session?.role === 'admin');
  const kinds = $derived(VAULT_KINDS.filter((k) => k.kind !== 'secret' || showSecrets));
  const kindAllowed = (k: VaultKind) => k !== 'secret' || showSecrets;

  // Each store loads, fails and reports on its own: a Secrets outage must not
  // take credential administration down with it, a slow credential read must not
  // hold back secrets that already arrived, and a store that failed must never
  // look empty.
  let credentials = $state<Credential[]>([]);
  let secrets = $state<Secret[]>([]);
  let credentialsLoading = $state(true);
  let secretsLoading = $state(true);
  let credentialsError = $state<unknown>(null);
  let secretsError = $state<unknown>(null);
  // True when the credential list could not be read as one consistent snapshot
  // (it changed while being paged, or is larger than the page bound). The rows
  // shown are real; "all of them" is not a claim the page can make.
  let credentialsIncomplete = $state(false);
  // Whether each store has been read successfully at least once. Rows can exist
  // before that (an entry created while the first read is still out), so "known
  // in full" cannot be judged from the rows alone.
  let credentialsLoaded = $state(false);
  let secretsLoaded = $state(false);

  let search = $state('');

  // The type filter lives in the URL (?type=<kind>), so a link, the browser's
  // Back button and the chips all agree. A missing, unknown or not-permitted type
  // reads as All.
  const filter = $derived.by<'all' | VaultKind>(() => {
    const type = page.url.searchParams.get('type');
    return isVaultKind(type) && kinds.some((k) => k.kind === type) ? type : 'all';
  });

  function vaultUrl(change: (query: URLSearchParams) => void): string {
    const query = new URLSearchParams(page.url.search);
    change(query);
    const rest = query.toString();
    return `${page.url.pathname}${rest ? `?${rest}` : ''}`;
  }

  const QUIET = { replaceState: true, keepFocus: true, noScroll: true } as const;

  function setFilter(next: 'all' | VaultKind) {
    if (next === filter) return;
    goto(vaultUrl((q) => (next === 'all' ? q.delete('type') : q.set('type', next))), QUIET);
  }

  const entries = $derived<Entry[]>([
    ...credentials.map((c) => ({
      key: `credential:${c.credential_id}`,
      kind: kindOfCredential(c),
      name: c.name,
      usedFor: c.type ?? '',
      detail: c.username ?? '',
      updatedAt: c.updated_at,
      credential: c,
      secret: null,
    })),
    ...(showSecrets ? secrets : []).map((s) => ({
      key: `secret:${s.secret_id}`,
      kind: 'secret' as VaultKind,
      name: s.name,
      usedFor: '',
      detail: s.description ?? '',
      updatedAt: s.updated_at,
      credential: null,
      secret: s,
    })),
  ]);

  // What is known about a store: 'ok' (read in full), 'partial' (rows are real
  // but may not be all of them) or 'unknown' (failed or still loading with
  // nothing to show).
  type StoreState = 'ok' | 'partial' | 'unknown';
  const credentialsState = $derived<StoreState>(
    credentialsLoaded && !credentialsError && !credentialsIncomplete
      ? 'ok'
      : credentials.length
        ? 'partial'
        : 'unknown',
  );
  const secretsState = $derived<StoreState>(
    !showSecrets ? 'ok' : secretsLoaded && !secretsError ? 'ok' : secrets.length ? 'partial' : 'unknown',
  );

  function stateFor(chip: 'all' | VaultKind): StoreState {
    if (chip === 'secret') return secretsState;
    if (chip !== 'all') return credentialsState;
    if (credentialsState === 'ok' && secretsState === 'ok') return 'ok';
    return credentialsState === 'unknown' && secretsState === 'unknown' ? 'unknown' : 'partial';
  }

  // A count is a claim about a whole store. When the store is not known in full
  // the count says so ("12+", "?") instead of looking exact.
  function countFor(chip: 'all' | VaultKind): string {
    const n = chip === 'all' ? entries.length : entries.filter((e) => e.kind === chip).length;
    const state = stateFor(chip);
    return state === 'ok' ? String(n) : state === 'partial' || n ? `${n}+` : '?';
  }

  const visible = $derived.by(() => {
    const q = search.trim().toLowerCase();
    return entries
      .filter((e) => filter === 'all' || e.kind === filter)
      .filter(
        (e) =>
          !q ||
          e.name.toLowerCase().includes(q) ||
          e.detail.toLowerCase().includes(q) ||
          e.usedFor.toLowerCase().includes(q),
      )
      .sort((a, b) => a.name.localeCompare(b.name));
  });

  // The stores the current filter looks at.
  const selected = $derived({
    credentials: filter !== 'secret',
    secrets: showSecrets && (filter === 'all' || filter === 'secret'),
  });
  const selectedLoading = $derived(
    (selected.credentials && credentialsLoading && !credentialsLoaded) ||
      (selected.secrets && secretsLoading && !secretsLoaded),
  );

  // What an empty table means depends on why it is empty, and only for the
  // stores the filter is showing. "Nothing matches" and "the Vault is empty" are
  // claims about everything, made only when everything has been read.
  const emptyText = $derived.by(() => {
    const state = stateFor(filter);
    const searching = !!search.trim();
    const anyForFilter = entries.some((e) => filter === 'all' || e.kind === filter);
    if (state !== 'ok') {
      return searching || anyForFilter
        ? 'No matches among the entries that loaded; some could not be read.'
        : 'These entries could not be loaded — see the message above.';
    }
    if (searching && anyForFilter) return 'Nothing matches this search.';
    if (filter !== 'all') return `No ${kindNoun(filter)} entries yet.`;
    return 'The Vault is empty — add the logins, keys and tokens devices, Git remotes and integrations need.';
  });

  // ── Loading ─────────────────────────────────────────────────────────────

  // The server caps a page at 200 rows. Search and the type counts are claims
  // about the whole store, so every page of credential metadata is read (the rows
  // are small and hold nothing secret) — and the read is checked: rows are keyed
  // by id so a row that moved between pages is never listed twice, and a read
  // during which the total changed, a row repeated or the page bound ran out is
  // retried, then reported as incomplete rather than passed off as the whole list.
  const PAGE = 200;
  const MAX_PAGES = 50;
  const READ_ATTEMPTS = 3;

  async function readAllCredentials(): Promise<{ items: Credential[]; complete: boolean }> {
    let last: Credential[] = [];
    for (let attempt = 0; attempt < READ_ATTEMPTS; attempt++) {
      const byId = new Map<string, Credential>();
      let offset = 0;
      let total = -1;
      let stable = true;
      let finished = false;
      for (let i = 0; i < MAX_PAGES; i++) {
        // Left mid-read: the remaining pages are not asked for.
        if (gone) return { items: last, complete: false };
        const res = await credentialsApi.list(PAGE, offset);
        if (total === -1) total = res.total;
        else if (res.total !== total) stable = false;
        for (const c of res.data) {
          if (byId.has(c.credential_id)) stable = false;
          byId.set(c.credential_id, c);
        }
        offset += res.data.length;
        if (res.data.length === 0 || offset >= res.total) {
          finished = true;
          break;
        }
      }
      last = [...byId.values()];
      // More rows than the bound allows: reading again would not help.
      if (!finished) return { items: last, complete: false };
      if (stable && byId.size === total) return { items: last, complete: true };
    }
    return { items: last, complete: false };
  }

  // Set when the page is torn down. From then on nothing is read, a write that
  // has not been sent yet is not sent, reads still in the air are discarded, and
  // anything the user has to be told goes to a toast, because the form it
  // belonged to is no longer on screen.
  let gone = false;
  // Each guards against an older, slower read overwriting a newer one.
  let credentialsGeneration = 0;
  let secretsGeneration = 0;

  async function loadCredentials() {
    if (gone) return;
    const generation = ++credentialsGeneration;
    credentialsLoading = true;
    try {
      const read = await readAllCredentials();
      if (generation !== credentialsGeneration) return;
      credentials = read.items;
      credentialsIncomplete = !read.complete;
      credentialsLoaded = true;
      credentialsError = null;
    } catch (e) {
      if (generation !== credentialsGeneration) return;
      credentialsError = e;
    } finally {
      if (generation === credentialsGeneration) credentialsLoading = false;
    }
  }

  async function loadSecrets() {
    if (gone) return;
    const generation = ++secretsGeneration;
    if (!showSecrets) {
      secrets = [];
      secretsError = null;
      secretsLoaded = false;
      secretsLoading = false;
      return;
    }
    secretsLoading = true;
    try {
      const rows = await secretsApi.list();
      if (generation !== secretsGeneration) return;
      secrets = rows;
      secretsLoaded = true;
      secretsError = null;
    } catch (e) {
      if (generation !== secretsGeneration) return;
      secretsError = e;
    } finally {
      if (generation === secretsGeneration) secretsLoading = false;
    }
  }

  // Credentials, then the known links that describe them. Every re-read of the
  // credential store goes through here, so a write's links are never left stale.
  function reloadCredentials() {
    return loadCredentials().then(loadLinks);
  }

  // Reads both stores side by side; neither waits for the other.
  function load() {
    void reloadCredentials();
    void loadSecrets();
  }

  // ── Known links ─────────────────────────────────────────────────────────
  // Where a credential is linked by id: devices and Git repositories. It is a
  // hint, not an inventory. Only the first page of each list is read (reading
  // whole inventories to decorate a column is not worth it), either may be out of
  // reach, and nothing here sees a workflow step or an integration that names a
  // credential, or a ${secret:…} reference inside a spec. So the column reports
  // what was found, says when it looked, and never says "unused".

  type LinkSource = { what: string; plural: string; state: 'complete' | 'partial' | 'failed' };
  let links = $state<Record<string, string[]>>({});
  let linkSources = $state<LinkSource[]>([]);
  let linksCheckedAt = $state<Date | null>(null);
  let linksGeneration = 0;

  const clock = (d: Date) => d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

  async function loadLinks() {
    if (gone) return;
    const generation = ++linksGeneration;
    // Nothing to link to: do not read two lists to learn that.
    if (credentials.length === 0) {
      links = {};
      linkSources = [];
      linksCheckedAt = null;
      return;
    }
    const [devices, repos] = await Promise.allSettled([devicesApi.list(PAGE), gitApi.list(PAGE)]);
    if (generation !== linksGeneration) return;

    const found: Record<string, Record<string, number>> = {};
    const add = (id: string | null | undefined, what: string) => {
      if (!id) return;
      (found[id] ??= {})[what] = (found[id][what] ?? 0) + 1;
    };
    const sources: LinkSource[] = [];
    const read = <T,>(
      result: PromiseSettledResult<{ total: number; data: T[] }>,
      what: string,
      plural: string,
      idOf: (item: T) => string | null | undefined,
    ) => {
      if (result.status !== 'fulfilled') {
        sources.push({ what, plural, state: 'failed' });
        return;
      }
      for (const item of result.value.data) add(idOf(item), what);
      sources.push({
        what,
        plural,
        state: result.value.total > result.value.data.length ? 'partial' : 'complete',
      });
    };
    read(devices, 'device', 'devices', (d) => d.credential_id);
    read(repos, 'repository', 'repositories', (r) => r.auth_credential_id);

    const pluralOf = Object.fromEntries(sources.map((src) => [src.what, src.plural]));
    const out: Record<string, string[]> = {};
    for (const [id, byKind] of Object.entries(found))
      out[id] = Object.entries(byKind).map(([what, n]) => `${n} ${n === 1 ? what : pluralOf[what]}`);
    links = out;
    linkSources = sources;
    linksCheckedAt = new Date();
  }

  // One sentence under the table saying what the column covers, what it does
  // not, and when it was read.
  const linksNote = $derived.by(() => {
    if (!linkSources.length || !linksCheckedAt) return '';
    const read = linkSources.filter((s) => s.state !== 'failed').map((s) => s.plural);
    const partial = linkSources.filter((s) => s.state === 'partial').map((s) => s.plural);
    const failed = linkSources.filter((s) => s.state === 'failed').map((s) => s.plural);
    const parts: string[] = [];
    parts.push(
      read.length
        ? `Known links were read from ${read.join(' and ')} at ${clock(linksCheckedAt)}; Refresh to update.`
        : 'Known links could not be read.',
    );
    if (partial.length) parts.push(`Only the first ${PAGE} ${partial.join(' and ')} were checked.`);
    if (failed.length) parts.push(`Not available here: ${failed.join(', ')}.`);
    parts.push('Workflow steps, integrations and ${secret:…} references in specs are not counted, so an empty cell does not mean unused.');
    return parts.join(' ');
  });

  // Loaded once, on arrival. Not an effect: the API client reads the auth token,
  // and an effect would re-run the whole load every time the token refreshes.
  onMount(() => {
    load();
    consumeUrl();
  });

  // The session can change while the Vault is open (a logout and a login as
  // someone else in another tab). When secret values stop being this user's to
  // see, their rows, errors and pending reads are dropped and a form for one is
  // closed; when they become visible they are loaded.
  let seenAdmin: boolean | null = null;
  $effect(() => {
    const admin = showSecrets;
    untrack(() => {
      if (seenAdmin === null || admin === seenAdmin) {
        seenAdmin = admin;
        return;
      }
      seenAdmin = admin;
      if (modalOpen && step === 'form' && !kindAllowed(kind)) closeForm();
      void loadSecrets();
      consumeUrl(true);
    });
  });

  // ── The entry form ──────────────────────────────────────────────────────
  // Step 1 picks the kind (create only); step 2 is that kind's fields.

  let modalOpen = $state(false);
  let step = $state<'pick' | 'form'>('pick');
  let kind = $state<VaultKind>('login');
  let editing = $state<Entry | null>(null);
  // The form whose save is in flight, if any. Scoped to that form: another form
  // opened meanwhile is not disabled by a request it never made.
  let savingForm = $state<number | null>(null);
  let formError = $state('');
  // Identifies the open form, so a late result is applied to the form it came
  // from — or reported as a toast if that form has since been closed or replaced.
  let formId = $state(0);
  const saving = $derived(savingForm !== null && savingForm === formId);

  let fName = $state('');
  // "Used for": one of the kind's known types, or 'custom' with fCustomType.
  let fUsedFor = $state('');
  let fCustomType = $state('');
  let fUsername = $state('');
  let fPassword = $state('');
  let fKey = $state('');
  let fPassphrase = $state('');
  let fDescription = $state('');
  let fValue = $state('');

  // A stored credential can change from one kind to another (password to key,
  // say), as it always could. It cannot become a secret value or the other way
  // round: those are different stores with different references.
  const credentialKinds = VAULT_KINDS.filter((k) => k.authMethod);

  // Only logins and SSH keys choose what they are used for: for the other kinds
  // the type *is* the kind (a git_token row is a Git token), so it is fixed.
  const choosesType = (k: VaultKind) => (kindInfo(k).types?.length ?? 0) > 1;

  // The type the form will store.
  const effectiveType = $derived(
    !choosesType(kind) ? defaultTypeOf(kind) : fUsedFor === 'custom' ? fCustomType.trim() : fUsedFor,
  );

  // Mirrors the backend rule for secret names so bad input is flagged before a round-trip.
  const SECRET_NAME_RE = /^[a-z0-9][a-z0-9_-]{0,62}[a-z0-9]$/;

  // Sensitive material typed into the form. Cleared whenever it is no longer
  // needed — the form closed, saved, or switched to a different kind — so nothing
  // typed for one kind is submitted with another or lingers in memory.
  function clearMaterial() {
    fPassword = '';
    fKey = '';
    fPassphrase = '';
    fValue = '';
  }

  function resetFields() {
    clearMaterial();
    fName = '';
    fUsedFor = '';
    fCustomType = '';
    fUsername = '';
    fDescription = '';
    formError = '';
  }

  // Sets "Used for" from a stored type: a type the kind knows is selected, any
  // other is shown as custom so it is kept exactly as stored.
  function applyType(k: VaultKind, type: string) {
    const known = kindInfo(k).types ?? [];
    if (known.includes(type)) {
      fUsedFor = type;
      fCustomType = '';
    } else {
      fUsedFor = 'custom';
      fCustomType = type;
    }
  }

  // When the kind changes, the type follows unless it still reads back as the new
  // kind: a login used for "netconf" that becomes an SSH key stays "netconf", but
  // one that becomes a Git token must be stored as git_token, or it would read
  // back as a login.
  function retargetType(next: VaultKind) {
    const current = effectiveType;
    const method = kindInfo(next).authMethod;
    if (!method) return;
    if (current && kindOfCredential({ auth_method: method, type: current }) === next && choosesType(next)) {
      applyType(next, current);
    } else {
      applyType(next, defaultTypeOf(next));
    }
  }

  const FORM = '[data-vault-form]';

  async function focusFirstField() {
    await tick();
    document
      .querySelector<HTMLElement>(`${FORM} input:not([disabled]), ${FORM} textarea:not([disabled])`)
      ?.focus();
  }

  function openCreate(preset?: VaultKind) {
    formId++;
    editing = null;
    resetFields();
    const allowed = preset && kindAllowed(preset) ? preset : undefined;
    if (allowed) {
      kind = allowed;
      formKind = allowed;
      applyType(allowed, defaultTypeOf(allowed));
      step = 'form';
      focusFirstField();
    } else {
      step = 'pick';
    }
    modalOpen = true;
  }

  // Choosing a type from the picker. Coming back to the type that was already
  // being filled in keeps the draft; choosing a different one drops what was
  // typed for the old one, and its "Used for" follows unless the user chose one.
  function pick(k: VaultKind) {
    if (k !== kind) {
      clearMaterial();
      if (!fUsedFor || effectiveType === defaultTypeOf(kind)) applyType(k, defaultTypeOf(k));
      else retargetType(k);
    } else if (!fUsedFor) {
      applyType(k, defaultTypeOf(k));
    }
    kind = k;
    formKind = k;
    formError = '';
    step = 'form';
    focusFirstField();
  }

  async function backToTypes() {
    step = 'pick';
    await tick();
    document.querySelector<HTMLElement>(`${FORM} [data-kind="${kind}"]`)?.focus();
  }

  function openEdit(e: Entry) {
    formId++;
    editing = e;
    resetFields();
    kind = e.kind;
    formKind = e.kind;
    step = 'form';
    fName = e.name;
    if (e.credential) {
      applyType(e.kind, e.credential.type ?? '');
      fUsername = e.credential.username ?? '';
    } else if (e.secret) {
      fDescription = e.secret.description ?? '';
    }
    modalOpen = true;
  }

  // The Type selector on the edit form changes `kind` directly. Whatever was
  // typed for the previous kind is dropped, so a password typed for a login is
  // not sent along when the entry is saved as an SSH key, and the stored type
  // follows the kind. Material already stored on the server is untouched.
  let formKind: VaultKind = 'login';
  $effect(() => {
    const current = kind;
    untrack(() => {
      if (current === formKind) return;
      formKind = current;
      clearMaterial();
      if (editing) retargetType(current);
    });
  });

  // Closing the form by any route — Cancel, X, Escape, the backdrop or a save —
  // drops what was typed.
  function onFormClosed() {
    formId++;
    resetFields();
  }

  function closeForm() {
    modalOpen = false;
    onFormClosed();
  }

  // Client-side mirror of the server's per-kind material rules. On edit, a key
  // already stored satisfies the requirement. Whether a password is stored the
  // API does not say, so one is required on create — and when an SSH key becomes
  // a password kind: the server accepts that switch without one (it validates
  // only keys), and the entry would then fail at connect time while looking fine.
  function validate(): string {
    if (kind === 'secret') {
      if (!SECRET_NAME_RE.test(fName.trim()))
        return 'Name must be 2–64 chars, lowercase letters/digits/hyphens/underscores.';
      if (!editing && !fValue) return 'A value is required when creating a secret value.';
      return '';
    }
    if (!fName.trim()) return 'Name is required.';
    if (!effectiveType) return 'Say what this credential is used for.';
    if (kindInfo(kind).authMethod === 'key') {
      if (!fKey && !editing?.credential?.has_private_key)
        return editing ? 'A private key is required when switching to an SSH key.' : 'An SSH key entry requires a private key.';
    } else if (!fPassword) {
      if (!editing) return `${secretLabel(kind)} is required.`;
      if (String(editing.credential?.auth_method ?? '').trim().toLowerCase() === 'key')
        return `${secretLabel(kind)} is required when an SSH key becomes a ${kindNoun(kind)}.`;
    }
    return '';
  }

  // A label as it reads mid-sentence; acronyms keep their capitals ("API key value").
  const inSentence = (label: string) => (/^[A-Z][a-z]/.test(label) ? label[0].toLowerCase() + label.slice(1) : label);

  // What the password field holds for each kind.
  function secretLabel(k: VaultKind): string {
    switch (k) {
      case 'snmp':
        return 'Passphrase';
      case 'git_token':
        return 'Token';
      case 'api_key':
        return 'API key value';
      default:
        return 'Password';
    }
  }

  // What a successful write returned goes straight into the list: a refresh that
  // then fails must not bring back a deleted row or hide a change that was made.
  //
  // Writes go out one at a time, in the order they were made. That is what keeps
  // overlapping changes honest without any bookkeeping about which answer is
  // newer: an edit left waiting, then a delete of the same entry, reach the
  // server and are applied here in that order, so the delete has the last word;
  // a second edit that fails cannot erase a first that succeeded. Applying a
  // change also retires any read in flight for that store, since that read began
  // before the change and would overwrite it.
  let writes: Promise<unknown> = Promise.resolve();

  // A write runs only after the one before it has settled. So that one stalled
  // reply cannot hold every later change forever, each write has a deadline. The
  // API client's own timeout (30 s) covers a request until its headers arrive; a
  // body that then never finishes, or a session refresh that hangs, is not covered
  // by it. Past the deadline the write is settled as unknown and its answer,
  // should it ever come, is not applied — a late answer cannot overwrite whatever
  // came after it here. The server, though, may still apply it after a later
  // write, so the store is re-read at the deadline and again if a late answer
  // arrives.
  //
  // `stillWanted` is asked at the moment the write's turn comes: while it waited
  // the form it came from may have been closed, or its store hidden from this
  // user. A write that is no longer wanted is not sent at all, which is different
  // from one already on the wire, which is always seen through. `left` marks a
  // write withdrawn because the page itself was left, which — unlike a form the
  // user closed — is said out loud: they pressed Save or Delete and it did not
  // happen.
  class Withdrawn extends Error {
    left: boolean;
    constructor(left = false) {
      super();
      this.left = left;
    }
  }
  class Unanswered extends Error {
    constructor() {
      super('The server did not finish answering. The change may or may not have been saved — check the list.');
    }
  }
  const WRITE_DEADLINE_MS = 45_000;

  // A write the client itself gave up on — its timeout passed before any answer
  // began, or the connection failed — is in the same position as one past the
  // deadline: it was sent, and the server may have made the change before the
  // answer was lost. So it is reported the same way and the store is re-read,
  // rather than the user being told it failed and invited to send it again.
  const NO_ANSWER = 'No answer came back from the server. The change may or may not have been saved — check the list.';
  const outcomeUnknown = (e: unknown) => e instanceof Unanswered || isOfflineError(e);
  const outcomeUnknownText = (e: unknown) => (e instanceof Unanswered ? e.message : NO_ANSWER);

  // `onLate` runs if the operation settles after its deadline has already
  // passed: the change has now definitely been made or refused, so the store is
  // worth reading again — the re-read made at the deadline may itself have failed.
  function withDeadline<T>(operation: Promise<T>, onLate: () => void): Promise<T> {
    return new Promise<T>((resolve, reject) => {
      let expired = false;
      const timer = setTimeout(() => {
        expired = true;
        reject(new Unanswered());
      }, WRITE_DEADLINE_MS);
      operation.then(
        (value) => {
          clearTimeout(timer);
          if (expired) onLate();
          else resolve(value);
        },
        (error) => {
          clearTimeout(timer);
          if (expired) onLate();
          else reject(error);
        },
      );
    });
  }

  function inOrder<T>(run: () => Promise<T>, stillWanted: () => boolean, reread: () => void): Promise<T> {
    const turn = writes
      .catch(() => undefined)
      .then(() => {
        // Asked in this order: a write the user had already called off (form
        // closed, store hidden) stays silent even if the page was then left.
        if (!stillWanted()) throw new Withdrawn();
        if (gone) throw new Withdrawn(true);
        return withDeadline(run(), reread);
      });
    writes = turn.catch(() => undefined);
    return turn;
  }

  function putCredential(c: Credential) {
    credentialsGeneration++;
    credentialsLoading = false;
    credentials = credentials.some((x) => x.credential_id === c.credential_id)
      ? credentials.map((x) => (x.credential_id === c.credential_id ? c : x))
      : [...credentials, c];
  }

  function putSecret(x: Secret) {
    secretsGeneration++;
    secretsLoading = false;
    secrets = secrets.some((y) => y.secret_id === x.secret_id)
      ? secrets.map((y) => (y.secret_id === x.secret_id ? x : y))
      : [...secrets, x];
  }

  const reread = (k: VaultKind) => void (k === 'secret' ? loadSecrets() : reloadCredentials());

  async function save() {
    if (saving) return;
    // The store may have been hidden from this user since the form opened.
    if (!kindAllowed(kind)) {
      closeForm();
      return;
    }
    formError = validate();
    if (formError) return;

    // Everything the request needs is captured here, before the first await:
    // the form may be closed (and its fields cleared) while the request is out.
    const submitted = formId;
    const submittedKind = kind;
    const entry = editing;
    const label = kindInfo(submittedKind).label;
    let run: () => Promise<Credential | Secret>;

    if (submittedKind === 'secret') {
      const s = entry?.secret;
      const description = fDescription.trim() || undefined;
      const value = fValue;
      const name = fName.trim();
      if (s) {
        // Replacing a stored value swaps the ciphertext the moment it is saved, so
        // confirm first. Editing the description alone must not touch the value.
        if (value) {
          const ok = await confirm({
            tone: 'danger',
            title: `Rotate "${s.name}"?`,
            message: 'Rotating replaces the value immediately; in-flight specs will use the new one.',
            confirmLabel: 'Rotate',
          });
          if (!ok) return;
          // The confirmation stayed open while anything could happen: the form
          // may have been closed or replaced, or the store hidden from this user.
          if (!kindAllowed('secret') || submitted !== formId || !modalOpen) return;
        }
        run = () => secretsApi.update(s.secret_id, { description, ...(value ? { value } : {}) });
      } else {
        run = () => secretsApi.create({ name, description, value });
      }
    } else {
      const authMethod = kindInfo(submittedKind).authMethod ?? 'password';
      const body = {
        name: fName.trim(),
        type: effectiveType,
        username: fUsername.trim() || undefined,
        auth_method: authMethod,
        // Only material the user typed is sent: an empty field on an edit keeps
        // what is stored.
        ...(authMethod === 'password' && fPassword ? { password: fPassword } : {}),
        ...(authMethod === 'key' && fKey ? { private_key: fKey } : {}),
        ...(authMethod === 'key' && fPassphrase ? { key_passphrase: fPassphrase } : {}),
      };
      const c = entry?.credential;
      run = c ? () => credentialsApi.update(c.credential_id, body) : () => credentialsApi.create(body);
    }

    savingForm = submitted;
    try {
      // Not yet sent when its turn comes: only if the form it was submitted from
      // is still the open one and the store is still this user's to change.
      const written = await inOrder(
        run,
        () => kindAllowed(submittedKind) && modalOpen && submitted === formId,
        () => reread(submittedKind),
      );
      // Applied as it arrives (writes are in order, so nothing newer has been
      // applied before it), then the store is re-read in the background.
      if (submittedKind === 'secret') putSecret(written as Secret);
      else putCredential(written as Credential);
      reread(submittedKind);
      if (modalOpen && submitted === formId) closeForm();
      toast.success(entry ? `${label} updated` : `${label} added to the Vault`);
    } catch (e) {
      // Withdrawn before it was sent: nothing happened, so nothing is reported —
      // unless it was withdrawn because the Vault was left with it still waiting.
      if (e instanceof Withdrawn) {
        if (e.left)
          toast.info(`${label} not saved`, {
            description: 'The Vault was closed before this change could be sent.',
          });
        return;
      }
      // Never answered: the list is the only way to learn whether it was saved,
      // and the user is told exactly that rather than "it failed".
      const unanswered = outcomeUnknown(e);
      if (unanswered) reread(submittedKind);
      // Shown in the form it came from. If that form is gone, the failure still
      // has to reach the user, so it becomes a toast.
      if (!gone && modalOpen && submitted === formId) formError = unanswered ? outcomeUnknownText(e) : errorMessage(e);
      else if (unanswered) toast.error(outcomeUnknownText(e));
      else toast.fromError(e, `Failed to save the ${kindNoun(submittedKind)}`);
    } finally {
      if (savingForm === submitted) savingForm = null;
    }
  }

  async function remove(e: Entry) {
    if (!kindAllowed(e.kind)) return;
    const known = e.credential ? (links[e.credential.credential_id] ?? []) : [];
    const ok = await confirm({
      title: `Delete "${e.name}"?`,
      message: e.credential
        ? `"${e.name}" will be removed. Devices and Git repositories referencing it lose their credential.` +
          (known.length && linksCheckedAt
            ? ` Known links when last checked (${clock(linksCheckedAt)}): ${known.join(', ')}.`
            : '')
        : 'Any spec referencing it will break until a replacement is created.',
      tone: 'danger',
      confirmLabel: 'Delete',
    });
    if (!ok) return;
    // The confirmation stayed open while the store may have been hidden from
    // this user; a delete confirmed after that must not go out.
    if (!kindAllowed(e.kind)) return;
    try {
      if (e.credential) {
        const id = e.credential.credential_id;
        await inOrder(() => credentialsApi.delete(id), () => kindAllowed(e.kind), () => reread(e.kind));
        credentialsGeneration++;
        credentialsLoading = false;
        credentials = credentials.filter((c) => c.credential_id !== id);
        const { [id]: _gone, ...rest } = links;
        links = rest;
      } else if (e.secret) {
        const id = e.secret.secret_id;
        await inOrder(() => secretsApi.delete(id), () => kindAllowed(e.kind), () => reread(e.kind));
        secretsGeneration++;
        secretsLoading = false;
        secrets = secrets.filter((x) => x.secret_id !== id);
      }
      reread(e.kind);
      toast.success(`${kindInfo(e.kind).label} deleted`);
    } catch (err) {
      if (err instanceof Withdrawn) {
        if (err.left)
          toast.info(`The delete of "${e.name}" was not sent`, {
            description: 'The Vault was closed before the delete could be sent.',
          });
        return;
      }
      if (outcomeUnknown(err)) {
        reread(e.kind);
        toast.error(outcomeUnknownText(err));
        return;
      }
      toast.fromError(err, "Couldn't delete the entry");
    }
  }

  const secretRef = (name: string) => `\${secret:secret:${name}:value}`;

  // Copies the ${secret:…} reference for a secret value, so it can be pasted into
  // a spec without remembering the syntax. Only when copying fails is it shown.
  async function copyRef(s: Secret) {
    const ref = secretRef(s.name);
    if (await copyText(ref)) toast.success('Reference copied', { description: ref });
    else toast.info('Copy failed — reference:', { description: ref });
  }

  // ?new=1 opens the form (straight to a kind's fields when ?type names one). The
  // command palette's create action and the old /credentials and /admin/secrets
  // URLs arrive this way.
  //
  // Handled after each navigation has finished (SvelteKit reuses this page when
  // only the query changes, so the palette action has to work while already
  // here). `new=1` is consumed: it is removed from the URL once acted on, so
  // cancelling the form does not reopen it and choosing the action again works a
  // second time. A ?type that is unknown, or names a kind this user may not see,
  // is removed the same way, so the URL never claims a filter that is not applied.
  //
  // The clean-up is its own navigation and is started only once the one that
  // brought us here is complete, and never while the user is on their way to
  // another page: a clean-up started then would replace that navigation and keep
  // them here. `handledHref` keeps the triggers from acting on one URL twice.
  let handledHref = '';
  let leaving = false;
  let cleanupTimer: ReturnType<typeof setTimeout> | undefined;

  beforeNavigate(({ to }) => {
    leaving = !!to && to.url.pathname !== page.url.pathname;
    if (leaving) clearTimeout(cleanupTimer);
  });
  onDestroy(() => {
    gone = true;
    clearTimeout(cleanupTimer);
    // Reads still in the air belong to a page that no longer exists.
    credentialsGeneration++;
    secretsGeneration++;
    linksGeneration++;
  });

  function consumeUrl(force = false) {
    if (leaving) return;
    const href = page.url.href;
    if (!force && href === handledHref) return;
    handledHref = href;
    const type = page.url.searchParams.get('type');
    const wantsNew = page.url.searchParams.get('new') === '1';
    const staleType = type !== null && filter === 'all';
    if (wantsNew) openCreate(filter !== 'all' ? filter : undefined);
    if (!wantsNew && !staleType) return;
    clearTimeout(cleanupTimer);
    cleanupTimer = setTimeout(() => {
      // Only if nothing else has navigated, or begun to, in the meantime.
      if (leaving || page.url.href !== href) return;
      goto(
        vaultUrl((q) => {
          q.delete('new');
          if (staleType) q.delete('type');
        }),
        QUIET,
      );
    }, 0);
  }

  afterNavigate(() => {
    // Still here after a navigation settled, so it was one within the Vault.
    leaving = false;
    consumeUrl();
  });

  // And whenever the address changes by any other route (Back and Forward):
  // deferred, so it runs after whatever changed the address has finished.
  $effect(() => {
    page.url.href;
    const timer = setTimeout(() => consumeUrl(), 0);
    return () => clearTimeout(timer);
  });

  const filterChips = $derived([
    { value: 'all' as const, label: 'All' },
    ...kinds.map((k) => ({ value: k.kind, label: k.label })),
  ]);

  // Nothing at all to show and every store this user may see failed: the whole
  // page is an error.
  const nothingLoaded = $derived(
    entries.length === 0 && !!credentialsError && (!showSecrets || !!secretsError),
  );
  // A spinner while a store the filter is showing has never been read and there
  // is nothing to show yet — judged after the search, so looking for an entry of
  // a store that is still loading shows "loading", not "no match".
  const tableLoading = $derived(selectedLoading && visible.length === 0);
</script>

<svelte:head><title>Vault · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title="Vault"
    description="Every login, key, token and secret value FlowWeaver uses to reach other systems. Stored encrypted; values are never shown again."
  >
    {#snippet actions()}
      <Button variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
      <Button variant="primary" icon={Plus} onclick={() => openCreate(filter !== 'all' ? filter : undefined)}>New entry</Button>
    {/snippet}
  </PageHeader>

  {#if nothingLoaded}
    <Alert tone="error" title="The Vault could not be loaded">
      <div class="flex flex-wrap items-center gap-3 text-sm">
        <span>{errorMessage(credentialsError ?? secretsError)}</span>
        <Button variant="ghost" size="xs" onclick={load}>Retry</Button>
      </div>
    </Alert>
  {:else}
    {#if credentialsError}
      <Alert tone="error" title="Logins, keys and tokens could not be loaded">
        <div class="flex flex-wrap items-center gap-3 text-sm">
          <span>{errorMessage(credentialsError)}{showSecrets ? ' Secret values below are unaffected.' : ''}</span>
          <Button variant="ghost" size="xs" onclick={() => void reloadCredentials()}>Retry</Button>
        </div>
      </Alert>
    {/if}
    {#if secretsError}
      <Alert tone="error" title="Secret values could not be loaded">
        <div class="flex flex-wrap items-center gap-3 text-sm">
          <span>{errorMessage(secretsError)} The other entries below are unaffected.</span>
          <Button variant="ghost" size="xs" onclick={() => void loadSecrets()}>Retry</Button>
        </div>
      </Alert>
    {/if}
    {#if credentialsIncomplete && !credentialsError}
      <Alert tone="warning" title="The list of logins, keys and tokens may be incomplete">
        <div class="flex flex-wrap items-center gap-3 text-sm">
          <span>
            It changed while it was being read, or holds more than {PAGE * MAX_PAGES} entries. The rows shown are
            real; counts and search may miss some.
          </span>
          <Button variant="ghost" size="xs" onclick={() => void reloadCredentials()}>Read again</Button>
        </div>
      </Alert>
    {/if}

    <div class="flex flex-wrap items-center justify-between gap-3">
      <div class="flex flex-wrap gap-1.5" role="group" aria-label="Filter by type">
        {#each filterChips as chip (chip.value)}
          <button
            type="button"
            aria-pressed={filter === chip.value}
            class="rounded-full border px-3 py-1 text-xs transition {filter === chip.value
              ? 'border-primary-500 bg-primary-500/10 text-surface-950-50'
              : 'border-surface-200-800 text-surface-600-400 hover:text-surface-950-50'}"
            onclick={() => setFilter(chip.value)}
          >
            {chip.label}
            <span class="ml-1 tabular-nums opacity-70">{countFor(chip.value)}</span>
          </button>
        {/each}
      </div>
      <SearchInput bind:value={search} placeholder="Search the Vault…" width="w-72" />
    </div>

    {#if tableLoading}
      <div class="py-12 flex justify-center"><Spinner size="lg" /></div>
    {:else if visible.length === 0}
      <Card padding="none">
        <EmptyState icon={Vault} title={emptyText}>
          {#snippet actions()}
            {#if search.trim()}
              <Button variant="ghost" onclick={() => (search = '')}>Clear search</Button>
            {:else if stateFor(filter) === 'ok'}
              <Button variant="primary" icon={Plus} onclick={() => openCreate(filter !== 'all' ? filter : undefined)}>New entry</Button>
            {/if}
          {/snippet}
        </EmptyState>
      </Card>
    {:else}
      <DataTable caption="Vault entries with their type, what they are used for, details, stored value, known links and last update">
        <thead>
          <tr>
            <th>Name</th>
            <th>Type</th>
            <th>Used for</th>
            <th>Details</th>
            <th>Value</th>
            <th>Known links</th>
            <th>Updated</th>
            <th class="!text-right">Actions</th>
          </tr>
        </thead>
        <tbody>
          {#each visible as row (row.key)}
            {@const info = kindInfo(row.kind)}
            <tr>
              <td>
                {#if row.secret}
                  <span class="block min-w-[9rem] max-w-[16rem] font-mono text-surface-900-100 [overflow-wrap:anywhere]">{row.name}</span>
                  <!-- w-0 + min-w-full: the reference takes the width the name gives the
                       column and truncates there, instead of widening the column itself. -->
                  <button
                    type="button"
                    class="block w-0 min-w-full truncate text-left font-mono text-[11px] text-surface-500 hover:text-surface-900-100"
                    title="Copy this reference to paste into a spec"
                    onclick={() => copyRef(row.secret!)}
                  >
                    {secretRef(row.name)}
                  </button>
                {:else}
                  <span class="block min-w-[9rem] max-w-[16rem] font-medium text-surface-900-100 [overflow-wrap:anywhere]">{row.name}</span>
                {/if}
              </td>
              <td>
                <Badge tone={row.kind === 'secret' ? 'neutral' : 'primary'}>
                  <span class="inline-flex items-center gap-1 whitespace-nowrap"><info.icon size={11} />{info.label}</span>
                </Badge>
              </td>
              <td>
                {#if row.usedFor}<Badge mono>{row.usedFor}</Badge>{:else}<span class="text-surface-500">—</span>{/if}
              </td>
              <td><span class="block min-w-[8rem] text-surface-700-300 [overflow-wrap:anywhere]">{row.detail || '—'}</span></td>
              <td>
                {#if row.secret}
                  <StatusBadge status={row.secret.has_value ? 'success' : 'failed'} label={row.secret.has_value ? 'set' : 'empty'} showDot={false} />
                {:else if row.credential?.auth_method === 'key'}
                  <StatusBadge status={row.credential.has_private_key ? 'success' : 'failed'} label={row.credential.has_private_key ? 'key' : 'key missing'} showDot={false} />
                {:else}
                  <!-- The API does not say whether a password is stored, only what kind of
                       material the entry authenticates with — as the Credentials page showed it. -->
                  <StatusBadge status="success" label={inSentence(secretLabel(row.kind))} showDot={false} />
                {/if}
              </td>
              <td>
                {#if row.credential && links[row.credential.credential_id]}
                  <!-- Each link stays on one line ("1 repository"); lines break only between links. -->
                  <span class="text-surface-700-300"
                    >{#each links[row.credential.credential_id] as link, i (link)}{#if i}, {/if}<span class="whitespace-nowrap">{link}</span
                      >{/each}</span
                  >
                {:else}
                  <span
                    class="text-surface-500"
                    title={row.secret
                      ? 'Secret values are used by reference, which is not tracked here.'
                      : 'No link found in the lists that were checked. That does not mean unused.'}>—</span
                  >
                {/if}
              </td>
              <!-- May break between date and time: on a 1280px screen the table needs those pixels to keep the actions in view. -->
              <td class="text-surface-500 text-[11px] tabular-nums">{formatDateTime(row.updatedAt)}</td>
              <td class="text-right">
                <div class="inline-flex items-center gap-1">
                  {#if row.secret}
                    <IconButton icon={Copy} label="Copy reference" onclick={() => copyRef(row.secret!)} />
                  {/if}
                  <IconButton icon={Pencil} label="Edit entry" onclick={() => openEdit(row)} />
                  <IconButton icon={Trash2} label="Delete entry" variant="danger" onclick={() => remove(row)} />
                </div>
              </td>
            </tr>
          {/each}
        </tbody>
      </DataTable>
      {#if linksNote}
        <p class="text-xs text-surface-500">{linksNote}</p>
      {/if}
    {/if}
  {/if}
</div>

<Dialog
  bind:open={modalOpen}
  onClose={onFormClosed}
  title={editing ? `Edit ${kindNoun(kind)}` : step === 'pick' ? 'New Vault entry' : `New ${kindNoun(kind)}`}
  size="md"
>
  <div data-vault-form class="space-y-4 p-1">
    {#if step === 'pick'}
      <p class="text-sm text-surface-600-400">What are you storing?</p>
      <div class="grid gap-2 sm:grid-cols-2">
        {#each kinds as k (k.kind)}
          <button
            type="button"
            data-kind={k.kind}
            class="flex items-start gap-3 rounded-lg border border-surface-200-800 p-3 text-left transition hover:border-primary-500 hover:bg-primary-500/5"
            onclick={() => pick(k.kind)}
          >
            <k.icon size={18} class="mt-0.5 shrink-0 text-primary-500" />
            <span>
              <span class="block text-sm font-medium">{k.label}</span>
              <span class="block text-xs text-surface-600-400">{k.desc}</span>
            </span>
          </button>
        {/each}
      </div>
    {:else}
      {#if formError}<Alert tone="error">{formError}</Alert>{/if}

      {#if kind === 'secret'}
        <Input
          label="Name"
          help="secrets.name"
          placeholder="netbox-token"
          bind:value={fName}
          disabled={!!editing || saving}
          hint={editing
            ? 'Names are immutable — references in specs stay stable.'
            : 'Lowercase letters, digits, hyphens and underscores. It becomes part of the reference.'}
        />
        <Textarea
          label="Description"
          help="secrets.description"
          placeholder="What this value authorizes — shown in the Vault only."
          rows={2}
          bind:value={fDescription}
          disabled={saving}
        />
        <Textarea
          label={editing ? 'New value (leave empty to keep current)' : 'Value'}
          help="secrets.value"
          placeholder="Paste the secret value here."
          rows={3}
          bind:value={fValue}
          disabled={saving}
          hint={editing
            ? 'Leave blank to update only the description. Submitting a value rotates it.'
            : 'Stored encrypted at rest. The value cannot be retrieved after submission — rotate to replace it.'}
        />
        {#if fName.trim() && SECRET_NAME_RE.test(fName.trim())}
          <p class="text-xs text-surface-600-400">
            Reference: <code class="font-mono">{secretRef(fName.trim())}</code>
          </p>
        {/if}
      {:else}
        <div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <Input label="Name" help="credentials.name" placeholder="core-routers-ssh" bind:value={fName} disabled={saving} />
          {#if editing}
            <Select label="Type" help="vault.kind" bind:value={kind} disabled={saving}>
              {#each credentialKinds as k (k.kind)}
                <option value={k.kind}>{k.label}</option>
              {/each}
            </Select>
          {:else}
            <Input label="Username" help="credentials.username" placeholder={kind === 'git_token' ? 'account name' : 'netops'} bind:value={fUsername} disabled={saving} />
          {/if}
        </div>
        {#if editing}
          <Input label="Username" help="credentials.username" placeholder={kind === 'git_token' ? 'account name' : 'netops'} bind:value={fUsername} disabled={saving} />
        {/if}

        {#if choosesType(kind)}
          <div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <Select label="Used for" help="credentials.type" bind:value={fUsedFor} disabled={saving}>
              {#each kindInfo(kind).types ?? [] as t (t)}
                <option value={t}>{t}</option>
              {/each}
              <option value="custom">custom…</option>
            </Select>
            {#if fUsedFor === 'custom'}
              <Input label="Custom type" help="vault.custom_type" placeholder="custom-type" bind:value={fCustomType} disabled={saving} />
            {/if}
          </div>
        {:else}
          <p class="text-xs text-surface-600-400">
            Stored with type <code class="font-mono">{effectiveType}</code>, which is what the pickers that offer it look for.
          </p>
        {/if}

        {#if kindInfo(kind).authMethod === 'key'}
          <Textarea
            label={editing?.credential?.has_private_key ? 'New private key (leave empty to keep current)' : 'Private key (PEM)'}
            help="credentials.private_key"
            placeholder={'-----BEGIN OPENSSH PRIVATE KEY-----\n…'}
            rows={6}
            bind:value={fKey}
            disabled={saving}
          />
          <Input
            label="Key passphrase (optional)"
            help="credentials.key_passphrase"
            type="password"
            revealable
            placeholder={editing ? '••••••••' : 'Leave empty if the key is not passphrase-protected'}
            bind:value={fPassphrase}
            disabled={saving}
          />
        {:else}
          <Input
            label={editing ? `New ${inSentence(secretLabel(kind))} (leave empty to keep current)` : secretLabel(kind)}
            help="credentials.password"
            type="password"
            revealable
            placeholder={editing ? '••••••••' : ''}
            bind:value={fPassword}
            disabled={saving}
          />
        {/if}
      {/if}
    {/if}
  </div>

  {#snippet footer()}
    {#if step === 'form' && !editing}
      <Button variant="ghost" icon={ChevronLeft} onclick={backToTypes} disabled={saving}>Type</Button>
    {/if}
    <Button variant="ghost" onclick={closeForm}>Cancel</Button>
    {#if step === 'form'}
      <Button variant="primary" onclick={save} loading={saving}>{editing ? 'Save changes' : 'Add to Vault'}</Button>
    {/if}
  {/snippet}
</Dialog>
