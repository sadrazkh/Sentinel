/**
 * Configs island. Razor renders one mount point per subscription, each carrying that
 * subscription's entries; Vue takes over the grid on mount.
 */
import ConfigGrid from '@/components/ConfigGrid.vue';
import { mountIsland, readJson } from '@/lib/island.js';

// A member can hold several subscriptions, so every mount point is wired up rather than the
// first one only.
document.querySelectorAll('[data-config-island]').forEach((element, index) => {
  element.id ||= `config-island-${index}`;

  mountIsland(`#${element.id}`, ConfigGrid, (host) => ({
    configs: readJson(host, 'configs', []),
    labels: readJson(host, 'labels', {}),
  }));
});
