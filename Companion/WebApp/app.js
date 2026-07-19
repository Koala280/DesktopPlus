// DesktopPlus Companion — PWA file browser.
// Token comes from the pairing QR (#t=...), manual entry, or the in-app scanner, and is sent
// as a bearer header on API calls. All user-controlled strings (file names, paths) are inserted
// via textContent — never innerHTML — so a crafted filename can't inject markup/script.

(function () {
    "use strict";

    const STORAGE_KEY = "desktopplus.companion.token";
    var token = null;
    var ws = null;
    var view = "home";          // "home" | "dir" | "panel" | "pair"
    var homeTab = "panels";     // "panels" | "drives"
    var currentPath = null;
    var currentParent = null;
    var currentPanelId = null;  // when view === "panel"
    var currentPanelName = null;
    var fsReloadTimer = null;

    var net = "init";           // "init" | "online" | "offline"
    var offlineUiTimer = null;  // delays the "reconnecting" UI so brief blips don't flicker
    var reconnectTimer = null;
    var reconnecting = false;
    var homeLoaded = false;     // suppresses the "Connecting…" placeholder after first success

    var scanning = false;
    var scanStream = null;

    // ---- token -------------------------------------------------------------
    function readHashToken() {
        const m = (window.location.hash || "").match(/[#&]t=([^&]+)/);
        if (m) {
            const t = decodeURIComponent(m[1]);
            history.replaceState(null, "", window.location.pathname + window.location.search);
            return t;
        }
        return null;
    }
    function loadToken() {
        const h = readHashToken();
        if (h) { saveToken(h); return h; }
        try { return localStorage.getItem(STORAGE_KEY); } catch (_) { return null; }
    }
    function saveToken(t) {
        token = t;
        try { localStorage.setItem(STORAGE_KEY, t); } catch (_) {}
    }
    function clearToken() {
        token = null;
        try { localStorage.removeItem(STORAGE_KEY); } catch (_) {}
    }

    // ---- small DOM helpers -------------------------------------------------
    function el(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text != null) node.textContent = text;       // XSS-safe
        return node;
    }
    function clear(node) { while (node.firstChild) node.removeChild(node.firstChild); }
    function $(id) { return document.getElementById(id); }

    // ---- icons -------------------------------------------------------------
    // A small, consistent line-icon set (Feather-style). Strings are trusted constants — never
    // user data — so parsing them as markup is safe. Sizing/colour come from CSS (currentColor).
    const ICONS = {
        folder: '<svg viewBox="0 0 24 24"><path d="M3 7.5A1.5 1.5 0 0 1 4.5 6h4.2a1.5 1.5 0 0 1 1.2.6l1 1.4h7.6A1.5 1.5 0 0 1 21 9.5v8A1.5 1.5 0 0 1 19.5 19h-15A1.5 1.5 0 0 1 3 17.5z"/></svg>',
        file: '<svg viewBox="0 0 24 24"><path d="M13 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V9z"/><path d="M13 3v6h6"/></svg>',
        image: '<svg viewBox="0 0 24 24"><rect x="3" y="3" width="18" height="18" rx="2.5"/><circle cx="8.5" cy="8.5" r="1.6"/><path d="M21 15.5 16 10.5 5.5 21"/></svg>',
        drive: '<svg viewBox="0 0 24 24"><rect x="3" y="5.5" width="18" height="13" rx="2.5"/><circle cx="8" cy="12" r="2.4"/><path d="M16.5 12h.01"/></svg>',
        usb: '<svg viewBox="0 0 24 24"><rect x="7" y="8.5" width="10" height="12.5" rx="2"/><path d="M10 8.5V5a2 2 0 0 1 2-2 2 2 0 0 1 2 2v3.5"/><path d="M10.5 13.5h3"/></svg>',
        network: '<svg viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="6.5" rx="1.6"/><rect x="3" y="13.5" width="18" height="6.5" rx="1.6"/><path d="M6.5 7.2h.01M6.5 16.7h.01"/></svg>',
        disc: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="8.5"/><circle cx="12" cy="12" r="2.2"/></svg>',
        monitor: '<svg viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="12.5" rx="2"/><path d="M9 20.5h6M12 16.5v4"/></svg>',
        trash: '<svg viewBox="0 0 24 24"><path d="M4 7h16"/><path d="M9 7V5.5A1.5 1.5 0 0 1 10.5 4h3A1.5 1.5 0 0 1 15 5.5V7"/><path d="M6.5 7l.8 11a2 2 0 0 0 2 1.9h5.4a2 2 0 0 0 2-1.9l.8-11"/></svg>',
        chevron: '<svg viewBox="0 0 24 24"><path d="M9 6l6 6-6 6"/></svg>',
        back: '<svg viewBox="0 0 24 24"><path d="M15 5l-7 7 7 7"/></svg>',
        more: '<svg viewBox="0 0 24 24"><circle cx="12" cy="5.5" r="1.4"/><circle cx="12" cy="12" r="1.4"/><circle cx="12" cy="18.5" r="1.4"/></svg>',
        plus: '<svg viewBox="0 0 24 24"><path d="M12 5v14M5 12h14"/></svg>',
        close: '<svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6 6 18"/></svg>'
    };
    function icon(name) {
        const tpl = document.createElement("template");
        tpl.innerHTML = ICONS[name] || ICONS.file;
        const svg = tpl.content.firstElementChild;
        svg.classList.add("ic");
        return svg;
    }
    function isImage(name) {
        return /\.(png|jpe?g|gif|webp|bmp|svg|heic|heif|avif|ico|tiff?)$/i.test(name || "");
    }

    function setStatus(kind, text) {
        const s = $("status");
        s.className = "status status--" + kind;
        s.textContent = text;
        // When connected, stay quiet — only surface trouble (reconnecting / not paired).
        s.hidden = (kind === "ok");
    }
    function showBanner(text) {
        const b = $("netbanner");
        if (!b) return;
        b.textContent = text;
        b.hidden = false;
    }
    function hideBanner() {
        const b = $("netbanner");
        if (b) b.hidden = true;
    }

    // ---- connection state --------------------------------------------------
    // Dropped connections are non-destructive: the current view stays on screen, a banner
    // shows "reconnecting", and a background ping loop restores the data once the PC answers.
    function goOffline() {
        if (net === "offline") { startReconnect(); return; }
        net = "offline";
        // Grace period: a dropped socket usually recovers within a ping or two. Hold off on the
        // "reconnecting" status/banner so a momentary blip never flashes on screen.
        if (!offlineUiTimer) {
            offlineUiTimer = setTimeout(function () {
                offlineUiTimer = null;
                if (net === "offline") {
                    setStatus("reconnecting", "reconnecting…");
                }
            }, 1500);
        }
        startReconnect();
    }
    function goOnline() {
        const was = net;
        net = "online";
        if (offlineUiTimer) { clearTimeout(offlineUiTimer); offlineUiTimer = null; }
        stopReconnect();
        setStatus("ok", "connected");
        hideBanner();
        if (was !== "online") { reloadCurrentView(); }
    }
    function startReconnect() {
        if (reconnecting) { return; }
        reconnecting = true;
        const tick = function () {
            reconnectTimer = null;
            if (net === "online") { reconnecting = false; return; }
            api("/api/ping").then(function () {
                reconnecting = false;
                goOnline();
            }).catch(function (err) {
                if (err && err.unauthorized) {
                    reconnecting = false;
                    renderPairing("expired");
                    return;
                }
                reconnectTimer = setTimeout(tick, 2500);
            });
        };
        tick();
    }
    function stopReconnect() {
        reconnecting = false;
        if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
    }
    function reloadCurrentView() {
        if (view === "dir" && currentPath) { openDir(currentPath); }
        else if (view === "panel" && currentPanelId) { openPanel(currentPanelId, currentPanelName); }
        else if (view === "home") { renderHome(); }
    }

    // ---- formatting --------------------------------------------------------
    function formatSize(bytes) {
        if (bytes == null) return "";
        const units = ["B", "KB", "MB", "GB", "TB"];
        let n = bytes, i = 0;
        while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
        return (i === 0 ? n : n.toFixed(1)) + " " + units[i];
    }
    function formatDate(iso) {
        if (!iso) return "";
        const d = new Date(iso);
        return isNaN(d) ? "" : d.toLocaleString();
    }

    // ---- API ---------------------------------------------------------------
    async function api(path) {
        let res;
        try {
            res = await fetch(path, {
                headers: { "Authorization": "Bearer " + token },
                cache: "no-store"
            });
        } catch (_) {
            throw { network: true };
        }
        if (res.status === 401) { throw { unauthorized: true }; }
        if (!res.ok) { throw new Error("HTTP " + res.status); }
        return res.json();
    }
    function thumbUrl(path, size) {
        return "/api/fs/thumb?path=" + encodeURIComponent(path) +
               "&size=" + (size || 96) + "&t=" + encodeURIComponent(token);
    }

    // Routes a failed view load: pairing problems → pairing screen; network blips → offline
    // (keep the current content); anything else → an inline notice only if nothing is shown yet.
    function routeError(err, main, placeholderHadContent) {
        if (err && err.unauthorized) { renderPairing("expired"); return; }
        if (err && err.network) { goOffline(); return; }
        if (!placeholderHadContent) {
            clear(main);
            main.appendChild(el("div", "placeholder error", "Couldn't load. Pull to refresh."));
        }
    }

    // ---- rows --------------------------------------------------------------
    function makeRow(opts) {
        const row = el("div", "row" + (opts.onTap ? " tappable" : ""));

        const thumb = el("div", "thumb" + (opts.thumbKind ? " thumb--" + opts.thumbKind : ""));
        const fallback = icon(opts.icon || "file");
        thumb.appendChild(fallback);
        if (opts.thumbPath) {
            const img = el("img");
            // Real image files fill the tile (cover); shell icons for other file types and folders
            // are small transparent glyphs that must be contained with a little breathing room.
            if (opts.thumbContain) { img.className = "as-icon"; }
            img.loading = "lazy";
            img.alt = "";
            img.onload = function () { if (fallback.parentNode) fallback.parentNode.removeChild(fallback); };
            img.onerror = function () { if (img.parentNode) img.parentNode.removeChild(img); };
            img.src = thumbUrl(opts.thumbPath, 96);
            thumb.appendChild(img);
        }
        row.appendChild(thumb);

        const body = el("div", "row-body");
        body.appendChild(el("div", "row-name", opts.name));
        if (opts.meta) body.appendChild(el("div", "row-meta", opts.meta));
        row.appendChild(body);

        if (opts.onActions) {
            const act = el("button", "row-act");
            act.setAttribute("aria-label", "Actions");
            act.appendChild(icon("more"));
            act.addEventListener("click", function (ev) { ev.stopPropagation(); opts.onActions(); });
            row.appendChild(act);
        } else if (opts.onTap) {
            const chev = el("div", "chevron");
            chev.appendChild(icon("chevron"));
            row.appendChild(chev);
        }
        if (opts.onTap) { row.addEventListener("click", opts.onTap); }
        return row;
    }

    // ---- home --------------------------------------------------------------
    // Home is a two-page swipe pager (Panels / Drives). Both pages are rendered side by side in a
    // flex track; switching tab — by tap or horizontal swipe — slides the track. The tab bar and
    // the track are kept in sync through homeTab.
    var pagerEl = null;

    function tabIndex(tab) { return tab === "drives" ? 1 : 0; }

    function renderHomeTabs() {
        const tabs = $("tabs");
        if (!tabs) { return; }
        const show = (view === "home");
        tabs.hidden = !show;
        document.body.classList.toggle("has-tabbar", show);
        const tp = $("tabPanels"), td = $("tabDrives");
        if (tp) tp.className = "tabbar-item" + (homeTab === "panels" ? " active" : "");
        if (td) td.className = "tabbar-item" + (homeTab === "drives" ? " active" : "");
    }

    function setPagerPosition(animate) {
        if (!pagerEl) { return; }
        if (!animate) { pagerEl.classList.add("no-anim"); }
        pagerEl.style.transform = "translateX(" + (-tabIndex(homeTab) * 100) + "%)";
        if (!animate) { void pagerEl.offsetWidth; pagerEl.classList.remove("no-anim"); }
    }

    function setHomeTab(tab, animate) {
        if (tab !== "panels" && tab !== "drives") { return; }
        if (tab === homeTab && pagerEl) { setPagerPosition(animate); return; }
        homeTab = tab;
        renderHomeTabs();
        if (view !== "home") { return; }
        replaceScreen({ screen: "home", tab: homeTab });
        if (pagerEl) { setPagerPosition(animate); }
        else { renderHome(); }
    }

    function fillPanels(page, panels) {
        if (!panels.length) { page.appendChild(el("div", "empty", "No panels.")); return; }
        panels.forEach(function (p) {
            var hasFolder = !!p.folderPath;
            var isList = !hasFolder && p.type === "List";
            var pIcon = hasFolder ? "folder" : (p.type === "RecycleBin" ? "trash" : "monitor");
            var pName = p.title || p.type || "Panel";
            page.appendChild(makeRow({
                icon: pIcon,
                thumbKind: hasFolder ? null : "panel",
                thumbPath: hasFolder ? p.folderPath : null,
                thumbContain: hasFolder,
                name: pName,
                meta: hasFolder ? p.folderPath : (p.type || ""),
                onTap: hasFolder ? function () { navDir(p.folderPath); }
                     : (isList ? function () { navPanel(p.id, pName); } : null)
            }));
        });
    }
    function driveIcon(type) {
        switch ((type || "").toLowerCase()) {
            case "removable": return "usb";
            case "network": return "network";
            case "cdrom": return "disc";
            default: return "drive";   // Fixed / Ram / Unknown
        }
    }
    function fillDrives(page, drives) {
        var ready = drives.filter(function (d) { return d.ready; });
        if (!ready.length) { page.appendChild(el("div", "empty", "No drives.")); return; }
        ready.forEach(function (d) {
            page.appendChild(makeRow({
                icon: driveIcon(d.type),
                thumbKind: "drive",
                name: d.label ? (d.label + " (" + d.name + ")") : d.name,
                meta: d.type,
                onTap: function () { navDir(d.path); }
            }));
        });
    }

    function buildPager(panels, drives) {
        const main = $("main");
        clear(main);
        const vp = el("div", "swipe-vp");
        const track = el("div", "pager");
        track.id = "pager";
        const pPanels = el("section", "page");
        const pDrives = el("section", "page");
        fillPanels(pPanels, panels);
        fillDrives(pDrives, drives);
        track.appendChild(pPanels);
        track.appendChild(pDrives);
        vp.appendChild(track);
        main.appendChild(vp);
        pagerEl = track;
        attachSwipe(track);
        setPagerPosition(false);
    }

    // Horizontal drag with a direction lock: the first few pixels decide swipe-vs-scroll, so a
    // vertical scroll is never hijacked. Edge pulls get resistance; a past-threshold release
    // commits to the neighbouring tab, otherwise it snaps back.
    function attachSwipe(track) {
        var startX = 0, startY = 0, dx = 0, width = 0;
        var dragging = false, decided = false, horizontal = false;

        track.addEventListener("touchstart", function (e) {
            if (e.touches.length !== 1) { return; }
            startX = e.touches[0].clientX;
            startY = e.touches[0].clientY;
            dx = 0; dragging = true; decided = false; horizontal = false;
            width = track.getBoundingClientRect().width || 1;
            track.classList.add("no-anim");
        }, { passive: true });

        track.addEventListener("touchmove", function (e) {
            if (!dragging) { return; }
            var mvx = e.touches[0].clientX - startX;
            var mvy = e.touches[0].clientY - startY;
            if (!decided) {
                if (Math.abs(mvx) < 8 && Math.abs(mvy) < 8) { return; }
                decided = true;
                horizontal = Math.abs(mvx) > Math.abs(mvy);
            }
            if (!horizontal) { return; }     // vertical gesture → leave it to the scroller
            e.preventDefault();
            var idx = tabIndex(homeTab);
            if ((idx === 0 && mvx > 0) || (idx === 1 && mvx < 0)) { mvx *= 0.35; }  // edge resistance
            dx = mvx;
            track.style.transform = "translateX(calc(" + (-idx * 100) + "% + " + dx + "px))";
        }, { passive: false });

        var end = function () {
            if (!dragging) { return; }
            dragging = false;
            track.classList.remove("no-anim");
            if (horizontal && Math.abs(dx) > width * 0.18) {
                if (dx < 0 && homeTab === "panels") { setHomeTab("drives", true); return; }
                if (dx > 0 && homeTab === "drives") { setHomeTab("panels", true); return; }
            }
            setPagerPosition(true);          // snap back to the current tab
        };
        track.addEventListener("touchend", end);
        track.addEventListener("touchcancel", end);
    }

    async function renderHome() {
        view = "home";
        currentPath = null;
        pagerEl = null;
        $("back").hidden = true;
        $("crumbs").hidden = true;
        renderHomeTabs();
        renderToolbar();
        $("title").textContent = "DesktopPlus";
        const main = $("main");
        if (!homeLoaded) {
            clear(main);
            main.appendChild(el("div", "placeholder", net === "offline" ? "Waiting for DesktopPlus…" : "Loading…"));
        }

        try {
            const [panels, drives] = await Promise.all([api("/api/panels"), api("/api/fs/drives")]);
            if (net !== "online") { goOnline(); }
            homeLoaded = true;
            buildPager(panels || [], drives || []);
        } catch (err) {
            routeError(err, main, homeLoaded);
        }
    }

    // ---- directory ---------------------------------------------------------
    async function openDir(path) {
        const main = $("main");
        const hadContent = (view === "dir");
        if (!hadContent) {
            clear(main);
            main.appendChild(el("div", "placeholder", "Loading…"));
        }
        try {
            const listing = await api("/api/fs/list?path=" + encodeURIComponent(path));
            if (net !== "online") { goOnline(); }
            view = "dir";
            currentPath = listing.path;
            currentParent = listing.parent || null;
            $("tabs").hidden = true;
            document.body.classList.remove("has-tabbar");
            pagerEl = null;
            $("back").hidden = false;
            $("title").textContent = listing.name || listing.path;
            renderCrumbs(listing.path);
            renderListing(listing);
            renderToolbar();
            watch(listing.path);
        } catch (err) {
            routeError(err, main, hadContent);
        }
    }

    // ---- list panel --------------------------------------------------------
    // A List-type panel has no backing folder — just pinned items. We show them in a flat list;
    // folders inside open the real directory (navDir), files get the usual action sheet.
    async function openPanel(id, name) {
        const main = $("main");
        const hadContent = (view === "panel" && currentPanelId === id);
        if (!hadContent) {
            clear(main);
            main.appendChild(el("div", "placeholder", "Loading…"));
        }
        try {
            const data = await api("/api/panel/items?id=" + encodeURIComponent(id));
            if (net !== "online") { goOnline(); }
            view = "panel";
            currentPath = null;
            currentPanelId = id;
            currentPanelName = name;
            $("tabs").hidden = true;
            document.body.classList.remove("has-tabbar");
            $("crumbs").hidden = true;
            pagerEl = null;
            $("back").hidden = false;
            $("title").textContent = name || "List";
            renderListing({ entries: data.entries }, "This list is empty.");
            renderToolbar();
        } catch (err) {
            routeError(err, main, hadContent);
        }
    }

    function renderListing(listing, emptyText) {
        const main = $("main");
        clear(main);
        if (!listing.entries || !listing.entries.length) {
            main.appendChild(el("div", "empty", emptyText || "This folder is empty."));
            return;
        }
        listing.entries.forEach(function (e) {
            var img = !e.isDir && isImage(e.name);
            main.appendChild(makeRow({
                icon: e.isDir ? "folder" : (img ? "image" : "file"),
                // Every entry loads its real Explorer icon/thumbnail (same as on the desktop); the
                // line-icon above is only a fallback while it loads or if it can't be produced.
                thumbPath: e.path,
                thumbContain: !img,
                name: e.name,
                meta: e.isDir ? "" : (formatSize(e.size) + (e.modified ? "  ·  " + formatDate(e.modified) : "")),
                onTap: e.isDir ? function () { navDir(e.path); } : null,
                onActions: function () { entryActions(e); }
            }));
        });
    }

    function renderCrumbs(path) {
        const crumbs = $("crumbs");
        clear(crumbs);
        crumbs.hidden = false;

        const home = el("button", "crumb", "Home");
        home.addEventListener("click", navHome);
        crumbs.appendChild(home);

        const parts = path.split("\\").filter(function (p) { return p.length; });
        let acc = "";
        parts.forEach(function (part, i) {
            acc += (i === 0 ? part + "\\" : part);
            const full = acc;
            if (i > 0) acc += "\\";
            crumbs.appendChild(el("span", "crumb-sep", "›"));
            const b = el("button", "crumb", part);
            b.addEventListener("click", function () { navDir(full); });
            crumbs.appendChild(b);
        });
    }

    // ---- file management (Phase 2) -----------------------------------------
    var clip = null;            // { mode: "move"|"copy", path, name }
    var toastTimer = null;

    async function apiPost(path, body) {
        let res;
        try {
            res = await fetch(path, {
                method: "POST",
                headers: { "Authorization": "Bearer " + token, "Content-Type": "application/json" },
                cache: "no-store",
                body: JSON.stringify(body || {})
            });
        } catch (_) {
            throw { network: true };
        }
        if (res.status === 401) { throw { unauthorized: true }; }
        if (!res.ok) { throw new Error("HTTP " + res.status); }
        return res.json();
    }

    function opErrorText(err) {
        if (err && err.unauthorized) { renderPairing("expired"); return "Pairing expired."; }
        if (err && err.network) { return "Connection lost — try again once reconnected."; }
        return "Something went wrong.";
    }

    function afterMutation() {
        if (view === "dir" && currentPath) { openDir(currentPath); }
        else if (view === "panel" && currentPanelId) { openPanel(currentPanelId, currentPanelName); }
    }

    function toast(msg) {
        let t = $("toast");
        if (!t) { t = el("div", "toast"); t.id = "toast"; document.body.appendChild(t); }
        t.textContent = msg;
        t.classList.add("show");
        clearTimeout(toastTimer);
        toastTimer = setTimeout(function () { t.classList.remove("show"); }, 2600);
    }

    // ---- bottom sheets ----
    function closeSheet() {
        const ov = $("sheetOv");
        if (ov && ov.parentNode) { ov.parentNode.removeChild(ov); }
    }
    function openSheet() {
        closeSheet();
        const ov = el("div", "sheet-ov");
        ov.id = "sheetOv";
        ov.addEventListener("click", function (e) { if (e.target === ov) { closeSheet(); } });
        const card = el("div", "sheet");
        ov.appendChild(card);
        document.body.appendChild(ov);
        return card;
    }
    function sheetButton(card, label, cls, fn) {
        const b = el("button", "sheet-btn" + (cls ? " " + cls : ""), label);
        b.addEventListener("click", fn);
        card.appendChild(b);
        return b;
    }

    function entryActions(e) {
        const card = openSheet();
        card.appendChild(el("div", "sheet-title", e.name));
        sheetButton(card, "Open on PC", "", function () { confirmOpenOnPc(e); });
        sheetButton(card, "Rename", "", function () { promptRename(e); });
        sheetButton(card, "Copy", "", function () { closeSheet(); setClip("copy", e); });
        sheetButton(card, "Move", "", function () { closeSheet(); setClip("move", e); });
        sheetButton(card, "Delete", "danger", function () { confirmDelete(e); });
        sheetButton(card, "Cancel", "muted", closeSheet);
    }

    function promptRename(e) {
        inputSheet("Rename", e.name, "Rename", function (val, s) {
            apiPost("/api/fs/rename", { path: e.path, newName: val })
                .then(function (r) { if (r.ok) { closeSheet(); afterMutation(); } else { s.error(r.error || "Couldn't rename."); } })
                .catch(function (err) { s.error(opErrorText(err)); });
        });
    }

    function promptNewFolder() {
        if (!currentPath) { return; }
        inputSheet("New folder", "", "Create", function (val, s) {
            apiPost("/api/fs/mkdir", { dir: currentPath, name: val })
                .then(function (r) { if (r.ok) { closeSheet(); afterMutation(); } else { s.error(r.error || "Couldn't create folder."); } })
                .catch(function (err) { s.error(opErrorText(err)); });
        });
    }

    function inputSheet(title, initial, okLabel, onOk) {
        const card = openSheet();
        card.appendChild(el("div", "sheet-title", title));
        const input = el("input", "field");
        input.type = "text";
        input.value = initial || "";
        input.autocomplete = "off";
        input.autocapitalize = "off";
        input.spellcheck = false;
        card.appendChild(input);
        const err = el("div", "sheet-err");
        card.appendChild(err);
        const s = { error: function (m) { err.textContent = m || ""; } };
        const row = el("div", "sheet-row");
        const cancel = el("button", "btn", "Cancel");
        cancel.addEventListener("click", closeSheet);
        const ok = el("button", "btn btn-primary", okLabel);
        const submit = function () {
            const v = (input.value || "").trim();
            if (!v) { s.error("Enter a name."); return; }
            onOk(v, s);
        };
        ok.addEventListener("click", submit);
        input.addEventListener("keydown", function (ev) { if (ev.key === "Enter") { submit(); } });
        row.appendChild(cancel);
        row.appendChild(ok);
        card.appendChild(row);
        setTimeout(function () { input.focus(); }, 60);
    }

    function confirmDelete(e) {
        const card = openSheet();
        card.appendChild(el("div", "sheet-title", "Delete " + e.name + "?"));
        card.appendChild(el("div", "sheet-sub", "Goes to the Windows recycle bin — you can restore it later."));
        const err = el("div", "sheet-err");
        const run = function (permanent) {
            apiPost("/api/fs/delete", { paths: [e.path], permanent: permanent })
                .then(function (r) { if (r.ok) { closeSheet(); afterMutation(); } else { err.textContent = r.error || "Couldn't delete."; } })
                .catch(function (x) { err.textContent = opErrorText(x); });
        };
        sheetButton(card, "Move to recycle bin", "", function () { run(false); });
        sheetButton(card, "Delete permanently", "danger", function () { run(true); });
        card.appendChild(err);
        sheetButton(card, "Cancel", "muted", closeSheet);
    }

    function confirmOpenOnPc(e) {
        const card = openSheet();
        card.appendChild(el("div", "sheet-title", "Open on PC"));
        card.appendChild(el("div", "sheet-sub", "Opens “" + e.name + "” on the computer. If a game is running fullscreen, this can pull it out of focus."));
        const err = el("div", "sheet-err");
        sheetButton(card, "Open on PC", "", function () {
            apiPost("/api/open", { path: e.path })
                .then(function (r) { if (r.ok) { closeSheet(); } else { err.textContent = r.error || "Couldn't open."; } })
                .catch(function (x) { err.textContent = opErrorText(x); });
        });
        card.appendChild(err);
        sheetButton(card, "Cancel", "muted", closeSheet);
    }

    // ---- move/copy clipboard + toolbar ----
    function setClip(mode, e) {
        clip = { mode: mode, path: e.path, name: e.name };
        renderToolbar();
        toast((mode === "move" ? "Ready to move: " : "Ready to copy: ") + e.name);
    }
    function doPaste() {
        if (!clip || !currentPath) { return; }
        const url = clip.mode === "move" ? "/api/fs/move" : "/api/fs/copy";
        const mode = clip.mode;
        apiPost(url, { paths: [clip.path], destDir: currentPath })
            .then(function (r) {
                if (r.ok) { clip = null; renderToolbar(); afterMutation(); toast(mode === "move" ? "Moved." : "Copied."); }
                else { toast(r.error || "Couldn't complete."); }
            })
            .catch(function (x) { toast(opErrorText(x)); });
    }
    function renderToolbar() {
        const tb = $("toolbar");
        if (!tb) { return; }
        clear(tb);
        if (view !== "dir") { tb.hidden = true; return; }
        tb.hidden = false;

        const newBtn = el("button", "tb-btn");
        newBtn.appendChild(icon("plus"));
        newBtn.appendChild(el("span", null, "New folder"));
        newBtn.addEventListener("click", promptNewFolder);
        tb.appendChild(newBtn);

        if (clip) {
            const pasteBtn = el("button", "tb-btn tb-primary", (clip.mode === "move" ? "Move here" : "Paste here"));
            pasteBtn.addEventListener("click", doPaste);
            tb.appendChild(pasteBtn);
            const cancelBtn = el("button", "tb-btn tb-x");
            cancelBtn.setAttribute("aria-label", "Cancel");
            cancelBtn.appendChild(icon("close"));
            cancelBtn.addEventListener("click", function () { clip = null; renderToolbar(); });
            tb.appendChild(cancelBtn);
        }
    }

    // ---- pairing -----------------------------------------------------------
    function renderPairing(reason) {
        view = "pair";
        stopReconnect();
        if (ws) { try { ws.close(); } catch (_) {} ws = null; }
        if (reason === "expired") { clearToken(); }
        homeLoaded = false;

        $("back").hidden = true;
        $("crumbs").hidden = true;
        $("tabs").hidden = true;
        document.body.classList.remove("has-tabbar");
        pagerEl = null;
        hideBanner();
        $("title").textContent = "DesktopPlus";
        setStatus(reason === "expired" ? "error" : "pending", "not paired");

        const main = $("main");
        clear(main);

        const wrap = el("div", "pair");
        const card = el("div", "pair-card");

        card.appendChild(el("div", "pair-h", "Connect to DesktopPlus"));
        card.appendChild(el("div", "pair-sub", reason === "expired"
            ? "The pairing expired or the token changed. Re-pair to continue."
            : "Get the QR code from DesktopPlus → Settings → Companion, then scan it or paste the link below."));

        const canScan = ("BarcodeDetector" in window) &&
                        navigator.mediaDevices && navigator.mediaDevices.getUserMedia;
        if (canScan) {
            const scanBtn = el("button", "btn btn-primary", "📷  Scan QR code");
            scanBtn.addEventListener("click", startScan);
            card.appendChild(scanBtn);
            card.appendChild(el("div", "pair-or", "or enter it manually"));
        }

        const field = el("input", "field");
        field.id = "pairInput";
        field.type = "text";
        field.autocomplete = "off";
        field.autocapitalize = "off";
        field.spellcheck = false;
        field.placeholder = "Paste token or pairing link";
        field.addEventListener("keydown", function (e) { if (e.key === "Enter") applyPairingInput(field.value); });
        card.appendChild(field);

        const row = el("div", "pair-row");
        if (navigator.clipboard && navigator.clipboard.readText) {
            const pasteBtn = el("button", "btn", "Paste");
            pasteBtn.addEventListener("click", function () {
                navigator.clipboard.readText()
                    .then(function (t) { field.value = (t || "").trim(); })
                    .catch(function () {});
            });
            row.appendChild(pasteBtn);
        }
        const connectBtn = el("button", "btn btn-primary", "Connect");
        connectBtn.addEventListener("click", function () { applyPairingInput(field.value); });
        row.appendChild(connectBtn);
        card.appendChild(row);

        const err = el("div", "pair-err");
        err.id = "pairErr";
        card.appendChild(err);

        wrap.appendChild(card);
        main.appendChild(wrap);
    }

    function pairError(msg) {
        const e = $("pairErr");
        if (e) e.textContent = msg || "";
    }

    // Accepts a raw token or a full pairing link. A link to a different host triggers a redirect
    // (so we land on the right origin with the token); same-host links and raw tokens are verified
    // against /api/ping before they're stored — invalid tokens are never persisted.
    function applyPairingInput(raw) {
        pairError("");
        const text = (raw || "").trim();
        if (!text) { pairError("Enter a token or pairing link."); return; }

        let candidate = text;
        if (/^https?:\/\//i.test(text)) {
            let u;
            try { u = new URL(text); } catch (_) { pairError("That doesn't look like a valid link."); return; }
            const m = (u.hash.match(/[#&]t=([^&]+)/) || u.search.match(/[?&]t=([^&]+)/));
            if (!m) { pairError("That link has no token."); return; }
            candidate = decodeURIComponent(m[1]);
            if (u.origin !== location.origin) {
                window.location.href = u.origin + "/#t=" + encodeURIComponent(candidate);
                return;
            }
        }
        verifyAndConnect(candidate);
    }

    function verifyAndConnect(candidate) {
        setStatus("pending", "checking…");
        const prev = token;
        token = candidate;
        api("/api/ping").then(function () {
            saveToken(candidate);
            net = "online";
            setStatus("ok", "connected");
            hideBanner();
            connectWs();
            renderHome();
        }).catch(function (err) {
            token = prev;
            setStatus("error", "not paired");
            if (err && err.network) {
                pairError("Can't reach DesktopPlus. Same Wi-Fi, and the app running with Companion enabled?");
            } else if (err && err.unauthorized) {
                pairError("That token was rejected. Check it or regenerate it in DesktopPlus. (Several wrong tries pause checks for a minute.)");
            } else {
                pairError("Couldn't verify. Try again.");
            }
        });
    }

    // ---- QR scanner --------------------------------------------------------
    // Uses the platform BarcodeDetector (Android Chrome/Edge). Where it's unavailable (e.g. iOS
    // Safari) the scan button isn't shown and manual entry is the path. Camera frames never leave
    // the device; only the decoded text is used.
    function startScan() {
        pairError("");
        if (!("BarcodeDetector" in window) || !navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
            pairError("QR scanning isn't supported here — paste the token or link instead.");
            return;
        }
        navigator.mediaDevices.getUserMedia({ video: { facingMode: "environment" }, audio: false })
            .then(function (stream) {
                scanStream = stream;
                scanning = true;
                buildScannerOverlay();
                const video = $("scanVideo");
                video.srcObject = stream;
                const detector = new BarcodeDetector({ formats: ["qr_code"] });
                const loop = function () {
                    if (!scanning) { return; }
                    detector.detect(video).then(function (codes) {
                        if (!scanning) { return; }
                        if (codes && codes.length && codes[0].rawValue) { onScanResult(codes[0].rawValue); }
                        else { setTimeout(loop, 250); }
                    }).catch(function () { setTimeout(loop, 400); });
                };
                const playing = video.play();
                if (playing && playing.then) { playing.then(function () { setTimeout(loop, 300); }).catch(function () { setTimeout(loop, 300); }); }
                else { setTimeout(loop, 300); }
            })
            .catch(function () {
                pairError("Camera permission denied or unavailable — paste the token or link instead.");
            });
    }
    function stopScan() {
        scanning = false;
        if (scanStream) { scanStream.getTracks().forEach(function (t) { t.stop(); }); scanStream = null; }
        const ov = $("scanner");
        if (ov && ov.parentNode) { ov.parentNode.removeChild(ov); }
    }
    function onScanResult(text) {
        stopScan();
        applyPairingInput(text);
    }
    function buildScannerOverlay() {
        const ov = el("div", "scanner");
        ov.id = "scanner";
        const video = document.createElement("video");
        video.id = "scanVideo";
        video.setAttribute("playsinline", "");
        video.setAttribute("muted", "");
        video.muted = true;
        ov.appendChild(video);
        ov.appendChild(el("div", "scan-frame"));
        ov.appendChild(el("div", "scan-hint", "Point the camera at the QR code in DesktopPlus → Companion"));
        const cancel = el("button", "scan-cancel", "Cancel");
        cancel.addEventListener("click", stopScan);
        ov.appendChild(cancel);
        document.body.appendChild(ov);
    }

    // ---- live updates ------------------------------------------------------
    function connectWs() {
        if (!token) { return; }
        try {
            const proto = location.protocol === "https:" ? "wss://" : "ws://";
            ws = new WebSocket(proto + location.host + "/api/events?t=" + encodeURIComponent(token));
            ws.onopen = function () {
                if (net === "offline") { goOnline(); }
                if (view === "dir" && currentPath) { watch(currentPath); }
            };
            ws.onmessage = function (ev) {
                let msg;
                try { msg = JSON.parse(ev.data); } catch (_) { return; }
                if (msg.type === "panelsChanged" && view === "home") {
                    renderHome();
                } else if (msg.type === "panelsChanged" && view === "panel" && currentPanelId) {
                    openPanel(currentPanelId, currentPanelName);
                } else if (msg.type === "fsChanged" && view === "dir" && msg.path === currentPath) {
                    clearTimeout(fsReloadTimer);
                    fsReloadTimer = setTimeout(function () { if (currentPath) openDir(currentPath); }, 400);
                }
            };
            ws.onclose = function () {
                ws = null;
                if (view === "pair" || !token) { return; }
                goOffline();                       // surface "reconnecting" + start the ping loop
                setTimeout(connectWs, 3000);       // independent, throttled socket retry
            };
            ws.onerror = function () { try { ws.close(); } catch (_) {} };
        } catch (_) {
            setTimeout(connectWs, 3000);
        }
    }
    function watch(path) {
        if (ws && ws.readyState === WebSocket.OPEN) {
            try { ws.send(JSON.stringify({ watch: path })); } catch (_) {}
        }
    }

    // ---- history / back navigation -----------------------------------------
    // Each screen (home, a directory) is a history entry, so the browser/OS back gesture — the
    // "swipe back" — and the in-app ‹ button pop within the app instead of leaving the page.
    // Live-update and reconnect reloads re-render in place and never push history.
    function currentScreenState() {
        if (view === "dir" && currentPath) { return { screen: "dir", path: currentPath }; }
        if (view === "panel" && currentPanelId) { return { screen: "panel", id: currentPanelId, name: currentPanelName }; }
        return { screen: "home", tab: homeTab };
    }
    function pushScreen(state) { try { history.pushState(state, ""); } catch (_) {} }
    function replaceScreen(state) { try { history.replaceState(state, ""); } catch (_) {} }
    function navDir(path) { pushScreen({ screen: "dir", path: path }); openDir(path); }
    function navPanel(id, name) { pushScreen({ screen: "panel", id: id, name: name }); openPanel(id, name); }
    function navHome() { pushScreen({ screen: "home", tab: homeTab }); renderHome(); }

    function onPopState(e) {
        // An open sheet or the QR scanner swallows back: dismiss it and stay put.
        if ($("sheetOv") || scanning) {
            closeSheet();
            stopScan();
            pushScreen(currentScreenState());
            return;
        }
        const st = e.state;
        if (st && st.screen === "dir") { openDir(st.path); return; }
        if (st && st.screen === "panel") { openPanel(st.id, st.name); return; }
        if (st && st.tab) { homeTab = st.tab; }
        renderHome();
    }

    // ---- boot --------------------------------------------------------------
    function boot() {
        const back = $("back");
        clear(back); back.appendChild(icon("back"));
        back.addEventListener("click", function () { history.back(); });
        window.addEventListener("popstate", onPopState);

        var tpIco = $("tabPanels").querySelector(".tabbar-ico");
        var tdIco = $("tabDrives").querySelector(".tabbar-ico");
        if (tpIco) { clear(tpIco); tpIco.appendChild(icon("monitor")); }
        if (tdIco) { clear(tdIco); tdIco.appendChild(icon("drive")); }
        const bn = $("netbanner");
        if (bn) { bn.addEventListener("click", function () { if (net !== "online") { startReconnect(); } }); }
        $("tabPanels").addEventListener("click", function () { setHomeTab("panels", true); });
        $("tabDrives").addEventListener("click", function () { setHomeTab("drives", true); });

        // Seed the history with a home entry so the first back gesture has somewhere to land.
        replaceScreen({ screen: "home", tab: homeTab });

        token = loadToken();
        if (!token) { renderPairing("unpaired"); return; }

        setStatus("pending", "connecting…");
        api("/api/ping").then(function () {
            net = "online";
            setStatus("ok", "connected");
            connectWs();
            renderHome();
        }).catch(function (err) {
            if (err && err.unauthorized) { renderPairing("expired"); return; }
            // Network blip on startup: show the home shell and keep retrying in the background.
            net = "offline";
            connectWs();
            renderHome();
            goOffline();
        });
    }

    document.addEventListener("DOMContentLoaded", boot);
})();
