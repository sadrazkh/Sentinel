import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

const here = dirname(fileURLToPath(import.meta.url));

/**
 * Each entry below is a self-contained island mounted by one Razor view — the jQuery model,
 * not an SPA. MVC still owns routing, rendering and SEO; Vue only takes over the interactive
 * region of a page.
 *
 * Single-file components are compiled to render functions here, at build time, and the bundle
 * resolves `vue` to the runtime-only build. That is what lets the Content-Security-Policy ship
 * without `unsafe-eval`: an in-browser template compiler would need `new Function`, and
 * allowing it would undo most of what the CSP is there to prevent.
 */
export default defineConfig({
  root: here,
  publicDir: false,
  plugins: [vue()],
  resolve: {
    alias: {
      '@': resolve(here, 'Scripts'),
    },
  },
  define: {
    __VUE_OPTIONS_API__: 'false',
    __VUE_PROD_DEVTOOLS__: 'false',
    __VUE_PROD_HYDRATION_MISMATCH_DETAILS__: 'false',
  },
  build: {
    outDir: resolve(here, 'wwwroot/js/dist'),
    emptyOutDir: true,
    manifest: false,
    sourcemap: false,
    target: 'es2022',
    rollupOptions: {
      input: {
        'site': resolve(here, 'Scripts/site.js'),
        'page-login': resolve(here, 'Scripts/pages/login.js'),
        'page-apps': resolve(here, 'Scripts/pages/apps.js'),
        'page-dashboard': resolve(here, 'Scripts/pages/dashboard.js'),
      },
      output: {
        entryFileNames: '[name].js',
        // Vue itself lands in a shared chunk both islands import, so a member who visits the
        // dashboard and then My Apps downloads the framework once.
        chunkFileNames: 'chunks/[name].[hash].js',
        assetFileNames: 'assets/[name][extname]',
      },
    },
  },
});
