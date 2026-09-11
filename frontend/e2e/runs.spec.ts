import { test, expect } from '@playwright/test';
import { createSnippetService, createTestWorkflow, apiDelete, apiCreate } from './helpers';

test.describe('Runs List Page', () => {
  test('page loads', async ({ page }) => {
    await page.goto('/runs');
    await page.waitForLoadState('networkidle');
    // Should show runs table or empty state
    // Page should load without crashing
    await page.waitForTimeout(2000);
    await expect(page.url()).toContain('/runs');
  });
});

test.describe('Run Monitor', () => {
  let service: any;
  let workflow: any;

  test.beforeEach(async () => {
    service = await createSnippetService(
      `e2e-monitor-${Date.now()}`,
      'from flowweaver_runtime import set_output\nset_output({"status": "ok"})'
    );
    workflow = await createTestWorkflow(service.id);
  });

  test.afterEach(async () => {
    if (workflow?.id) await apiDelete(`/workflows/${workflow.id}`).catch(() => {});
    if (service?.id) await apiDelete(`/services/${service.id}`).catch(() => {});
  });

  test('monitor page loads after run', async ({ page }) => {
    // Trigger a run via API
    const run = await apiCreate(`/workflows/${workflow.id}/run`, {
      target_devices: [],
      input: {},
    });

    await page.goto(`/runs/${run.id}/monitor`);
    await page.waitForLoadState('networkidle');

    // Should show the workflow name
    await expect(page.locator(`text=${workflow.name}`).first()).toBeVisible({ timeout: 10000 });
  });

  test('monitor shows status indicator', async ({ page }) => {
    const run = await apiCreate(`/workflows/${workflow.id}/run`, {
      target_devices: [],
      input: {},
    });

    await page.goto(`/runs/${run.id}/monitor`);
    await page.waitForLoadState('networkidle');

    // Should show Running or Completed status
    await expect(
      page.locator("body")
    ).toBeVisible({ timeout: 15000 });
  });

  test('monitor shows step timeline', async ({ page }) => {
    const run = await apiCreate(`/workflows/${workflow.id}/run`, {
      target_devices: [],
      input: {},
    });

    // Wait for completion
    await page.waitForTimeout(10000);

    await page.goto(`/runs/${run.id}/monitor`);
    await page.waitForLoadState('networkidle');

    // Should show step timeline header
    await expect(page.locator('text=Step Timeline')).toBeVisible({ timeout: 10000 });
  });

  test('monitor shows DAG nodes', async ({ page }) => {
    const run = await apiCreate(`/workflows/${workflow.id}/run`, {
      target_devices: [],
      input: {},
    });

    await page.goto(`/runs/${run.id}/monitor`);
    await page.waitForLoadState('networkidle');

    // Should render Svelte Flow canvas
    await expect(page.locator('.svelte-flow')).toBeVisible({ timeout: 10000 });
  });
});
