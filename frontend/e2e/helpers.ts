import { Page, expect } from '@playwright/test';

const API = 'http://localhost:8090/api/v1';

/** Create a resource via API and return it */
export async function apiCreate(path: string, data: Record<string, unknown>) {
  const res = await fetch(`${API}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(data),
  });
  return res.json();
}

/** Delete a resource via API */
export async function apiDelete(path: string) {
  await fetch(`${API}${path}`, { method: 'DELETE' });
}

/** Get a resource via API */
export async function apiGet(path: string) {
  const res = await fetch(`${API}${path}`);
  return res.json();
}

/** Wait for page to be fully loaded and interactive */
export async function waitForPage(page: Page) {
  await page.waitForLoadState('networkidle');
}

/** Create a test ping service */
export async function createPingService() {
  return apiCreate('/services', {
    name: `e2e-ping-${Date.now()}`,
    type: 'ping',
    description: 'E2E test ping',
    target_mode: 'per_device',
    input_schema: { type: 'object', properties: { count: { type: 'integer', default: 2 } } },
  });
}

/** Create a test python snippet service */
export async function createSnippetService(name?: string, code?: string) {
  return apiCreate('/services', {
    name: name || `e2e-snippet-${Date.now()}`,
    type: 'python_snippet',
    description: 'E2E test snippet',
    target_mode: 'once',
    code: code || 'from flowweaver_runtime import set_output\nset_output({"e2e": True})',
    script_language: 'python',
    input_schema: { type: 'object', properties: {} },
  });
}

/** Create a test workflow with nodes and edges */
export async function createTestWorkflow(serviceId: string) {
  return apiCreate('/workflows', {
    name: `e2e-workflow-${Date.now()}`,
    description: 'E2E test workflow',
    nodes: [
      { id: '__start__', service_def_id: '__start__', x: 50, y: 200 },
      { id: 'step-1', service_def_id: serviceId, x: 300, y: 200 },
      { id: '__end__', service_def_id: '__end__', x: 550, y: 200 },
    ],
    edges: [
      { source: '__start__', target: 'step-1', type: 'success' },
      { source: 'step-1', target: '__end__', type: 'success' },
    ],
  });
}

/** Create a test adapter */
export async function createTestAdapter() {
  return apiCreate('/adapters', {
    name: `e2e-adapter-${Date.now()}`,
    type: 'generic_rest',
    base_url: 'https://httpbin.org',
    auth_config: { method: 'token', token: 'test123' },
    health_check: { path: '/get', expected_status: 200 },
  });
}
