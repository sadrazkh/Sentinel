/**
 * Loaded by every page. Deliberately free of Vue: theme switching and confirmation prompts
 * are DOM plumbing, and mounting a framework for them would be pure overhead. Vue is reserved
 * for pages with real interactive state (see Scripts/pages/*).
 */

import { applyTheme, cycleTheme, getTheme } from './lib/theme.js';
import { bindConfirmables, confirmDialog, toast } from './lib/ui.js';
import { http } from './lib/http.js';

const THEME_LABEL_ATTRIBUTES = {
  light: 'data-label-light',
  dark: 'data-label-dark',
  auto: 'data-label-auto',
};

function syncThemeToggle(button, theme) {
  const labelAttribute = THEME_LABEL_ATTRIBUTES[theme];
  const label = button.getAttribute(labelAttribute) || theme;

  button.setAttribute('aria-label', label);
  button.setAttribute('title', label);
  button.dataset.themeState = theme;
}

function initThemeToggle() {
  const button = document.querySelector('[data-theme-toggle]');
  if (!button) {
    return;
  }

  syncThemeToggle(button, getTheme());

  button.addEventListener('click', () => {
    syncThemeToggle(button, cycleTheme());
  });
}

function initTimestamps() {
  // Server-rendered timestamps carry the UTC instant in datetime="" and a server-side
  // rendering as text. Where the browser knows a narrower time zone than the profile does,
  // the title gives the viewer the exact local value on hover.
  document.querySelectorAll('time[datetime]').forEach((element) => {
    if (element.title) {
      return;
    }

    const parsed = new Date(element.getAttribute('datetime'));
    if (!Number.isNaN(parsed.valueOf())) {
      element.title = parsed.toLocaleString();
    }
  });
}

function boot() {
  applyTheme(getTheme());
  initThemeToggle();
  initTimestamps();
  bindConfirmables();
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', boot);
} else {
  boot();
}

// A single namespace for page scripts and inline handlers to reach the shared helpers.
window.sentinel = { toast, confirmDialog, http, applyTheme, getTheme };
