import { expect, test, type Page, type Route } from '@playwright/test';
import { BASE } from './fixtures/chat-stub';

// The Vault: one screen over the two stores the Credentials and Secrets pages
// used to show separately. Like the chat specs, this runs against a stubbed API —
// no backend — and asserts what the page sends and shows.
//
// Run the app first:  npm run build && npm run preview -- --port 4173
// (or point E2E_BASE at a dev server).

const TS = '2026-11-01T12:00:00Z';

type Cred = {
  credential_id: string;
  name: string;
  type: string;
  username: string | null;
  auth_method: string;
  has_private_key: boolean;
  extra: Record<string, unknown>;
  created_at: string;
  updated_at: string;
};

const cred = (id: string, name: string, type: string, username: string | null, auth = 'password'): Cred => ({
  credential_id: id,
  name,
  type,
  username,
  auth_method: auth,
  has_private_key: auth.trim().toLowerCase() === 'key',
  extra: {},
  created_at: TS,
  updated_at: TS,
});

// Rows as older write paths left them: method and type are read trimmed and
// lowercased, so "KEY" is a key and " Git_Token " is a Git token.
const CREDS: Cred[] = [
  cred('c1', 'core-ssh', 'ssh', 'netops'),
  cred('c2', 'legacy-key', 'netconf', 'admin', 'KEY'),
  cred('c3', 'github-pat', ' Git_Token ', 'bot'),
  cred('c4', 'servicenow-api', 'api_key', 'svc'),
  cred('c5', 'snmp-ro', 'snmp_v3', 'monitor'),
  cred('c6', 'lab-basic', 'http_basic', 'lab'),
];

type Sec = { secret_id: string; name: string; description: string | null; has_value: boolean; created_by: string; created_at: string; updated_at: string };
const SECRETS: Sec[] = [
  { secret_id: 's1', name: 'netbox-token', description: 'NetBox API token', has_value: true, created_by: 'admin', created_at: TS, updated_at: TS },
  { secret_id: 's2', name: 'empty-one', description: 'never filled', has_value: false, created_by: 'admin', created_at: TS, updated_at: TS },
];

interface Options {
  role?: string;
  creds?: Cred[];
  secrets?: Sec[];
  secretFail?: boolean;
  /** PUT and POST to a credential are held until the test releases them. */
  holdWrites?: boolean;
  /** A write to a credential is committed, then the connection drops before any answer. */
  dropWriteAnswer?: boolean;
  quickLinks?: string[];
}

type Call = { method: string; path: string; body: Record<string, unknown> | null };

function gate() {
  let release!: () => void;
  let markArrived!: () => void;
  const released = new Promise<void>((r) => (release = r));
  const arrived = new Promise<void>((r) => (markArrived = r));
  return { release, markArrived, released, arrived };
}

const json = (route: Route, body: unknown, status = 200) =>
  route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });

async function mockVault(page: Page, opts: Options = {}) {
  const state = {
    creds: [...(opts.creds ?? CREDS)],
    secrets: [...(opts.secrets ?? SECRETS)],
    devices: [
      { device_id: 'd1', name: 'r1', credential_id: 'c1' },
      { device_id: 'd2', name: 'r2', credential_id: 'c1' },
    ] as { device_id: string; name: string; credential_id: string }[],
  };
  const calls = Object.assign([] as Call[], { server: state, write: gate() });

  await page.addInitScript(
    ({ role, quickLinks }) => {
      localStorage.setItem(
        'flowweaver:auth',
        JSON.stringify({
          accessToken: 'e2e-token',
          refreshToken: 'e2e-refresh',
          userId: '00000000-0000-0000-0000-000000000001',
          username: 'e2e',
          role,
          expiresAt: Date.now() + 86_400_000,
        }),
      );
      if (quickLinks) localStorage.setItem('flowweaver:dashboard:quicklinks', JSON.stringify(quickLinks));
    },
    { role: opts.role ?? 'admin', quickLinks: opts.quickLinks ?? null },
  );

  // Match on pathname, not a glob: `**/api/**` also swallows the app's own
  // modules under /src/lib/api/. Catch-all first — later routes win.
  await page.route(
    (url) => url.pathname.startsWith('/api/'),
    (route) => json(route, { data: [], items: [], total: 0 }),
  );

  await page.route(
    (url) => url.pathname === '/api/credential',
    async (route) => {
      const request = route.request();
      if (request.method() === 'GET') {
        const url = new URL(request.url());
        const limit = Number(url.searchParams.get('limit') ?? 50);
        const offset = Number(url.searchParams.get('offset') ?? 0);
        calls.push({ method: 'GET', path: `credential?offset=${offset}`, body: null });
        return json(route, { data: state.creds.slice(offset, offset + limit), total: state.creds.length, limit, offset });
      }
      const body = request.postDataJSON();
      calls.push({ method: 'POST', path: 'credential', body });
      const created = cred(`new${calls.length}`, body.name, body.type, body.username ?? null, body.auth_method);
      state.creds = [...state.creds, created];
      if (opts.holdWrites) {
        calls.write.markArrived();
        await calls.write.released;
      }
      return json(route, created);
    },
  );
  await page.route(
    (url) => url.pathname.startsWith('/api/credential/'),
    async (route) => {
      const request = route.request();
      const id = new URL(request.url()).pathname.split('/').pop()!;
      const body = request.method() === 'PUT' ? request.postDataJSON() : null;
      calls.push({ method: request.method(), path: `credential/${id}`, body });
      if (request.method() === 'DELETE') {
        state.creds = state.creds.filter((c) => c.credential_id !== id);
        if (opts.dropWriteAnswer) return route.abort('connectionreset');
        return route.fulfill({ status: 204, body: '' });
      }
      const current = state.creds.find((c) => c.credential_id === id)!;
      const edited: Cred = {
        ...current,
        name: body.name ?? current.name,
        type: body.type ?? current.type,
        username: body.username ?? current.username,
        auth_method: body.auth_method ?? current.auth_method,
        has_private_key: current.has_private_key || !!body.private_key,
      };
      state.creds = state.creds.map((c) => (c.credential_id === id ? edited : c));
      if (opts.dropWriteAnswer) return route.abort('connectionreset');
      if (opts.holdWrites) {
        calls.write.markArrived();
        await calls.write.released;
      }
      return json(route, edited);
    },
  );
  await page.route(
    (url) => url.pathname === '/api/secrets',
    (route) => {
      const request = route.request();
      if (request.method() === 'GET') {
        calls.push({ method: 'GET', path: 'secrets', body: null });
        return opts.secretFail ? json(route, { error: 'boom' }, 500) : json(route, state.secrets);
      }
      calls.push({ method: 'POST', path: 'secrets', body: request.postDataJSON() });
      return json(route, state.secrets[0]);
    },
  );
  await page.route(
    (url) => url.pathname.startsWith('/api/secrets/'),
    (route) => {
      const request = route.request();
      const id = new URL(request.url()).pathname.split('/').pop()!;
      calls.push({ method: request.method(), path: `secrets/${id}`, body: request.method() === 'PUT' ? request.postDataJSON() : null });
      if (request.method() !== 'DELETE') return json(route, state.secrets[0]);
      state.secrets = state.secrets.filter((x) => x.secret_id !== id);
      return route.fulfill({ status: 204, body: '' });
    },
  );
  await page.route(
    (url) => url.pathname === '/api/device',
    (route) => json(route, { data: state.devices, total: state.devices.length, limit: 200, offset: 0 }),
  );
  await page.route(
    (url) => url.pathname === '/api/git/repositories',
    (route) => json(route, { data: [{ git_repository_id: 'r1', name: 'cfg', auth_credential_id: 'c3' }], total: 1, limit: 200, offset: 0 }),
  );
  return calls;
}

const sent = (calls: Call[], method: string, path: string) => calls.find((c) => c.method === method && c.path === path);
const row = (page: Page, name: string) => page.getByRole('row').filter({ hasText: name });
const dialog = (page: Page) => page.getByRole('dialog');
const field = (page: Page, label: string | RegExp) => dialog(page).getByLabel(label, { exact: typeof label === 'string' });
const chip = (page: Page, label: RegExp) => page.getByRole('group', { name: 'Filter by type' }).getByRole('button', { name: label });
const vault = (page: Page, query = '') => page.goto(`${BASE}/vault${query}`);

test.describe('the list', () => {
  test('both stores in one list, each credential read as the kind its method and type say', async ({ page }) => {
    await mockVault(page);
    await vault(page);

    await expect(row(page, 'legacy-key')).toContainText('SSH key');
    await expect(row(page, 'legacy-key')).toContainText('netconf');
    await expect(row(page, 'github-pat')).toContainText('Git token');
    await expect(row(page, 'snmp-ro')).toContainText('SNMPv3 user');
    await expect(row(page, 'servicenow-api')).toContainText('API key');
    await expect(row(page, 'lab-basic')).toContainText('Login');
    await expect(row(page, 'netbox-token')).toContainText('${secret:secret:netbox-token:value}');

    await expect(chip(page, /^All/)).toContainText('8');
    await expect(chip(page, /^Login/)).toContainText('2');
    await expect(chip(page, /^Secret value/)).toContainText('2');
  });

  test('known links say what was found where, and never call an entry unused', async ({ page }) => {
    await mockVault(page);
    await vault(page);
    await expect(row(page, 'core-ssh')).toContainText('2 devices');
    await expect(row(page, 'github-pat')).toContainText('1 repository');
    await expect(page.getByText('an empty cell does not mean unused')).toBeVisible();
  });

  test('every page of credentials is read, so counts and search cover the whole store', async ({ page }) => {
    const many = Array.from({ length: 450 }, (_, i) => cred(`m${i}`, `dev-${String(i).padStart(3, '0')}`, 'ssh', 'ops'));
    const calls = await mockVault(page, { creds: many, secrets: [] });
    await vault(page);
    await expect(chip(page, /^Login/)).toContainText('450');
    expect(calls.filter((c) => c.path.startsWith('credential?')).map((c) => c.path)).toEqual([
      'credential?offset=0',
      'credential?offset=200',
      'credential?offset=400',
    ]);
    await page.getByPlaceholder('Search the Vault…').fill('dev-449');
    await expect(row(page, 'dev-449')).toBeVisible();
  });

  test('secrets down: credentials still work, and the secret count is unknown, not zero', async ({ page }) => {
    await mockVault(page, { secretFail: true });
    await vault(page);
    await expect(page.getByText('Secret values could not be loaded')).toBeVisible();
    await expect(row(page, 'core-ssh')).toBeVisible();
    await expect(chip(page, /^Secret value/)).toContainText('?');
    await expect(chip(page, /^All/)).toContainText('6+');
  });
});

test.describe('who sees what', () => {
  test('a non-admin manages credentials but sees no secret values, and none are asked for', async ({ page }) => {
    const calls = await mockVault(page, { role: 'operator' });
    await vault(page, '?type=secret');
    await expect(row(page, 'core-ssh')).toBeVisible();
    await expect(chip(page, /^Secret value/)).toHaveCount(0);
    await expect(page.getByText('netbox-token')).toHaveCount(0);
    // ?type=secret names a type this user may not see: dropped from the address.
    await expect(page).toHaveURL(/\/vault$/);
    expect(calls.some((c) => c.path === 'secrets')).toBe(false);

    await page.getByRole('button', { name: 'New entry' }).click();
    await expect(dialog(page).getByRole('button', { name: /^Secret value/ })).toHaveCount(0);
  });

  test('the old addresses lead to the Vault', async ({ page }) => {
    await mockVault(page);
    await page.goto(`${BASE}/credentials?new=1`);
    await expect(page).toHaveURL(/\/vault$/);
    await expect(dialog(page)).toContainText('What are you storing?');
    await dialog(page).getByRole('button', { name: 'Cancel' }).click();

    await page.goto(`${BASE}/admin/secrets`);
    await expect(page).toHaveURL(/\/vault\?type=secret$/);
    await expect(row(page, 'netbox-token')).toBeVisible();
    await expect(row(page, 'core-ssh')).toHaveCount(0);
  });

  test('a dashboard shortcut saved for Credentials or Secrets becomes one Vault shortcut', async ({ page }) => {
    await mockVault(page, { quickLinks: ['/credentials', '/admin/secrets', '/runs'] });
    await page.goto(`${BASE}/`);
    const vaultLinks = page.locator('main a[href="/vault"]');
    await expect(vaultLinks).toHaveCount(1);
    const stored = await page.evaluate(() => localStorage.getItem('flowweaver:dashboard:quicklinks'));
    expect(stored === null || !stored.includes('/credentials')).toBe(true);
  });
});

test.describe('the entry form', () => {
  test('"Used for" shows the type a new entry will be stored with, and follows the kind until the user picks one', async ({ page }) => {
    const calls = await mockVault(page);
    await vault(page);
    await expect(row(page, 'core-ssh')).toBeVisible();

    await page.getByRole('button', { name: 'New entry' }).click();
    await dialog(page).getByRole('button', { name: /^Login/ }).click();
    await expect(field(page, 'Used for')).toHaveValue('ssh');

    // A Git token's type is the kind itself.
    await dialog(page).getByRole('button', { name: 'Type', exact: true }).click();
    await dialog(page).getByRole('button', { name: /^Git token/ }).click();
    await expect(dialog(page).getByText('Stored with type git_token')).toBeVisible();

    // Back to a login: a type the user chose is kept when it still fits.
    await dialog(page).getByRole('button', { name: 'Type', exact: true }).click();
    await dialog(page).getByRole('button', { name: /^Login/ }).click();
    await field(page, 'Used for').selectOption('netconf');
    await dialog(page).getByRole('button', { name: 'Type', exact: true }).click();
    await dialog(page).getByRole('button', { name: /^SSH key/ }).click();
    await expect(field(page, 'Used for')).toHaveValue('netconf');

    await field(page, 'Name').fill('nc-key');
    await field(page, /^Private key/).fill('-----BEGIN OPENSSH PRIVATE KEY-----\nx\n-----END OPENSSH PRIVATE KEY-----');
    await dialog(page).getByRole('button', { name: 'Add to Vault' }).click();
    await expect.poll(() => sent(calls, 'POST', 'credential')?.body).toMatchObject({
      name: 'nc-key',
      type: 'netconf',
      auth_method: 'key',
    });
    expect(sent(calls, 'POST', 'credential')!.body).not.toHaveProperty('password');
  });

  test('renaming an older key keeps it a key and sends no secret material', async ({ page }) => {
    const calls = await mockVault(page);
    await vault(page);
    await row(page, 'legacy-key').getByRole('button', { name: 'Edit entry' }).click();
    await field(page, 'Name').fill('legacy-key-renamed');
    await dialog(page).getByRole('button', { name: 'Save changes' }).click();
    await expect.poll(() => sent(calls, 'PUT', 'credential/c2')?.body).toEqual({
      name: 'legacy-key-renamed',
      type: 'netconf',
      username: 'admin',
      auth_method: 'key',
    });
  });

  test('changing a login into a Git token stores the type that reads back as one, and drops what was typed', async ({ page }) => {
    const calls = await mockVault(page);
    await vault(page);
    await row(page, 'core-ssh').getByRole('button', { name: 'Edit entry' }).click();
    await field(page, /^New password/).fill('typed-password');
    await field(page, 'Type').selectOption('git_token');
    await expect(field(page, /^New token/)).toHaveValue('');
    await field(page, /^New token/).fill('ghp_123');
    await dialog(page).getByRole('button', { name: 'Save changes' }).click();
    await expect.poll(() => sent(calls, 'PUT', 'credential/c1')?.body).toMatchObject({
      type: 'git_token',
      auth_method: 'password',
      password: 'ghp_123',
    });
    await expect(row(page, 'core-ssh')).toContainText('Git token');
  });

  test('an SSH key turned into a login needs a password, which the server would not ask for', async ({ page }) => {
    const calls = await mockVault(page);
    await vault(page);
    await row(page, 'legacy-key').getByRole('button', { name: 'Edit entry' }).click();
    await field(page, 'Type').selectOption('login');
    await dialog(page).getByRole('button', { name: 'Save changes' }).click();
    await expect(dialog(page).getByText('Password is required when an SSH key becomes a login.')).toBeVisible();
    expect(sent(calls, 'PUT', 'credential/c2')).toBeUndefined();

    await field(page, /^New password/).fill('s3cret');
    await dialog(page).getByRole('button', { name: 'Save changes' }).click();
    await expect.poll(() => sent(calls, 'PUT', 'credential/c2')?.body).toMatchObject({
      auth_method: 'password',
      type: 'netconf',
      password: 's3cret',
    });
  });

  test('a login renamed without retyping its password is saved as it is', async ({ page }) => {
    // The rule above is only for a key turning into a password kind: an existing
    // password entry keeps "leave empty to keep current".
    const calls = await mockVault(page);
    await vault(page);
    await row(page, 'lab-basic').getByRole('button', { name: 'Edit entry' }).click();
    await field(page, 'Name').fill('lab-basic-2');
    await dialog(page).getByRole('button', { name: 'Save changes' }).click();
    await expect.poll(() => sent(calls, 'PUT', 'credential/c6')?.body).toEqual({
      name: 'lab-basic-2',
      type: 'http_basic',
      username: 'lab',
      auth_method: 'password',
    });
  });

  test('a credential form cannot be edited while its save is in flight', async ({ page }) => {
    const calls = await mockVault(page, { holdWrites: true });
    await vault(page);
    await row(page, 'core-ssh').getByRole('button', { name: 'Edit entry' }).click();
    await field(page, 'Name').fill('core-ssh-renamed');
    await dialog(page).getByRole('button', { name: 'Save changes' }).click();
    await calls.write.arrived;

    // Typing now would be lost when the save closes the form.
    await expect(field(page, 'Name')).toBeDisabled();
    await expect(field(page, 'Username')).toBeDisabled();
    await expect(field(page, 'Used for')).toBeDisabled();
    await expect(field(page, /^New password/)).toBeDisabled();

    calls.write.release();
    await expect(dialog(page)).toBeHidden();
    await expect(row(page, 'core-ssh-renamed')).toBeVisible();
  });

  test('known links are read again after a credential is saved or deleted', async ({ page }) => {
    const calls = await mockVault(page);
    await vault(page);
    await expect(row(page, 'core-ssh')).toContainText('2 devices');

    // A device is moved onto legacy-key elsewhere; saving the entry brings it in.
    calls.server.devices = [
      { device_id: 'd1', name: 'r1', credential_id: 'c1' },
      { device_id: 'd2', name: 'r2', credential_id: 'c2' },
    ];
    await row(page, 'legacy-key').getByRole('button', { name: 'Edit entry' }).click();
    await field(page, 'Name').fill('legacy-key-2');
    await dialog(page).getByRole('button', { name: 'Save changes' }).click();
    await expect(row(page, 'legacy-key-2')).toContainText('1 device');
    await expect(row(page, 'core-ssh')).toContainText('1 device');

    // And after a delete.
    calls.server.devices = [{ device_id: 'd2', name: 'r2', credential_id: 'c2' }];
    await row(page, 'core-ssh').getByRole('button', { name: 'Delete entry' }).click();
    await page.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(row(page, 'core-ssh')).toHaveCount(0);
    await expect(row(page, 'legacy-key-2')).toContainText('1 device');
  });

  test('a save whose connection is lost is reported as unknown, not failed, and the list is re-read', async ({ page }) => {
    const calls = await mockVault(page, { dropWriteAnswer: true });
    await vault(page);
    await row(page, 'core-ssh').getByRole('button', { name: 'Edit entry' }).click();
    await field(page, 'Name').fill('core-ssh-x');
    const readsBefore = calls.filter((c) => c.path === 'credential?offset=0').length;
    await dialog(page).getByRole('button', { name: 'Save changes' }).click();
    await expect(dialog(page).getByText('may or may not have been saved')).toBeVisible();
    // The server made the change; the re-read shows it.
    await expect.poll(() => calls.filter((c) => c.path === 'credential?offset=0').length).toBeGreaterThan(readsBefore);
    await expect(row(page, 'core-ssh-x')).toBeVisible();
  });

  test('a secret name is fixed, a bad one is refused, and a description edit leaves the value alone', async ({ page }) => {
    const calls = await mockVault(page);
    await vault(page);
    await row(page, 'netbox-token').getByRole('button', { name: 'Edit entry' }).click();
    await expect(field(page, 'Name')).toBeDisabled();
    await field(page, 'Description').fill('new description');
    await dialog(page).getByRole('button', { name: 'Save changes' }).click();
    await expect.poll(() => sent(calls, 'PUT', 'secrets/s1')?.body).toEqual({ description: 'new description' });

    await page.getByRole('button', { name: 'New entry' }).click();
    await dialog(page).getByRole('button', { name: /^Secret value/ }).click();
    await field(page, 'Name').fill('Bad Name');
    await field(page, 'Value').fill('v');
    await dialog(page).getByRole('button', { name: 'Add to Vault' }).click();
    await expect(dialog(page).getByText('Name must be 2–64 chars')).toBeVisible();
    expect(sent(calls, 'POST', 'secrets')).toBeUndefined();
  });

  test('form titles keep the capitals of SSH, SNMPv3 and API', async ({ page }) => {
    await mockVault(page);
    await vault(page);
    await page.getByRole('button', { name: 'New entry' }).click();
    await dialog(page).getByRole('button', { name: /^API key/ }).click();
    await expect(dialog(page)).toContainText('New API key');
    await dialog(page).getByRole('button', { name: 'Type', exact: true }).click();
    await dialog(page).getByRole('button', { name: /^Login/ }).click();
    await expect(dialog(page)).toContainText('New login');
    await dialog(page).getByRole('button', { name: 'Cancel' }).click();
    await row(page, 'snmp-ro').getByRole('button', { name: 'Edit entry' }).click();
    await expect(dialog(page)).toContainText('Edit SNMPv3 user');
  });
});

test.describe('layout', () => {
  // Playwright scrolls before it clicks, so no other test would notice actions
  // pushed behind a horizontal scroll.
  for (const width of [1280, 1440, 1920])
    test(`every row's actions are on screen at ${width}px, long names and all`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await mockVault(page, {
        secrets: [
          ...SECRETS,
          { secret_id: 's3', name: 'servicenow-production-oauth-client-secret', description: 'Client secret for the ServiceNow production instance', has_value: true, created_by: 'admin', created_at: TS, updated_at: TS },
        ],
      });
      await vault(page);
      await expect(row(page, 'core-ssh')).toContainText('2 devices');
      const fit = await page.evaluate(() => {
        const wrap = document.querySelector('table')!.parentElement!;
        const right = wrap.getBoundingClientRect().right;
        const deletes = [...document.querySelectorAll('[aria-label="Delete entry"]')];
        return { overflow: wrap.scrollWidth - wrap.clientWidth, cut: deletes.filter((b) => b.getBoundingClientRect().right > right + 1).length };
      });
      expect(fit).toEqual({ overflow: 0, cut: 0 });
    });
});
