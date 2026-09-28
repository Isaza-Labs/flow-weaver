# FlowWeaver frontend

The frontend is the SvelteKit application for the visual workflow builder, inventory, run monitoring, AI chat, administration, and the in-app manual. For a full local instance, start with the [quick start](../QUICK_START_GUIDE.md); the frontend alone still needs a running backend.

## Local development

Prerequisites: Node.js 20+ and a backend listening on `http://localhost:8080`.

```bash
cd frontend
npm ci
npm run dev
```

Open `http://localhost:5173`. The Vite development server proxies `/api`, `/openapi`, and `/scalar` to the backend on port 8080, so use the backend command and port shown in the [local development guide](../QUICK_START_GUIDE.md#5-local-development-no-docker).

## Checks and build

```bash
npm run check
npm run check:hints
npm run check:labels
npm run build
```

End-to-end tests use Playwright and require the application stack to be running:

```bash
npx playwright test
```

The in-app manual lives in [`src/routes/docs/`](./src/routes/docs/). Update its relevant chapter when changing a user-facing workflow.
