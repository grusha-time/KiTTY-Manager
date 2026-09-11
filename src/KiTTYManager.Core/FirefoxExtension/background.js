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

// Track the active tab separately for each window. onCreated and onActivated
// may arrive in either order; retain the previous tab for the new tab's event.
const knownTabs = new Map();
const activeTabs = new Map();
const previousTabs = new Map();
const pendingContainers = new Map();
const blankTab = url => url === "about:newtab" || url === "about:blank";
let inheritanceReady = false;
browser.tabs.onActivated.addListener(info => {
  previousTabs.set(info.tabId, info.previousTabId ?? activeTabs.get(info.windowId));
  activeTabs.set(info.windowId, info.tabId);
});
browser.tabs.onUpdated.addListener((id, changes, tab) => knownTabs.set(id, tab));
browser.tabs.onRemoved.addListener(id => {
  knownTabs.delete(id);
  previousTabs.delete(id);
  pendingContainers.delete(id);
});
browser.windows.onRemoved.addListener(id => activeTabs.delete(id));
browser.tabs.onCreated.addListener(tab => {
  const activeId = activeTabs.get(tab.windowId);
  const parentId = tab.openerTabId ?? (activeId === tab.id ? previousTabs.get(tab.id) : activeId);
  const parent = knownTabs.get(parentId);
  const cookieStoreId = pendingContainers.get(parentId) ?? parent?.cookieStoreId;
  knownTabs.set(tab.id, tab);
  console.debug("KiTTY tab created", tab.id, tab.cookieStoreId, blankTab(tab.url), parentId, cookieStoreId);
  if (!inheritanceReady || tab.cookieStoreId !== "firefox-default" ||
      (tab.url && !blankTab(tab.url)) || (tab.pendingUrl && !blankTab(tab.pendingUrl)) ||
      !routes.has(cookieStoreId)) return;
  pendingContainers.set(tab.id, cookieStoreId);
  inheritNewTab(tab, cookieStoreId).catch(e => console.error("KiTTY new tab", e))
    .finally(() => pendingContainers.delete(tab.id));
});
async function inheritNewTab(original, cookieStoreId) {
  let tab;
  try { tab = await browser.tabs.get(original.id); } catch (_) { return; }
  const url = tab.pendingUrl || tab.url;
  // A just-created default tab cannot send HTTP requests (blocked below).
  // Preserve a quickly submitted address instead of losing it during replacement.
  if (!blankTab(url) && !/^https?:\/\//i.test(url)) return;
  if (!routes.has(cookieStoreId)) return;
  const replacement = await browser.tabs.create({
    windowId: tab.windowId, index: tab.index + 1, active: false,
    cookieStoreId, ...(blankTab(url) ? {} : {url})
  });
  try {
    tab = await browser.tabs.get(original.id);
    const latestUrl = tab.pendingUrl || tab.url;
    if (latestUrl !== url) {
      if (!/^https?:\/\//i.test(latestUrl)) {
        await browser.tabs.remove(replacement.id);
        return;
      }
      await browser.tabs.update(replacement.id, {url: latestUrl});
    }
    // Do not steal focus if the user switched to another tab while we awaited.
    console.debug("KiTTY tab replacement", original.id, replacement.id, tab.active);
    if (tab.active) await browser.tabs.update(replacement.id, {active: true});
    await browser.tabs.remove(original.id);
  } catch (e) {
    // The user may have closed the original while the replacement was created.
    try { await browser.tabs.remove(replacement.id); } catch (_) { }
    throw e;
  }
}
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
      const openingContainers = new Set(pendingContainers.values());
      const tabs = await browser.tabs.query({});
      const activeKeys = [...routes.entries()]
        .filter(([id]) => tabs.some(t => t.cookieStoreId === id) || openingContainers.has(id) ||
          [...pendingContainers.values()].includes(id))
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
  const tabs = await browser.tabs.query({});
  startupTabs = tabs.filter(t => t.url === KITTY.startupUrl).map(t => t.id);
  for (const tab of tabs) {
    knownTabs.set(tab.id, tab);
    if (tab.active && !activeTabs.has(tab.windowId)) activeTabs.set(tab.windowId, tab.id);
  }
  inheritanceReady = true;
  await poll();
})();
