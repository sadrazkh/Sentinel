<script setup>
/**
 * The grid of configs for one subscription, with a search box and a "copy all" action.
 *
 * A subscription commonly carries dozens of entries whose names differ by a country flag and a
 * number, so filtering is the difference between a usable page and a wall of near-identical
 * cards. That is real client state, which is what earns Vue its place here.
 */
import { computed, ref } from 'vue';
import ConfigCard from './ConfigCard.vue';

const props = defineProps({
  configs: { type: Array, required: true },
  labels: { type: Object, required: true },
});

const query = ref('');
const copiedAll = ref(false);

const visible = computed(() => {
  const needle = query.value.trim().toLocaleLowerCase();

  if (!needle) {
    return props.configs;
  }

  return props.configs.filter((config) =>
    [config.name, config.endpoint, config.protocol]
      .filter(Boolean)
      .some((field) => field.toLocaleLowerCase().includes(needle)),
  );
});

async function copyAll() {
  try {
    await navigator.clipboard.writeText(visible.value.map((c) => c.uri).join('\n'));
    copiedAll.value = true;
    window.setTimeout(() => { copiedAll.value = false; }, 2000);
  } catch {
    copiedAll.value = false;
  }
}
</script>

<template>
  <div>
    <div class="toolbar" v-if="configs.length > 3">
      <div class="field toolbar__search">
        <label class="visually-hidden" :for="'config-search'">{{ labels.searchLabel }}</label>
        <input
          id="config-search"
          v-model="query"
          class="input"
          type="search"
          autocomplete="off"
          :placeholder="labels.searchPlaceholder"
        />
      </div>

      <button type="button" class="btn btn--ghost btn--sm" @click="copyAll">
        {{ copiedAll ? labels.copied : labels.copyAll }}
      </button>
    </div>

    <div v-if="visible.length" class="grid grid--configs">
      <ConfigCard
        v-for="(config, index) in visible"
        :key="config.uri + index"
        :config="config"
        :labels="labels"
      />
    </div>

    <p v-else class="text-sm text-muted" style="padding:var(--space-4) 0">
      {{ labels.noMatches }}
    </p>
  </div>
</template>
