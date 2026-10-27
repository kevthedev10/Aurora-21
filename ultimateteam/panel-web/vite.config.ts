/// <reference types="vitest/config" />
// The React operator panel is served at /panel/ from the embedded Resources/panel/ folder.
import { fileURLToPath } from 'node:url';
import { defineConfig, type ProxyOptions } from 'vite';
import react from '@vitejs/plugin-react';

// Generated frontend files only: downloaded card images live in the separate panel-assets folder.
const outDir = fileURLToPath(new URL('../server/src/Aurora21.UltimateTeam.Server/Resources/panel', import.meta.url));

// Development only: a running local panel (loopback). Its access checks refuse a request whose Origin differs from
// its Host, so the proxy presents the panel's own origin, and it adds no X-Forwarded-* headers (xfwd stays off).
const panel = process.env.AURORA21_PANEL_URL ?? 'http://127.0.0.1:18448';
const toPanel: ProxyOptions = {
  target: panel,
  changeOrigin: true,
  configure: (proxy) => {
    proxy.on('proxyReq', (request, incoming) => {
      if (incoming.headers.origin) request.setHeader('origin', new URL(panel).origin);
    });
  },
};

export default defineConfig({
  base: '/panel/',
  plugins: [react()],
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
    proxy: { '/panel/api': toPanel, '/panel/assets': toPanel, '/panel/heads': toPanel },
  },
  build: {
    outDir,
    emptyOutDir: true,
    // /panel/app/*: never under /panel/assets/, which is the downloaded card images.
    assetsDir: 'app',
    // No data: URIs: the page's CSP allows fonts and scripts from 'self' only.
    assetsInlineLimit: 0,
    sourcemap: false,
    modulePreload: { polyfill: false },
    target: 'es2023',
    rolldownOptions: {
      output: {
        codeSplitting: {
          groups: [
            { name: 'react', test: /node_modules[\\/](?:react|react-dom|scheduler)[\\/]/ },
            { name: 'motion', test: /node_modules[\\/](?:motion|motion-dom|motion-utils|framer-motion)[\\/]/ },
            { name: 'query', test: /node_modules[\\/]@tanstack[\\/]/ },
            { name: 'radix', test: /node_modules[\\/](?:@radix-ui|react-remove-scroll|react-remove-scroll-bar|react-style-singleton)[\\/]/ },
            { name: 'card-editor', test: /panel-web[\\/]src[\\/]pages[\\/]cards[\\/]/ },
          ],
        },
      },
    },
  },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.{ts,tsx}'],
    setupFiles: ['test/setup.ts'],
  },
});
