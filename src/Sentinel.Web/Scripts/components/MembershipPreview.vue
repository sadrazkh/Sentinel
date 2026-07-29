<script setup>
/**
 * Live preview of what a membership edit will actually mean.
 *
 * The computation is deliberately *not* done here. Every change posts the form back to
 * `PreviewMembership`, which runs the same `IMembershipStatusResolver` the portal enforces
 * with. Reimplementing expiry and grace-period arithmetic in JavaScript would create a second
 * copy of the rule, and the day the two disagreed the editor would be confidently lying to
 * the operator about what they were about to save.
 */
import { onMounted, onUnmounted, ref } from 'vue';

const props = defineProps({
  form: { type: Object, required: true },
  previewUrl: { type: String, required: true },
  labels: { type: Object, required: true },
});

const DEBOUNCE_MS = 250;

const preview = ref(null);
const failed = ref(false);
const pending = ref(false);

let timer = null;
let inFlight = null;

async function refresh() {
  // A newer edit supersedes whatever is still in flight; otherwise a slow earlier response
  // could land last and show a stale answer.
  if (inFlight) {
    inFlight.abort();
  }

  const controller = new AbortController();
  inFlight = controller;
  pending.value = true;

  try {
    const response = await fetch(props.previewUrl, {
      method: 'POST',
      credentials: 'same-origin',
      // The form already carries the anti-forgery field, so posting it wholesale satisfies
      // the global validation filter without any special handling here.
      body: new FormData(props.form),
      headers: { Accept: 'application/json' },
      signal: controller.signal,
    });

    if (!response.ok) {
      throw new Error(`Preview failed with status ${response.status}`);
    }

    preview.value = await response.json();
    failed.value = false;
  } catch (error) {
    if (error.name === 'AbortError') {
      return;
    }

    // The preview is an aid, not a gate: if it cannot be produced, say so and let the
    // operator save anyway — the server validates the real submission regardless.
    failed.value = true;
    preview.value = null;
  } finally {
    if (inFlight === controller) {
      inFlight = null;
      pending.value = false;
    }
  }
}

function schedule() {
  window.clearTimeout(timer);
  timer = window.setTimeout(refresh, DEBOUNCE_MS);
}

onMounted(() => {
  props.form.addEventListener('input', schedule);
  props.form.addEventListener('change', schedule);
  refresh();
});

onUnmounted(() => {
  window.clearTimeout(timer);
  inFlight?.abort();
  props.form.removeEventListener('input', schedule);
  props.form.removeEventListener('change', schedule);
});
</script>

<template>
  <div class="card" style="padding: var(--space-4); background-color: var(--surface-canvas)">
    <div class="stat__label">{{ labels.heading }}</div>

    <p v-if="failed" class="text-sm text-muted" style="margin-block-start: var(--space-2)">
      {{ labels.failed }}
    </p>

    <div
      v-else-if="preview"
      class="cluster"
      style="margin-block-start: var(--space-3)"
      :style="{ opacity: pending ? 0.55 : 1, transition: 'opacity 150ms' }"
    >
      <span class="badge" :class="preview.badgeClass">{{ preview.status }}</span>

      <span class="badge badge--plain" :class="preview.grantsAccess ? 'badge--success' : 'badge--neutral'">
        {{ preview.grantsAccess ? labels.grantsAccess : labels.noAccess }}
      </span>

      <span v-if="preview.daysRemaining !== null" class="text-sm text-secondary numeric">
        {{ labels.daysRemaining }}: {{ preview.daysRemaining }}
      </span>

      <span v-if="preview.accessEndsAt" class="text-sm text-secondary">
        {{ labels.accessUntil }} {{ preview.accessEndsAt }}
      </span>
    </div>

    <div v-else class="skeleton skeleton--text" style="inline-size: 40%; margin-block-start: var(--space-3)"></div>
  </div>
</template>
