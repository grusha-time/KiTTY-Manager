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

// Track the active tab separately for each window.
const knownTabs = new Map();
const activeTabs = new Map();
const previousTabs = new Map();
const activeContainers = new Map();
const pendingContainers = new Map();
const blankTab = url => !url || url === "about:newtab" || url === "about:blank" || url === "about:home";
let inheritanceReady = false;

browser.tabs.onActivated.addListener(async info => {
  const current = activeTabs.get(info.windowId);
  if (current && current !== info.tabId) previousTabs.set(info.tabId, current);
  if (info.previousTabId && info.previousTabId !== info.tabId) previousTabs.set(info.tabId, info.previousTabId);
  activeTabs.set(info.windowId, info.tabId);
  const known = knownTabs.get(info.tabId);
  if (known) {
    if (known.cookieStoreId && routes.has(known.cookieStoreId)) {
      activeContainers.set(info.windowId, known.cookieStoreId);
    } else if (known.cookieStoreId === "firefox-default" && !blankTab(known.url)) {
      activeContainers.delete(info.windowId);
    }
  }
  try {
    const tab = await browser.tabs.get(info.tabId);
    if (tab && activeTabs.get(info.windowId) === info.tabId) {
      knownTabs.set(tab.id, tab);
      if (tab.cookieStoreId && routes.has(tab.cookieStoreId)) {
        activeContainers.set(tab.windowId, tab.cookieStoreId);
      } else if (tab.cookieStoreId === "firefox-default" && !blankTab(tab.url)) {
        activeContainers.delete(tab.windowId);
      }
    }
  } catch (_) { }
});
browser.tabs.onUpdated.addListener((id, changes, tab) => {
  knownTabs.set(id, tab);
  if (tab.active) {
    if (tab.cookieStoreId && routes.has(tab.cookieStoreId)) {
      activeContainers.set(tab.windowId, tab.cookieStoreId);
    } else if (tab.cookieStoreId === "firefox-default" && !blankTab(tab.url)) {
      activeContainers.delete(tab.windowId);
    }
  }
});
browser.tabs.onRemoved.addListener(id => {
  knownTabs.delete(id);
  previousTabs.delete(id);
  pendingContainers.delete(id);
});
browser.windows.onRemoved.addListener(id => {
  activeTabs.delete(id);
  activeContainers.delete(id);
});

browser.tabs.onCreated.addListener(async tab => {
  knownTabs.set(tab.id, tab);
  if (!inheritanceReady || tab.cookieStoreId !== "firefox-default") return;
  const url = tab.pendingUrl || tab.url;
  if (!blankTab(url)) return;

  // Determine parent container from opener, previous active tab, or window active container
  let parentId = tab.openerTabId;
  if (!parentId) {
    const activeId = activeTabs.get(tab.windowId);
    parentId = (activeId === tab.id) ? previousTabs.get(tab.id) : activeId;
  }
  let parent = parentId ? knownTabs.get(parentId) : null;
  if (!parent && parentId) {
    try {
      parent = await browser.tabs.get(parentId);
      if (parent) knownTabs.set(parent.id, parent);
    } catch (_) { }
  }
  const cookieStoreId = pendingContainers.get(parentId) ?? parent?.cookieStoreId ?? activeContainers.get(tab.windowId);
  if (!cookieStoreId || !routes.has(cookieStoreId)) return;

  pendingContainers.set(tab.id, cookieStoreId);
  inheritNewTab(tab, cookieStoreId)
    .catch(e => console.error("KiTTY new tab", e))
    .finally(() => pendingContainers.delete(tab.id));
});

async function inheritNewTab(original, cookieStoreId) {
  let tab;
  try { tab = await browser.tabs.get(original.id); } catch (_) { return; }
  const url = tab.pendingUrl || tab.url;
  if (!blankTab(url) && !/^https?:\/\//i.test(url)) return;
  if (!routes.has(cookieStoreId)) return;
  const replacement = await browser.tabs.create({
    windowId: tab.windowId, index: tab.index, active: false,
    cookieStoreId, ...(blankTab(url) ? {} : {url})
  });
  try {
    knownTabs.set(replacement.id, replacement);
    tab = await browser.tabs.get(original.id);
    const latestUrl = tab.pendingUrl || tab.url;
    if (latestUrl !== url) {
      if (!/^https?:\/\//i.test(latestUrl)) {
        await browser.tabs.remove(replacement.id);
        return;
      }
      await browser.tabs.update(replacement.id, {url: latestUrl});
    }
    if (tab.active && activeTabs.get(tab.windowId) === original.id) {
      await browser.tabs.update(replacement.id, {active: true});
    }
    await browser.tabs.remove(original.id);
  } catch (e) {
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
        .filter(([id]) => tabs.some(t => t.cookieStoreId === id) || openingContainers.has(id))
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
      for (const [windowId, tabId] of activeTabs) {
        const tab = knownTabs.get(tabId);
        if (tab && routes.has(tab.cookieStoreId)) {
          activeContainers.set(windowId, tab.cookieStoreId);
        }
      }
      for (const command of state.commands) {
        if (!completed.has(command.id)) {
          let error = null;
          try {
            const id = containerIds[command.key];
            if (!routes.has(id)) throw new Error("Маршрут контейнера недоступен");
            const tab = await browser.tabs.create({url: command.url, cookieStoreId: id});
            knownTabs.set(tab.id, tab);
          } catch (e) { console.error("KiTTY container open", command.key, e); error = String(e); }
          completed.set(command.id, {id: command.id, error});
        }
        acknowledgements.push(completed.get(command.id));
      }
      if (acknowledgements.some(a => a.error === null)) {
        // Close initial blank/startup tabs once the first container tab exists.
        for (const tabId of startupTabs) {
          try {
            const tab = await browser.tabs.get(tabId);
            if (tab.url === KITTY.startupUrl || (tab.cookieStoreId === "firefox-default" && blankTab(tab.url))) {
              await browser.tabs.remove(tabId);
            }
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
  startupTabs = tabs.filter(t => t.url === KITTY.startupUrl || (t.cookieStoreId === "firefox-default" && blankTab(t.url))).map(t => t.id);
  for (const tab of tabs) {
    knownTabs.set(tab.id, tab);
    if (tab.active && !activeTabs.has(tab.windowId)) activeTabs.set(tab.windowId, tab.id);
  }
  inheritanceReady = true;
  await poll();
})();
