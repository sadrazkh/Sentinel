<script setup>
/**
 * The My Apps grid: search, availability filter, and the resulting cards.
 *
 * This is the whole reason a framework is on this page. Filtering is genuine client state,
 * and re-fetching a page of already-loaded cards to apply a text filter would be slower and
 * no more correct — the server has already decided what this member may see, and no amount of
 * client filtering can widen that set.
 */
import { computed, ref } from 'vue';
import AppCard from './AppCard.vue';

const props = defineProps({
  apps: { type: Array, required: true },
  labels: { type: Object, required: true },
});

const FILTERS = ['all', 'available', 'locked'];

const query = ref('');
const filter = ref('all');

const counts = computed(() => ({
  all: props.apps.length,
  available: props.apps.filter((app) => app.canLaunch).length,
  locked: props.apps.filter((app) => !app.canLaunch).length,
}));

const visible = computed(() => {
  const needle = query.value.trim().toLocaleLowerCase();

  return props.apps.filter((app) => {
    if (filter.value === 'available' && !app.canLaunch) return false;
    if (filter.value === 'locked' && app.canLaunch) return false;
    if (!needle) return true;

    return [app.name, app.key, app.description]
      .filter(Boolean)
      .some((field) => field.toLocaleLowerCase().includes(needle));
  });
});

const isFiltered = computed(() => query.value.trim() !== '' || filter.value !== 'all');
</script>

<template>
  <div>
    <div class="toolbar">
      <div class="field toolbar__search">
        <label class="visually-hidden" for="app-search">{{ labels.searchLabel }}</label>
        <input
          id="app-search"
          v-model="query"
          class="input"
          type="search"
          autocomplete="off"
          :placeholder="labels.searchPlaceholder"
        />
      </div>

      <div class="chip-group" role="group" :aria-label="labels.filterLabel">
        <button
          v-for="option in FILTERS"
          :key="option"
          type="button"
          class="chip"
          :aria-pressed="filter === option"
          @click="filter = option"
        >
          {{ labels.filters[option] }}
          <span class="chip__count">{{ counts[option] }}</span>
        </button>
      </div>
    </div>

    <div v-if="visible.length" class="grid grid--cards">
      <AppCard v-for="app in visible" :key="app.id" :app="app" :labels="labels" />
    </div>

    <!-- Two different empty states: "your filter matched nothing" is a very different message
         from "you have no applications yet", and collapsing them would leave a member who
         typed a typo thinking their access had been revoked. -->
    <div v-else class="empty-state">
      <div class="empty-state__icon" aria-hidden="true">
        <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor"
             stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">
          <circle cx="11" cy="11" r="7" />
          <path d="m20 20-3.6-3.6" />
        </svg>
      </div>
      <p class="empty-state__title">
        {{ isFiltered ? labels.emptyFilteredTitle : labels.emptyTitle }}
      </p>
      <p class="empty-state__body">
        {{ isFiltered ? labels.emptyFilteredBody : labels.emptyBody }}
      </p>
      <button v-if="isFiltered" type="button" class="btn btn--ghost btn--sm"
              @click="query = ''; filter = 'all'">
        {{ labels.clearFilters }}
      </button>
    </div>
  </div>
</template>
