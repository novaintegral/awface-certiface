/*
 * Service worker "kill switch" do AWFace.
 *
 * O AWFace não usa service worker. Este domínio, porém, já hospedou uma aplicação Angular
 * com service worker (ngsw-worker.js), que continua instalado nos navegadores de quem a
 * acessou e serve uma versão antiga e quebrada da página (tela em branco).
 *
 * O navegador verifica periodicamente este mesmo arquivo. Ao recebê-lo, o worker antigo é
 * substituído por este, que apaga os caches, se desregistra e recarrega as abas abertas.
 */
self.addEventListener('install', () => self.skipWaiting());

self.addEventListener('activate', (event) => {
  event.waitUntil((async () => {
    const cacheNames = await caches.keys();
    await Promise.all(cacheNames.map((name) => caches.delete(name)));
    await self.registration.unregister();
    const windows = await self.clients.matchAll({ type: 'window' });
    windows.forEach((client) => client.navigate(client.url));
  })());
});
