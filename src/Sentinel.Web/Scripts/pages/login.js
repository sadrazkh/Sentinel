/**
 * Login page enhancements.
 *
 * Deliberately framework-free. The sign-in form is the one page that has to work when
 * scripting is unavailable or fails to load, so Razor renders the real, functional form and
 * this file only adds affordances on top of it. Vue islands are used on pages where the
 * markup is genuinely dynamic (see Scripts/pages/apps.js), not here.
 */

/**
 * Shows or hides an element by attribute.
 *
 * Works for any element, including SVG. The `hidden` property only exists on HTMLElement, so
 * `svgElement.hidden = true` compiles, runs, and does nothing at all — the kind of failure that
 * looks like a CSS problem for an hour.
 */
function setHidden(element, hide) {
  if (hide) {
    element.setAttribute('hidden', '');
  } else {
    element.removeAttribute('hidden');
  }
}

/**
 * Wires every password-reveal toggle on the page.
 *
 * Each toggle finds its own input inside the shared `.field__control` wrapper rather than looking
 * up a fixed id, so the same markup works for the sign-in form and for the admin form that takes
 * a VPN panel's API token. Binding to an id would have quietly done nothing on the second one.
 */
function initPasswordReveal() {
  for (const toggle of document.querySelectorAll('[data-password-toggle]')) {
    const control = toggle.closest('.field__control');
    const input = control && control.querySelector('input[type="password"], input[type="text"]');

    if (!input) {
      continue;
    }

    const eyeOpen = toggle.querySelector('[data-icon="show"]');
    const eyeClosed = toggle.querySelector('[data-icon="hide"]');

    // Revealed only after the script is in place, so a visitor without scripting never sees a
    // control that would do nothing.
    toggle.hidden = false;

    toggle.addEventListener('click', () => {
      const revealed = input.type === 'text';

      input.type = revealed ? 'password' : 'text';
      toggle.setAttribute('aria-pressed', String(!revealed));
      toggle.setAttribute(
        'aria-label',
        revealed ? toggle.dataset.labelShow : toggle.dataset.labelHide,
      );

      // The icon shows the action the button performs, not the current state: while the password
      // is visible the button offers to hide it, so it carries the crossed-out eye.
      //
      // Set as an attribute, not through the `hidden` property. `hidden` is an IDL attribute of
      // HTMLElement, and an <svg> is an SVGElement — assigning `svg.hidden` silently creates a
      // plain JavaScript property and never touches the DOM, so the icons would never swap.
      if (eyeOpen && eyeClosed) {
        setHidden(eyeOpen, !revealed);
        setHidden(eyeClosed, revealed);
      }

      // Keep the caret where the user left it. Guarded because setSelectionRange throws on input
      // types that do not support selection, and the type was just reassigned.
      input.focus();

      try {
        input.setSelectionRange(input.value.length, input.value.length);
      } catch {
        /* selection unsupported for this input; focus alone is enough */
      }
    });
  }
}

/**
 * Caps Lock silently breaks password entry more often than anything else. The state is only
 * observable from a keyboard event, so it cannot be rendered server-side.
 */
function initCapsLockHint() {
  const hint = document.querySelector('[data-capslock-hint]');
  const input = document.getElementById('Password');

  // Only the sign-in form carries the hint. An API token is not a password somebody types from
  // memory, so caps lock is not the failure mode there.
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
