/**
 * Toasts and confirmation dialogs, built on the platform's own <dialog> element.
 *
 * All text is inserted with textContent, never innerHTML: these helpers are routinely handed
 * server data and user-supplied names, and an innerHTML shortcut here would be an XSS sink
 * that no amount of Razor encoding elsewhere could compensate for.
 */

const TOAST_REGION_ID = 'sentinel-toast-region';
const DEFAULT_TOAST_MS = 4500;

function toastRegion() {
  let region = document.getElementById(TOAST_REGION_ID);

  if (!region) {
    region = document.createElement('div');
    region.id = TOAST_REGION_ID;
    region.className = 'toast-region';
    // Announced by screen readers without stealing focus.
    region.setAttribute('role', 'status');
    region.setAttribute('aria-live', 'polite');
    document.body.appendChild(region);
  }

  return region;
}

export function toast(message, { variant = 'info', duration = DEFAULT_TOAST_MS } = {}) {
  const element = document.createElement('div');
  element.className = `toast toast--${variant}`;
  element.textContent = message;

  toastRegion().appendChild(element);

  const remove = () => {
    element.style.opacity = '0';
    element.style.transform = 'translateY(8px)';
    window.setTimeout(() => element.remove(), 200);
  };

  const timer = window.setTimeout(remove, duration);

  element.addEventListener('click', () => {
    window.clearTimeout(timer);
    remove();
  });

  return remove;
}

/**
 * Confirmation gate for destructive actions. Resolves true only on explicit confirmation:
 * dismissing with Escape or the backdrop resolves false, so an accidental keypress can never
 * be read as approval.
 */
export function confirmDialog({ title, message, confirmLabel, cancelLabel, danger = true }) {
  return new Promise((resolve) => {
    const dialog = document.createElement('dialog');
    dialog.className = 'dialog';

    const body = document.createElement('div');
    body.className = 'dialog__body';

    const heading = document.createElement('h2');
    heading.className = 'dialog__title';
    heading.textContent = title;

    const text = document.createElement('p');
    text.className = 'dialog__text';
    text.textContent = message;

    body.append(heading, text);

    const actions = document.createElement('div');
    actions.className = 'dialog__actions';

    const cancel = document.createElement('button');
    cancel.type = 'button';
    cancel.className = 'btn btn--ghost';
    cancel.textContent = cancelLabel;

    const confirm = document.createElement('button');
    confirm.type = 'button';
    confirm.className = danger ? 'btn btn--danger' : 'btn btn--primary';
    confirm.textContent = confirmLabel;

    actions.append(cancel, confirm);
    dialog.append(body, actions);
    document.body.appendChild(dialog);

    let settled = false;
    const close = (result) => {
      if (settled) return;
      settled = true;
      resolve(result);
      dialog.close();
      dialog.remove();
    };

    cancel.addEventListener('click', () => close(false));
    confirm.addEventListener('click', () => close(true));
    dialog.addEventListener('cancel', (event) => {
      event.preventDefault();
      close(false);
    });

    dialog.showModal();
    cancel.focus();
  });
}

/**
 * Wires any element carrying data-confirm to a confirmation dialog. Used by forms that
 * perform destructive POSTs; the server still authorises the action independently.
 */
export function bindConfirmables(root = document) {
  root.querySelectorAll('[data-confirm]').forEach((element) => {
    if (element.dataset.confirmBound === 'true') {
      return;
    }

    element.dataset.confirmBound = 'true';

    element.addEventListener('click', async (event) => {
      if (element.dataset.confirmed === 'true') {
        element.dataset.confirmed = 'false';
        return;
      }

      event.preventDefault();

      const approved = await confirmDialog({
        title: element.dataset.confirmTitle || '',
        message: element.dataset.confirm,
        confirmLabel: element.dataset.confirmYes || 'OK',
        cancelLabel: element.dataset.confirmNo || 'Cancel',
        danger: element.dataset.confirmDanger !== 'false',
      });

      if (approved) {
        element.dataset.confirmed = 'true';
        element.click();
      }
    });
  });
}

/**
 * Wires any element carrying data-copy to the clipboard.
 *
 * The value is read from the attribute rather than from a sibling's text, so a page can offer a
 * masked display and still copy the whole thing — which is what the subscription link does.
 *
 * navigator.clipboard is unavailable on an insecure origin and can be refused by the user, so a
 * failure is reported rather than swallowed: silently doing nothing would leave somebody pasting
 * whatever was on their clipboard before into a VPN client.
 */
export function bindCopyables(root = document) {
  root.querySelectorAll('[data-copy]').forEach((element) => {
    if (element.dataset.copyBound === 'true') {
      return;
    }

    element.dataset.copyBound = 'true';

    element.addEventListener('click', async () => {
      const value = element.getAttribute('data-copy');
      if (!value) {
        return;
      }

      try {
        await navigator.clipboard.writeText(value);
        toast(element.dataset.copyDone || 'Copied', { variant: 'success' });
      } catch {
        // No message from the exception: it can carry the page's own URL, and this one is a
        // capability. The member is told what to do instead.
        toast(element.dataset.copyFailed || 'Copy failed', { variant: 'warning' });
      }
    });
  });
}
