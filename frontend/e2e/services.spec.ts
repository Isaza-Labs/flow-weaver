import { test, expect } from '@playwright/test';
import { createSnippetService, apiDelete } from './helpers';

test.describe('Services List Page', () => {
  let service: any;

  test.beforeEach(async () => {
    service = await createSnippetService();
  });

  test.afterEach(async () => {
    if (service?.id) await apiDelete(`/services/${service.id}`).catch(() => {});
  });

  test('page loads and shows services', async ({ page }) => {
    await page.goto('/services');
    await page.waitForLoadState('networkidle');
    await expect(page.locator(`text=${service.name}`)).toBeVisible({ timeout: 10000 });
  });

  test('service name links to editor', async ({ page }) => {
    await page.goto('/services');
    await page.waitForLoadState('networkidle');
    await page.locator(`a:has-text("${service.name}")`).click();
    await expect(page).toHaveURL(`/services/${service.id}`);
  });
});

test.describe('Service Editor Page', () => {
  let service: any;

  test.beforeEach(async () => {
    service = await createSnippetService(
      `e2e-editor-${Date.now()}`,
      'from flowweaver_runtime import set_output\nset_output({"hello": "world"})'
    );
  });

  test.afterEach(async () => {
    if (service?.id) await apiDelete(`/services/${service.id}`).catch(() => {});
  });

  test('editor page loads', async ({ page }) => {
    await page.goto(`/services/${service.id}`);
    await page.waitForLoadState('networkidle');
    await expect(page.locator(`text=${service.name}`)).toBeVisible({ timeout: 10000 });
  });

  test('shows service type badge', async ({ page }) => {
    await page.goto(`/services/${service.id}`);
    await page.waitForLoadState('networkidle');
    // Type badge might render as text or in a specific element
    await page.waitForTimeout(1000);
    const pageText = await page.textContent('body');
    expect(pageText).toContain('python_snippet');
  });

  test('shows code editor with actual code', async ({ page }) => {
    await page.goto(`/services/${service.id}`);
    await page.waitForLoadState('networkidle');
    // The code textarea should contain the snippet code
    const codeArea = page.locator('textarea').first();
    await expect(codeArea).toBeVisible({ timeout: 5000 });
    const value = await codeArea.inputValue();
    expect(value).toContain('set_output');
  });

  test('save button exists', async ({ page }) => {
    await page.goto(`/services/${service.id}`);
    await page.waitForLoadState('networkidle');
    await expect(page.locator('button:has-text("Save")')).toBeVisible({ timeout: 5000 });
  });

  test('back link exists', async ({ page }) => {
    await page.goto(`/services/${service.id}`);
    await page.waitForLoadState('networkidle');
    // Back link might say different things
    await expect(page.locator('a[href="/services"]').first()).toBeVisible({ timeout: 5000 });
  });

  test('shows settings panel with fields', async ({ page }) => {
    await page.goto(`/services/${service.id}`);
    await page.waitForLoadState('networkidle');
    await expect(page.locator('text=Target Mode')).toBeVisible({ timeout: 5000 });
    await expect(page.locator('text=Max Parallel')).toBeVisible({ timeout: 5000 });
    await expect(page.locator('text=Timeout')).toBeVisible({ timeout: 5000 });
  });
});
