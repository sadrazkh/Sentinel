/**
 * Dashboard island: the short strip of highlighted applications.
 *
 * It reuses the same AppCard component as My Apps, so a card looks and behaves identically on
 * both pages and there is only one implementation to change.
 */
import { h } from 'vue';
import AppCard from '@/components/AppCard.vue';
import { mountIsland, readJson } from '@/lib/island.js';

const FeaturedApps = {
  props: {
    apps: { type: Array, required: true },
    labels: { type: Object, required: true },
  },
  render() {
    return h(
      'div',
      { class: 'grid grid--cards' },
      this.apps.map((app) => h(AppCard, { key: app.id, app, labels: this.labels })),
    );
  },
};

mountIsland('#dashboard-apps-island', FeaturedApps, (element) => ({
  apps: readJson(element, 'apps', []),
  labels: readJson(element, 'labels', {}),
}));
