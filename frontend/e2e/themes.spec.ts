import { test, expect, type Page } from '@playwright/test';

// User-authored themes have no stylesheet in the bundle: their CSS variables
// are generated in the browser from a few base colours and injected. These
// pin the end of that chain — that selecting one actually repaints the app —
// because every piece can typecheck and still leave the page unstyled.
//
// Run the app first (see e2e/fixtures/chat-stub.ts for the two options).

const BASE = process.env.E2E_BASE || 'http://localhost:4173';

const THEME_ID = 'aaaaaaaa-1111-2222-3333-444444444444';
const PRIMARY = '#7c6cf0';

const THEMES = {
  data: [
    {
      theme_id: THEME_ID,
      name: 'Midnight ops',
      description: 'High contrast for the NOC wall.',
      colors: { primary: PRIMARY, surface: '#2a2d3a' },
      // A style override rides along with the colours — roundness lands as
      // scaled --radius-* variables in the injected rule.
      settings: { roundness: 1.5 },
      is_shared: true,
      owner_user_id: null,
      can_edit: true,
      created_at: '2026-08-06T10:00:00Z',
      updated_at: '2026-08-06T10:00:00Z',
    },
  ],
  total: 1,
};

async function stub(page: Page) {
  await page.addInitScript(() => {
    localStorage.setItem(
      'flowweaver:auth',
      JSON.stringify({
        accessToken: 't',
        refreshToken: 'r',
        userId: '00000000-0000-0000-0000-000000000001',
        username: 'e2e',
        role: 'admin',
        expiresAt: Date.now() + 86_400_000,
      }),
    );
  });
  await page.route(
    (u) => u.pathname.startsWith('/api/'),
    (r) =>
      r.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ data: [], items: [], total: 0 }),
      }),
  );
  await page.route(
    (u) => u.pathname === '/api/themes',
    (r) =>
      r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(THEMES) }),
  );
}

/** What the browser actually resolves `--color-primary-500` to, as rgb(). */
const resolvedPrimary = (page: Page) =>
  page.evaluate(() =>
    getComputedStyle(document.documentElement).getPropertyValue('--color-primary-500').trim(),
  );

test('selecting a saved theme repaints the app', async ({ page }) => {
  await stub(page);
  await page.goto(BASE + '/themes');
  await expect(page.getByText('Midnight ops')).toBeVisible();

  await page.getByRole('button', { name: 'Use Midnight ops' }).click();

  await expect
    .poll(() => page.evaluate(() => document.documentElement.getAttribute('data-theme')))
    .toBe(`fw-custom-${THEME_ID}`);
  // The generated stylesheet has to have landed too — the attribute alone
  // would leave the app on the inherited brand palette.
  expect(await resolvedPrimary(page)).toBe(PRIMARY);
  // And the style settings with it: roundness 1.5 scales the default
  // 0.375rem medium radius that `rounded-md` resolves through.
  expect(
    await page.evaluate(() =>
      getComputedStyle(document.documentElement).getPropertyValue('--radius-md').trim(),
    ),
  ).toBe('0.563rem');
});

test('the choice survives a reload without flashing the default palette', async ({ page }) => {
  await stub(page);
  await page.goto(BASE + '/themes');
  await page.getByRole('button', { name: 'Use Midnight ops' }).click();
  await expect
    .poll(() => page.evaluate(() => document.documentElement.getAttribute('data-theme')))
    .toBe(`fw-custom-${THEME_ID}`);

  // Block the API so nothing can re-inject the CSS after load: what survives
  // is purely what app.html replayed from the cache pre-paint.
  await page.route((u) => u.pathname === '/api/themes', (r) => r.abort());
  await page.goto(BASE + '/runs');

  expect(await resolvedPrimary(page)).toBe(PRIMARY);
});

test('a custom theme appears in the palette picker', async ({ page }) => {
  await stub(page);
  await page.goto(BASE + '/themes');
  await expect(page.getByText('Midnight ops')).toBeVisible();

  await page.getByRole('button', { name: 'Pick a theme' }).click();
  const menu = page.getByRole('menu');
  await expect(menu.getByRole('menuitem', { name: /Midnight ops/ })).toBeVisible();
  // Built-ins stay listed alongside it.
  await expect(menu.getByRole('menuitem', { name: /Editorial/ })).toBeVisible();
  await expect(menu.getByRole('menuitem', { name: 'Create a theme' })).toBeVisible();
});

// The editor's preview must not touch the document — it scopes the theme to
// its own subtree so you can audition colours while still reading the page.
test('the editor preview does not leak into the page', async ({ page }) => {
  await stub(page);
  await page.goto(BASE + '/themes');
  await expect(page.getByText('Midnight ops')).toBeVisible();
  const before = await resolvedPrimary(page);

  await page.getByLabel('Primary hex value').fill('#ff0000');

  // The preview repainted…
  await expect
    .poll(() =>
      page.evaluate(() => {
        const el = document.querySelector('[data-theme-preview] .bg-primary-500') as HTMLElement | null;
        return el ? getComputedStyle(el).backgroundColor : null;
      }),
    )
    .toBe('rgb(255, 0, 0)');
  // …while the app around it did not.
  expect(await resolvedPrimary(page)).toBe(before);
});
