// Minimal SSE server that streams one chat turn slowly, so chat-focus.spec.ts
// can interact with the page mid-stream (click Stop, poke the thread). Playwright's
// route.fulfill() delivers a body in one shot, which can't reproduce that.
//
//   node e2e/fixtures/sse-server.mjs
//
// CORS-open because the page under test runs on another port.
import http from 'node:http';

const CORS = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Headers': '*',
  'Access-Control-Allow-Methods': 'POST, OPTIONS',
};

const frames = [
  { type: 'conversation', id: 'conv-1', is_new: true },
  { type: 'tool_start', name: 'get_run_details', args_preview: { run_id: 'r1' } },
  { type: 'tool_result', name: 'get_run_details', success: true, preview: { status: 'failed' } },
  { type: 'text', content: '## Diagnosis\n\nThe step failed because of a bad template.\n\n' },
  { type: 'text', content: 'Here is the fix.\n\n```json\n{"a":1}\n```\n' },
  { type: 'done', tokens_in: 10, tokens_out: 20, iterations: 2 },
];

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

http
  .createServer(async (req, res) => {
    if (req.method === 'OPTIONS') {
      res.writeHead(204, CORS);
      res.end();
      return;
    }
    req.resume();
    res.writeHead(200, {
      ...CORS,
      'Content-Type': 'text/event-stream',
      'Cache-Control': 'no-cache',
      Connection: 'keep-alive',
    });
    for (const f of frames) {
      res.write(`data: ${JSON.stringify(f)}\n\n`);
      await sleep(900);
    }
    res.end();
  })
  .listen(8099, () => console.log('slow SSE fixture on http://localhost:8099'));
