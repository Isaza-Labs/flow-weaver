// The session token lives in localStorage (see $lib/stores/auth.svelte), so
// SSR can't know whether the user is authenticated. Rendering server-side
// would always paint the logged-out shell and then hydrate into a redirect,
// flashing sidebar content. Disable SSR for the whole app — the deployment
// already proxies /api/* through hooks.server.ts, there is no data to
// server-render.
export const ssr = false;
