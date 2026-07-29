/**
 * My Apps island. Razor renders the mount point, the payload and a plain-list fallback for
 * visitors without scripting; Vue takes over the region on mount.
 */
import AppGrid from '@/components/AppGrid.vue';
import { mountIsland, readJson } from '@/lib/island.js';

mountIsland('#apps-island', AppGrid, (element) => ({
  apps: readJson(element, 'apps', []),
  labels: readJson(element, 'labels', {}),
}));
