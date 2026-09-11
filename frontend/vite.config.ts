import { readFileSync } from "node:fs";
import { sveltekit } from "@sveltejs/kit/vite";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vite";

// Read the version here, in Node, and inline it as a constant. Importing
// package.json from client code instead makes the browser request
// /package.json, which Vite 7 refuses to serve (it is outside `server.fs.allow`,
// which now covers only src/ and .svelte-kit/). That 403 aborts the root layout
// module and `vite dev` renders a blank page — the build was unaffected because
// Rollup inlines the import, so the breakage only ever showed up in dev.
const pkg = JSON.parse(
  readFileSync(new URL("./package.json", import.meta.url), "utf-8"),
) as { version: string };

export default defineConfig({
  define: {
    __APP_VERSION__: JSON.stringify(pkg.version),
  },
  plugins: [tailwindcss(), sveltekit()],
  server: {
    port: 5173,
    host: process.env.VITE_HOST || "localhost",
    proxy: {
      "/api": {
        target: "http://localhost:8080",
        changeOrigin: true,
        ws: true,
        timeout: 300000,
      },
      "/openapi": {
        target: "http://localhost:8080",
        changeOrigin: true,
      },
      "/scalar": {
        target: "http://localhost:8080",
        changeOrigin: true,
      },
    },
  },
});
