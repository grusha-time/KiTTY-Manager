/* global browser, KITTY */
"use strict";
// Stable container identities are stored by the extension. Ports are deliberately
// memory-only: a restored tab must not reuse a port from a previous manager run.
const routes = new Map();
const completed = new Map();
let online = false;
let lastError = "";
let containerIds = {};
let startupTabs = [];
const ownOrigin = browser.runtime.getURL("");
const bridgeRequest = r => r.url === KITTY.url &&
  [r.originUrl, r.documentUrl].some(u => u && u.startsWith(ownOrigin));
const blocked = {type: "http", host: "127.0.0.1", port: KITTY.blockedPort};
function routeFor(r) { return online ? routes.get(r.cookieStoreId) : undefined; }
browser.proxy.onRequest.addListener(r => {
  if (bridgeRequest(r)) return {type: "direct"};
  const route = routeFor(r);
  return route
    ? [{type: "socks", host: "127.0.0.1", port: route.port, proxyDNS: true,
        connectionIsolationKey: route.key}, null]
    : [blocked, null];
}, {urls: ["<all_urls>"]});
browser.webRequest.onBeforeRequest.addListener(r => {
  return {cancel: !bridgeRequest(r) && !routeFor(r)};
}, {urls: ["<all_urls>"]}, ["blocking"]);

async function container(key, name) {
  let id = containerIds[key];
  if (id) {
    try { await browser.contextualIdentities.get(id); return id; } catch (_) { /* user deleted it */ }
  }
  const identity = await browser.contextualIdentities.create({name, color: "blue", icon: "briefcase"});
  id = identity.cookieStoreId;
  containerIds[key] = id;
  await browser.storage.local.set({containerIds});
  return id;
}
async function poll() {
  let acknowledgements = [];
  for (;;) {
    try {
      const tabs = await browser.tabs.query({});
      const activeKeys = [...routes.entries()]
        .filter(([id]) => tabs.some(t => t.cookieStoreId === id))
        .map(([, route]) => route.key);
      const response = await fetch(KITTY.url, {
        method: "POST", headers: {"Content-Type": "application/json", "Authorization": "Bearer " + KITTY.token},
        body: JSON.stringify({acks: acknowledgements, activeKeys}),
        signal: AbortSignal.timeout(3000), cache: "no-store", credentials: "omit"
      });
      if (!response.ok) throw new Error("Bridge HTTP " + response.status);
      const state = await response.json();
      lastError = "";
      acknowledgements = [];
      const next = new Map();
      for (const route of state.routes) {
        const id = await container(route.key, route.name);
        next.set(id, route);
      }
      routes.clear();
      for (const [id, route] of next) routes.set(id, route);
      online = true;
      for (const command of state.commands) {
        if (!completed.has(command.id)) {
          let error = null;
          try {
            const id = containerIds[command.key];
            if (!routes.has(id)) throw new Error("Маршрут контейнера недоступен");
            await browser.tabs.create({url: command.url, cookieStoreId: id});
          } catch (e) { console.error("KiTTY container open", command.key, e); error = String(e); }
          completed.set(command.id, {id: command.id, error});
        }
        acknowledgements.push(completed.get(command.id));
      }
      if (acknowledgements.some(a => a.error === null)) {
        // Close only our marked launcher tab, after a real tab exists.
        // If the user already navigated it elsewhere, preserve that tab.
        for (const tabId of startupTabs) {
          try {
            const tab = await browser.tabs.get(tabId);
            if (tab.url === KITTY.startupUrl) await browser.tabs.remove(tabId);
          } catch (_) { /* already closed */ }
        }
        startupTabs = [];
      }
      // Keep deduplication only for commands still awaiting acknowledgement.
      const pending = new Set(state.commands.map(c => c.id));
      for (const id of completed.keys()) if (!pending.has(id)) completed.delete(id);
    } catch (e) {
      if (String(e) !== lastError) console.error("KiTTY bridge", String(e));
      lastError = String(e);
      online = false;
    }
    await new Promise(resolve => setTimeout(resolve, 500));
  }
}
(async () => {
  containerIds = (await browser.storage.local.get("containerIds")).containerIds || {};
  startupTabs = (await browser.tabs.query({})).filter(t => t.url === KITTY.startupUrl).map(t => t.id);
  await poll();
})();
