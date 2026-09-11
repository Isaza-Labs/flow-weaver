import { test, expect } from '@playwright/test';
import { apiCreate, apiDelete } from './helpers';

test.describe('Devices Page', () => {
  let device: any;

  test.beforeEach(async () => {
    device = await apiCreate('/devices', {
      name: `e2e-device-${Date.now()}`,
      ip_address: '10.99.99.1',
      platform: 'arista_eos',
      vendor: 'Arista',
      site: 'e2e-test',
      role: 'spine',
    });
  });

  test.afterEach(async () => {
    if (device?.id) await apiDelete(`/devices/${device.id}`).catch(() => {});
  });

  test('page loads and shows devices', async ({ page }) => {
    await page.goto('/devices');
    await page.waitForLoadState('networkidle');
    await expect(page.locator(`text=${device.name}`)).toBeVisible({ timeout: 10000 });
  });

  test('shows device details', async ({ page }) => {
    await page.goto('/devices');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(2000);
    const text = await page.textContent("body");
    expect(text).toContain(device.name);
    // Platform might be in a table cell that needs scroll
    await expect(page.locator('text=arista_eos').first()).toBeVisible({ timeout: 10000 });
  });

  test('new device button exists', async ({ page }) => {
    await page.goto('/devices');
    await page.waitForLoadState('networkidle');
    await expect(page.locator('button:has-text("New Device")')).toBeVisible({ timeout: 5000 });
  });
});
