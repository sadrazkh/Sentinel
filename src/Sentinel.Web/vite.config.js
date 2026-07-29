import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';

const here = dirname(fileURLToPath(import.meta.url));

/**
 * Each entry below is a self-contained island mounted by one Razor view — the jQuery model,
 * not an SPA. MVC still owns routing, rendering and SEO; Vue only takes over the interactive
 * region of a page.
 *
 * The bundle resolves `vue` to the runtime-only build, whose templates are compiled here at
 * build time. That is what lets the Content-Security-Policy ship without `unsafe-eval`: the
 * in-browser template compiler would need `new Function`, and allowing it would undo most of
 * what the CSP is there to prevent.
 */
export default defineConfig({
  root: here,
  publicDir: false,
  resolve: {
    alias: {
      vue: 'vue/dist/vue.runtime.esm-bundler.js',
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
      },
      output: {
        entryFileNames: '[name].js',
        chunkFileNames: 'chunks/[name].[hash].js',
        assetFileNames: 'assets/[name][extname]',
      },
    },
  },
});
