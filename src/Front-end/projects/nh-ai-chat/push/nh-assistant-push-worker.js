/*
 * Web Push worker of @newheap/platform-ai-chat.
 *
 * Copy this file into the application's assets, for example to /nh-assistant/, and set
 * `push.serviceWorkerUrl` in `provideNhAssistant`. The assistant registers it with its own
 * scope, so it never controls pages and does not conflict with an application service worker.
 *
 * It shows a notification only when no window of the application is focused; otherwise it
 * forwards the message to the open windows, which already show the conversation live. A click
 * focuses an open window and opens the conversation there, or opens `app` with the conversation
 * in the URL fragment. Notifications carry a conversation id and short texts only.
 */
const settings = new URL(self.location.href).searchParams;
const appUrl = settings.get('app') || '/';
const iconUrl = settings.get('icon') || undefined;

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));

self.addEventListener('push', event => {
  let payload = null;
  try {
    payload = event.data ? event.data.json() : null;
  } catch {
    payload = null;
  }
  if (!payload || payload.type !== 'nh-assistant' || typeof payload.conversationId !== 'string') {
    return;
  }

  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    if (windows.some(client => client.focused && client.visibilityState === 'visible')) {
      for (const client of windows) {
        client.postMessage({ type: 'nh-assistant.push', kind: payload.kind, conversationId: payload.conversationId });
      }
      return;
    }

    await self.registration.showNotification(String(payload.title || ''), {
      body: String(payload.body || ''),
      tag: String(payload.tag || `nh-assistant:${payload.conversationId}`),
      renotify: true,
      icon: iconUrl,
      data: { conversationId: payload.conversationId }
    });
  })());
});

self.addEventListener('notificationclick', event => {
  event.notification.close();
  const conversationId = event.notification.data && event.notification.data.conversationId;
  if (typeof conversationId !== 'string') {
    return;
  }

  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const target = windows.find(client => client.focused) || windows[0];
    if (target) {
      await target.focus();
      target.postMessage({ type: 'nh-assistant.open-conversation', conversationId });
      return;
    }

    const url = new URL(appUrl, self.location.origin);
    url.hash = `nh-assistant-conversation=${encodeURIComponent(conversationId)}`;
    await self.clients.openWindow(url.href);
  })());
});
