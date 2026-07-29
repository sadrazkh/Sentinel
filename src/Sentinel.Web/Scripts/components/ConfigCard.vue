<script setup>
/**
 * One proxy config.
 *
 * The card exists mainly so the raw URI can be copied: that string is what a member pastes
 * into their own client, and selecting it by hand from a wrapped block is miserable.
 *
 * Everything rendered here — the remark above all — arrives from a third-party subscription
 * server. It is interpolated as text, never as markup: `v-html` on this data would be handing
 * an XSS sink to whoever operates the subscription panel.
 */
import { ref } from 'vue';

const props = defineProps({
  config: { type: Object, required: true },
  labels: { type: Object, required: true },
});

const copied = ref(false);
const failed = ref(false);
let resetTimer = null;

async function copy() {
  failed.value = false;

  try {
    await navigator.clipboard.writeText(props.config.uri);
    copied.value = true;
  } catch {
    // Clipboard access needs a secure context and can be refused outright. Saying so beats
    // a button that silently does nothing.
    failed.value = true;
  }

  window.clearTimeout(resetTimer);
  resetTimer = window.setTimeout(() => {
    copied.value = false;
    failed.value = false;
  }, 2000);
}
</script>

<template>
  <article class="config-card">
    <div class="config-card__head">
      <span class="badge badge--accent badge--plain config-card__protocol">
        {{ config.protocol }}
      </span>
      <h3 class="config-card__name" :title="config.name">{{ config.name }}</h3>
    </div>

    <dl class="config-card__meta">
      <div v-if="config.endpoint">
        <dt>{{ labels.endpoint }}</dt>
        <dd dir="ltr">{{ config.endpoint }}</dd>
      </div>
      <div v-if="config.network">
        <dt>{{ labels.network }}</dt>
        <dd dir="ltr">{{ config.network }}</dd>
      </div>
      <div v-if="config.security">
        <dt>{{ labels.security }}</dt>
        <dd dir="ltr">{{ config.security }}</dd>
      </div>
      <div v-if="config.sni">
        <dt>{{ labels.sni }}</dt>
        <dd dir="ltr" class="truncate">{{ config.sni }}</dd>
      </div>
    </dl>

    <button type="button" class="btn btn--outline btn--sm btn--block" @click="copy">
      <span v-if="copied">{{ labels.copied }}</span>
      <span v-else-if="failed">{{ labels.copyFailed }}</span>
      <span v-else>{{ labels.copy }}</span>
    </button>
  </article>
</template>
