import { test, expect } from '@playwright/test';

test.describe('Dashboard', () => {
  test('loads and shows stats cards', async ({ page }) => {
    await page.goto('/');
    await expect(page.locator('h2')).toContainText('Dashboard');
    // Should show stat cards
    await expect(page.locator('text=Devices')).toBeVisible();
    await expect(page.locator('text=Workflows')).toBeVisible();
    await expect(page.locator('text=Total Runs')).toBeVisible();
    await expect(page.locator('text=AI Skills')).toBeVisible();
  });

  test('navigation sidebar works', async ({ page }) => {
    await page.goto('/');
    // Check sidebar links exist
    await expect(page.locator('a[href="/workflows"]')).toBeVisible();
    await expect(page.locator('a[href="/devices"]')).toBeVisible();
    await expect(page.locator('a[href="/runs"]')).toBeVisible();
    await expect(page.locator('a[href="/services"]')).toBeVisible();
    await expect(page.locator('a[href="/adapters"]')).toBeVisible();
  });

  test('navigate to workflows page', async ({ page }) => {
    await page.goto('/');
    await page.click('a[href="/workflows"]');
    await expect(page).toHaveURL('/workflows');
  });

  test('navigate to devices page', async ({ page }) => {
    await page.goto('/');
    await page.click('a[href="/devices"]');
    await expect(page).toHaveURL('/devices');
  });

  test('navigate to adapters page', async ({ page }) => {
    await page.goto('/');
    await page.click('a[href="/adapters"]');
    await expect(page).toHaveURL('/adapters');
  });
});
