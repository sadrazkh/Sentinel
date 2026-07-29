<script setup>
/**
 * One application in the catalogue.
 *
 * Every string it renders — name, description, badge label, lock reason — arrives already
 * translated from the server. Keeping the translation on the server means there is one
 * message catalogue rather than two, and the component stays free of any locale logic.
 *
 * Note the absence of a destination URL. The card links to the portal's own launch endpoint;
 * the real address of the application is never sent to the browser, so hiding the button is
 * not what enforces access — the server is.
 */
defineProps({
  app: { type: Object, required: true },
  labels: { type: Object, required: true },
});

function initial(name) {
  return (name || '?').trim().charAt(0).toUpperCase();
}
</script>

<template>
  <article class="app-card" :class="app.canLaunch ? 'app-card--available' : 'app-card--locked'">
    <div class="app-card__head">
      <div class="app-card__icon" aria-hidden="true">
        <!-- v-html is never used here; an icon is either an image the server vouched for or a letter. -->
        <img v-if="app.iconUrl" :src="app.iconUrl" alt="" loading="lazy" />
        <span v-else>{{ initial(app.name) }}</span>
      </div>

      <div class="app-card__titles">
        <h3 class="app-card__name">{{ app.name }}</h3>
        <p class="app-card__subtitle">{{ app.key }}</p>
      </div>
    </div>

    <div class="app-card__badges">
      <span class="badge" :class="app.badgeClass">{{ app.badgeLabel }}</span>
      <span v-if="app.isBeta" class="badge badge--warning">{{ labels.beta }}</span>
      <span v-if="app.tierLabel" class="badge badge--accent badge--plain">{{ app.tierLabel }}</span>
    </div>

    <p v-if="app.description" class="app-card__description">{{ app.description }}</p>

    <div class="app-card__footer">
      <a
        v-if="app.canLaunch"
        class="btn btn--primary"
        :href="app.openUrl"
        target="_blank"
        rel="noopener noreferrer"
      >
        {{ labels.open }}
      </a>
      <p v-else class="app-card__reason">{{ app.reason }}</p>
    </div>
  </article>
</template>
