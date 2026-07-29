/**
 * Theme preference: 'light', 'dark' or 'auto'.
 *
 * 'auto' works by removing the data-theme attribute entirely and letting the stylesheet's
 * prefers-color-scheme block take over, so the OS setting is followed live — no media query
 * listener and no re-render needed.
 *
 * The initial value is applied by a tiny inline script in <head> (see _Layout) so the first
 * paint is already correct; this module only handles later changes.
 */

const STORAGE_KEY = 'sentinel.theme';
const VALID = ['light', 'dark', 'auto'];

export function getTheme() {
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY);
    return VALID.includes(stored) ? stored : 'auto';
  } catch {
    // Private browsing or blocked storage: fall back rather than break the page.
    return 'auto';
  }
}

export function applyTheme(theme) {
  const next = VALID.includes(theme) ? theme : 'auto';

  if (next === 'auto') {
    document.documentElement.removeAttribute('data-theme');
  } else {
    document.documentElement.setAttribute('data-theme', next);
  }

  try {
    window.localStorage.setItem(STORAGE_KEY, next);
  } catch {
    /* Preference simply will not persist. */
  }

  document.dispatchEvent(new CustomEvent('sentinel:theme-changed', { detail: { theme: next } }));
  return next;
}

/** Cycles light → dark → auto, which keeps the control to a single button. */
export function cycleTheme() {
  const order = ['light', 'dark', 'auto'];
  const index = order.indexOf(getTheme());
  return applyTheme(order[(index + 1) % order.length]);
}

export function resolvedTheme() {
  const theme = getTheme();
  if (theme !== 'auto') {
    return theme;
  }

  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}
