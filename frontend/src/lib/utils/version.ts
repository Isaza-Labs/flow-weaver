// Surfaces the application version in one place. Vite inlines this constant
// at build time so the bundle is plain text — no runtime fetch and no risk
// of mismatch with the deployed asset hash.
//
// The value is injected by `define` in vite.config.ts, which reads
// package.json in Node. Importing package.json from here instead would make
// the browser fetch /package.json, and Vite 7 serves a 403 for it (outside
// `server.fs.allow`) — which killed the whole app under `vite dev`.
declare const __APP_VERSION__: string;

export const APP_VERSION = __APP_VERSION__;
