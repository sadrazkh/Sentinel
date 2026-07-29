/**
 * Mounts a Vue island onto a server-rendered element and reads its data from `data-` attributes.
 *
 * The payload travels in an attribute rather than an inline `<script type="application/json">`
 * block for one specific reason: Razor HTML-encodes attribute values, and `dataset` decodes
 * them, so the data cannot break out of its context no matter what an application is named.
 * An inline script block would have needed `@Html.Raw` and its own escaping rules.
 */
import { createApp } from 'vue';

function readJson(element, name, fallback) {
  const raw = element.dataset[name];

  if (!raw) {
    return fallback;
  }

  try {
    return JSON.parse(raw);
  } catch (error) {
    console.error(`Island payload "${name}" is not valid JSON.`, error);
    return fallback;
  }
}

/**
 * @param {string} selector   element to mount on; absent means this page has no such island
 * @param {object} component  the root component
 * @param {(element: Element) => object} propsFrom  builds props from the element's dataset
 */
export function mountIsland(selector, component, propsFrom) {
  const element = document.querySelector(selector);

  if (!element) {
    return null;
  }

  const app = createApp(component, propsFrom(element));

  // A crash inside one island must not take the rest of the page down with it. The
  // server-rendered fallback stays in place and the failure is visible in the console.
  app.config.errorHandler = (error) => console.error('Island failed to render.', error);

  app.mount(element);
  return app;
}

export { readJson };
