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
 * in the URL fragment. A notification about the proposals of a run also carries the run, which the
 * click forwards, so the application can open its inbox of the run (`openRun$`). A notification of
 * alerts (`alert` or `alert-digest`) belongs to no conversation: a click opens the host's alert list,
 * in an open window through `openAlerts$`, or else by opening `alerts` (`push.alertsUrl`).
 * Notifications carry a conversation, run, alert or digest id and short texts only.
 */
const settings = new URL(self.location.href).searchParams;
const appUrl = settings.get('app') || '/';
const alertsUrl = settings.get('alerts') || appUrl;
const iconUrl = settings.get('icon') || undefined;

/** The kinds of a notification of alerts, which opens the alert list. */
const alertKinds = ['alert', 'alert-digest'];

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));

self.addEventListener('push', event => {
  let payload = null;
  try {
    payload = event.data ? event.data.json() : null;
  } catch {
    payload = null;
  }
  if (!payload || payload.type !== 'nh-assistant') {
    return;
  }
  const alerts = alertKinds.includes(payload.kind);
  if (!alerts && typeof payload.conversationId !== 'string') {
    return;
  }
  const runId = typeof payload.runId === 'string' ? payload.runId : undefined;
  const alertId = alerts && typeof payload.alertId === 'string' ? payload.alertId : undefined;
  const digestId = alerts && typeof payload.digestId === 'string' ? payload.digestId : undefined;

  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    if (windows.some(client => client.focused && client.visibilityState === 'visible')) {
      for (const client of windows) {
        client.postMessage(alerts
          ? { type: 'nh-assistant.push', kind: payload.kind, alertId, digestId }
          : { type: 'nh-assistant.push', kind: payload.kind, conversationId: payload.conversationId, runId });
      }
      return;
    }

    const fallbackTag = alerts
      ? `nh-assistant-alert:${digestId || alertId || ''}`
      : `nh-assistant:${payload.conversationId}`;
    await self.registration.showNotification(String(payload.title || ''), {
      body: String(payload.body || ''),
      tag: String(payload.tag || fallbackTag),
      renotify: true,
      icon: iconUrl,
      data: alerts ? { alerts: true, alertId, digestId } : { conversationId: payload.conversationId, runId }
    });
  })());
});

self.addEventListener('notificationclick', event => {
  event.notification.close();
  const data = event.notification.data || {};
  if (data.alerts === true) {
    event.waitUntil(openAlerts(data));
    return;
  }
  const conversationId = data.conversationId;
  if (typeof conversationId !== 'string') {
    return;
  }
  const runId = typeof data.runId === 'string' ? data.runId : undefined;

  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const target = windows.find(client => client.focused) || windows[0];
    if (target) {
      await target.focus();
      target.postMessage({ type: 'nh-assistant.open-conversation', conversationId, runId });
      return;
    }

    const url = new URL(appUrl, self.location.origin);
    url.hash = runId
      ? `nh-assistant-run=${encodeURIComponent(runId)}.${encodeURIComponent(conversationId)}`
      : `nh-assistant-conversation=${encodeURIComponent(conversationId)}`;
    await self.clients.openWindow(url.href);
  })());
});

/** Opens the host's alert list: in an open window of the application, or in a new one. */
async function openAlerts(data) {
  const alertId = typeof data.alertId === 'string' ? data.alertId : undefined;
  const digestId = typeof data.digestId === 'string' ? data.digestId : undefined;
  const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
  const target = windows.find(client => client.focused) || windows[0];
  if (target) {
    await target.focus();
    target.postMessage({ type: 'nh-assistant.open-alerts', alertId, digestId });
    return;
  }

  await self.clients.openWindow(new URL(alertsUrl, self.location.origin).href);
}
