/**
 * Thin fetch wrapper for the portal's own endpoints.
 *
 * Its whole reason to exist is the anti-forgery header: the token is rendered into a hidden
 * input by Razor, and every unsafe request must carry it or the server rejects the call.
 * Centralising that here means no call site can forget it.
 *
 * Requests are same-origin only. The CSP restricts connect-src to 'self' as well, so this is
 * belt and braces rather than the only guard.
 */

const UNSAFE_METHODS = ['POST', 'PUT', 'PATCH', 'DELETE'];

function antiForgeryToken() {
  const input = document.querySelector('input[name="__RequestVerificationToken"]');
  return input ? input.value : null;
}

export class HttpError extends Error {
  constructor(status, payload) {
    super(`Request failed with status ${status}`);
    this.name = 'HttpError';
    this.status = status;
    this.payload = payload;
  }
}

export async function request(url, { method = 'GET', body, signal, headers = {} } = {}) {
  const upperMethod = method.toUpperCase();

  const finalHeaders = {
    Accept: 'application/json',
    'X-Requested-With': 'fetch',
    ...headers,
  };

  if (UNSAFE_METHODS.includes(upperMethod)) {
    const token = antiForgeryToken();
    if (token) {
      finalHeaders.RequestVerificationToken = token;
    }
  }

  if (body !== undefined && !(body instanceof FormData)) {
    finalHeaders['Content-Type'] = 'application/json';
  }

  const response = await fetch(url, {
    method: upperMethod,
    credentials: 'same-origin',
    redirect: 'error',
    signal,
    headers: finalHeaders,
    body: body === undefined ? undefined : body instanceof FormData ? body : JSON.stringify(body),
  });

  const isJson = (response.headers.get('content-type') || '').includes('application/json');
  const payload = isJson ? await response.json().catch(() => null) : await response.text();

  if (!response.ok) {
    throw new HttpError(response.status, payload);
  }

  return payload;
}

export const http = {
  get: (url, options) => request(url, { ...options, method: 'GET' }),
  post: (url, body, options) => request(url, { ...options, method: 'POST', body }),
  put: (url, body, options) => request(url, { ...options, method: 'PUT', body }),
  patch: (url, body, options) => request(url, { ...options, method: 'PATCH', body }),
  delete: (url, options) => request(url, { ...options, method: 'DELETE' }),
};
