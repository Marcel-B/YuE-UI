// Service worker for Web Push (src/YueUI.Api/Push). Deliberately no fetch handler: the app is only useful with the
// server reachable, and a cache would keep serving an old build after a deploy.

self.addEventListener('install', () => self.skipWaiting())
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()))

// Payload from PushNotifier.Payload: { title, body, tag, url }. Every push must show a notification: Safari revokes
// the subscription of a site that receives pushes silently.
self.addEventListener('push', (event) => {
  let message = {}
  try {
    message = event.data ? event.data.json() : {}
  } catch {
    message = { body: event.data ? event.data.text() : '' }
  }
  event.waitUntil(
    self.registration.showNotification(message.title || 'YuE UI', {
      body: message.body || '',
      tag: message.tag,
      icon: 'icon-192.png',
      badge: 'icon-192.png',
      data: { url: message.url || self.registration.scope },
    }),
  )
})

// Tapping a notification brings an open window of the app forward, or opens one.
self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const url = new URL(event.notification.data?.url || self.registration.scope, self.location.origin).href
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((windows) => {
      const open = windows.find((client) => client.url.startsWith(self.registration.scope))
      return open ? open.focus() : self.clients.openWindow(url)
    }),
  )
})
