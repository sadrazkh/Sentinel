/**
 * Login page enhancements.
 *
 * Deliberately framework-free. The sign-in form is the one page that has to work when
 * scripting is unavailable or fails to load, so Razor renders the real, functional form and
 * this file only adds affordances on top of it. Vue islands are used on pages where the
 * markup is genuinely dynamic (see Scripts/pages/apps.js), not here.
 */

function initPasswordReveal() {
  const toggle = document.querySelector('[data-password-toggle]');
  const input = document.getElementById('Password');

  if (!toggle || !input) {
    return;
  }

  const eyeOpen = toggle.querySelector('[data-icon="show"]');
  const eyeClosed = toggle.querySelector('[data-icon="hide"]');

  toggle.hidden = false;

  toggle.addEventListener('click', () => {
    const revealed = input.type === 'text';

    input.type = revealed ? 'password' : 'text';
    toggle.setAttribute('aria-pressed', String(!revealed));
    toggle.setAttribute(
      'aria-label',
      revealed ? toggle.dataset.labelShow : toggle.dataset.labelHide,
    );

    if (eyeOpen && eyeClosed) {
      eyeOpen.hidden = !revealed;
      eyeClosed.hidden = revealed;
    }

    // Keep the caret where the user left it.
    input.focus();
    input.setSelectionRange(input.value.length, input.value.length);
  });
}

/**
 * Caps Lock silently breaks password entry more often than anything else. The state is only
 * observable from a keyboard event, so it cannot be rendered server-side.
 */
function initCapsLockHint() {
  const hint = document.querySelector('[data-capslock-hint]');
  const input = document.getElementById('Password');

  if (!hint || !input) {
    return;
  }

  const update = (event) => {
    const active = typeof event.getModifierState === 'function' && event.getModifierState('CapsLock');
    hint.hidden = !active;
  };

  input.addEventListener('keydown', update);
  input.addEventListener('keyup', update);
  input.addEventListener('blur', () => {
    hint.hidden = true;
  });
}

/**
 * Blocks the double submit that produces two sign-in attempts — and therefore two rows in the
 * login-attempt table and two hits against the rate limiter — from one impatient click.
 */
function initSubmitGuard() {
  const form = document.querySelector('[data-login-form]');
  if (!form) {
    return;
  }

  const submit = form.querySelector('[type="submit"]');

  form.addEventListener('submit', () => {
    if (!submit) {
      return;
    }

    // Disabling outright would drop the button from the POST body, so the button stays
    // enabled for the browser and is only made inert for the user.
    submit.setAttribute('aria-disabled', 'true');
    submit.dataset.busy = 'true';

    const busyLabel = submit.dataset.labelBusy;
    if (busyLabel) {
      submit.textContent = busyLabel;
    }
  });
}

function boot() {
  initPasswordReveal();
  initCapsLockHint();
  initSubmitGuard();
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', boot);
} else {
  boot();
}
