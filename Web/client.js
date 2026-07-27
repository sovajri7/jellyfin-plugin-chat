/*
 * Jellyfin Chat plugin - client web.
 * Injecte un widget de chat flottant dans l'interface Jellyfin.
 * Communique avec les endpoints /ChatPlugin/* via l'ApiClient de Jellyfin.
 */
(function () {
    'use strict';

    if (window.__jfChatLoaded) {
        return;
    }
    window.__jfChatLoaded = true;

    var POLL_MS = 3000;
    var state = {
        me: null,
        isAdmin: false,
        users: [],           // annuaire
        usersById: {},
        currentRoom: 'public',
        currentTarget: null, // id de l'autre user en DM
        tabs: [],            // { room, target, name } ouverts
        lastId: {},          // room -> dernier id charge
        seen: {},            // room -> Set d'ids affiches
        polling: null,
        blockedByMe: [],
        gifEnabled: false,
        _observer: null,
        _pending: false,
        notif: { badge: 0, friendRequests: 0, conversations: [] },
        notifPolling: null,
        win: null // etat de fenetre (charge depuis localStorage)
    };

    /* ------------------------------------------------------------------ */
    /* Etat de la fenetre (position/taille/mode), persiste.               */
    /* ------------------------------------------------------------------ */
    var WIN_KEY = 'jfc-win';
    function loadWin() {
        try {
            var w = JSON.parse(localStorage.getItem(WIN_KEY) || '{}');
            return {
                mode: w.mode || 'float',       // float | docked-left | docked-right | minimized
                prevMode: w.prevMode || 'float',
                x: (typeof w.x === 'number') ? w.x : null,
                y: (typeof w.y === 'number') ? w.y : null,
                w: w.w || 375,
                h: w.h || 560,
                dockW: w.dockW || 360,
                dockH: w.dockH || 320
            };
        } catch (e) {
            return { mode: 'float', prevMode: 'float', x: null, y: null, w: 375, h: 560, dockW: 360, dockH: 320 };
        }
    }
    function saveWin() {
        try { localStorage.setItem(WIN_KEY, JSON.stringify(state.win)); } catch (e) {}
    }

    /* ------------------------------------------------------------------ */
    /* Amorcage : attendre l'ApiClient et un utilisateur connecte.        */
    /* ------------------------------------------------------------------ */
    function ready() {
        return window.ApiClient
            && typeof window.ApiClient.accessToken === 'function'
            && window.ApiClient.accessToken()
            && window.ApiClient.getCurrentUserId
            && window.ApiClient.getCurrentUserId();
    }

    function boot() {
        if (!ready()) {
            setTimeout(boot, 800);
            return;
        }
        state.me = window.ApiClient.getCurrentUserId();
        state.win = loadWin();
        injectStyle();
        buildLauncher();
        startNotifications();
    }

    function scriptVersion() {
        var s = document.querySelector('script[src*="ChatPlugin/client.js"]');
        if (s) { var m = s.src.match(/[?&]v=([^&]+)/); if (m) { return m[1]; } }
        return '';
    }

    function injectStyle() {
        if (document.getElementById('jf-chat-style')) {
            return;
        }
        var v = scriptVersion();
        var link = document.createElement('link');
        link.id = 'jf-chat-style';
        link.rel = 'stylesheet';
        link.href = apiUrl('ChatPlugin/client.css') + (v ? ('?v=' + v) : '');
        document.head.appendChild(link);
    }

    /* ------------------------------------------------------------------ */
    /* Helpers API                                                        */
    /* ------------------------------------------------------------------ */
    function apiUrl(path) {
        var base = window.ApiClient.serverAddress();
        return base.replace(/\/$/, '') + '/' + path;
    }

    function api(path, opts) {
        opts = opts || {};
        opts.headers = opts.headers || {};
        opts.headers['X-Emby-Token'] = window.ApiClient.accessToken();
        if (opts.body && typeof opts.body !== 'string') {
            opts.body = JSON.stringify(opts.body);
            opts.headers['Content-Type'] = 'application/json';
        }
        return fetch(apiUrl(path), opts).then(function (r) {
            if (!r.ok) {
                return r.json().catch(function () { return {}; }).then(function (e) {
                    throw new Error(e.error || ('HTTP ' + r.status));
                });
            }
            if (r.status === 204) {
                return null;
            }
            return r.json().catch(function () { return null; });
        });
    }

    function avatar(url, name) {
        var el = document.createElement('div');
        el.className = 'jfc-avatar';
        if (url) {
            var img = document.createElement('img');
            img.src = apiUrl(url);
            img.alt = name || '';
            img.onerror = function () { el.textContent = initials(name); img.remove(); };
            el.appendChild(img);
        } else {
            el.textContent = initials(name);
        }
        return el;
    }

    function initials(name) {
        return (name || '?').trim().slice(0, 2).toUpperCase();
    }

    function esc(s) {
        var d = document.createElement('div');
        d.textContent = s == null ? '' : s;
        return d.innerHTML;
    }

    function timeAgo(ts) {
        var diff = Math.max(0, Date.now() - ts);
        var m = Math.floor(diff / 60000);
        if (m < 1) { return "a l'instant"; }
        if (m < 60) { return m + 'min'; }
        var h = Math.floor(m / 60);
        if (h < 24) { return h + 'h'; }
        return Math.floor(h / 24) + 'j';
    }

    /* ------------------------------------------------------------------ */
    /* UI : lanceur + panneau                                             */
    /* ------------------------------------------------------------------ */
    function buildLauncher() {
        ensureHeaderButton();
        // Le client Jellyfin est une SPA : il re-rend le header a chaque navigation.
        // On observe le DOM pour re-injecter le bouton s'il disparait.
        if (!state._observer) {
            state._observer = new MutationObserver(scheduleEnsure);
            state._observer.observe(document.body, { childList: true, subtree: true });
        }
    }

    function scheduleEnsure() {
        if (state._pending) { return; }
        state._pending = true;
        setTimeout(function () { state._pending = false; ensureHeaderButton(); }, 300);
    }

    function findHeaderRight() {
        return document.querySelector('.headerRight')
            || document.querySelector('.skinHeader .headerRight')
            || document.querySelector('[class*="headerRight"]')
            || null;
    }

    function ensureHeaderButton() {
        if (document.getElementById('jfc-launcher')) { return; }
        var host = findHeaderRight();
        var btn = document.createElement('button');
        btn.id = 'jfc-launcher';
        btn.type = 'button';
        btn.title = 'Chat en Direct';
        if (host) {
            // Bouton natif Jellyfin : herite du theme (et de KefinTweaks).
            btn.className = 'headerButton headerButtonRight paper-icon-button-light jfc-headerbtn';
            btn.innerHTML = '<span class="material-icons" aria-hidden="true" style="font-size:1.6em">forum</span>'
                + '<span class="jfc-unread" id="jfc-unread"></span>';
            host.insertBefore(btn, host.firstChild);
        } else {
            // Repli : bouton flottant en bas a droite.
            btn.className = 'jfc-float';
            btn.innerHTML = '💬<span class="jfc-unread" id="jfc-unread"></span>';
            document.body.appendChild(btn);
        }
        btn.addEventListener('click', togglePanel);
        refreshBadge();
    }

    /* ------------------------------------------------------------------ */
    /* Notifications (DM non lus + demandes d'ami) + presence.            */
    /* ------------------------------------------------------------------ */
    function refreshBadge() {
        var el = document.getElementById('jfc-unread');
        if (!el) { return; }
        var n = state.notif.badge || 0;
        if (n > 0) { el.textContent = n > 99 ? '99+' : n; el.classList.add('show'); }
        else { el.textContent = ''; el.classList.remove('show'); }
    }

    function startNotifications() {
        fetchNotifications();
        if (!state.notifPolling) {
            state.notifPolling = setInterval(fetchNotifications, 5000);
        }
    }

    function fetchNotifications() {
        api('ChatPlugin/notifications').then(function (n) {
            if (!n) { return; }
            state.notif = {
                badge: n.badge || 0,
                friendRequests: n.friendRequests || 0,
                conversations: n.conversations || [],
                online: n.online || 0,
                members: n.members || 0
            };
            refreshBadge();
            updateOnline();
            renderConversationsBadges();
        }).catch(function () {});
    }

    function renderConversationsBadges() {
        var people = document.getElementById('jfc-people');
        if (!people) { return; }
        var dm = (state.notif.conversations || []).reduce(function (s, c) { return s + (c.unread || 0); }, 0);
        var total = dm + (state.notif.friendRequests || 0);
        var b = people.querySelector('.jfc-btnbadge');
        if (total > 0) {
            if (!b) { b = document.createElement('span'); b.className = 'jfc-btnbadge'; people.appendChild(b); }
            b.textContent = total > 99 ? '99+' : total;
        } else if (b) { b.remove(); }
    }

    function panel() { return document.getElementById('jfc-panel'); }

    function togglePanel() {
        var p = panel();
        if (!p) { buildPanel(); return; }
        if (p.classList.contains('open') && state.win.mode !== 'minimized') {
            closePanel();
        } else {
            openPanel();
        }
    }

    function openPanel() {
        var p = panel();
        if (!p) { buildPanel(); return; }
        if (state.win.mode === 'minimized') {
            state.win.mode = state.win.prevMode || 'float';
            saveWin();
        }
        p.classList.add('open');
        applyWindowState();
        startPolling();
    }

    function closePanel() {
        var p = panel();
        if (!p) { return; }
        p.classList.remove('open');
        clearDockPush();
        stopPolling();
    }

    function buildPanel() {
        var p = document.createElement('div');
        p.id = 'jfc-panel';
        p.innerHTML =
            '<div class="jfc-header" id="jfc-header">' +
                '<span class="jfc-title">Chat</span>' +
                '<span class="jfc-online"><i></i><span id="jfc-online-count"></span></span>' +
                '<div class="jfc-winctrls">' +
                    '<button class="jfc-icon" id="jfc-people" title="Membres / conversations">👥</button>' +
                    (state.isAdmin ? '<button class="jfc-icon" id="jfc-admin" title="Moderation">⚙️</button>' : '') +
                    '<button class="jfc-icon" id="jfc-dockl" title="Ancrer a gauche">⇤</button>' +
                    '<button class="jfc-icon" id="jfc-dockr" title="Ancrer a droite">⇥</button>' +
                    '<button class="jfc-icon" id="jfc-dockt" title="Ancrer en haut">⤒</button>' +
                    '<button class="jfc-icon" id="jfc-dockb" title="Ancrer en bas">⤓</button>' +
                    '<button class="jfc-icon" id="jfc-min" title="Reduire">–</button>' +
                    '<button class="jfc-icon" id="jfc-max" title="Agrandir / restaurer">▢</button>' +
                    '<button class="jfc-icon" id="jfc-close" title="Fermer">✕</button>' +
                '</div>' +
            '</div>' +
            '<div class="jfc-tabs" id="jfc-tabs"></div>' +
            '<div class="jfc-body" id="jfc-body"></div>' +
            '<div class="jfc-side" id="jfc-side"></div>' +
            '<div class="jfc-inputbar">' +
                '<input id="jfc-input" placeholder="Tapez un message..." autocomplete="off" />' +
                '<button class="jfc-icon" id="jfc-emoji" title="Emoji">😀</button>' +
                '<button class="jfc-gif" id="jfc-gif" title="Envoyer un GIF / image">GIF</button>' +
                '<button class="jfc-send" id="jfc-send" title="Envoyer">➤</button>' +
            '</div>' +
            '<div class="jfc-resize" id="jfc-resize" title="Redimensionner"></div>' +
            '<div class="jfc-dockresize" id="jfc-dockresize" title="Redimensionner"></div>';
        document.body.appendChild(p);

        p.querySelector('#jfc-close').onclick = closePanel;
        p.querySelector('#jfc-people').onclick = toggleSide;
        var adminBtn = p.querySelector('#jfc-admin');
        if (adminBtn) { adminBtn.onclick = openAdmin; }
        p.querySelector('#jfc-dockl').onclick = function () { toggleDock('left'); };
        p.querySelector('#jfc-dockr').onclick = function () { toggleDock('right'); };
        p.querySelector('#jfc-dockt').onclick = function () { toggleDock('top'); };
        p.querySelector('#jfc-dockb').onclick = function () { toggleDock('bottom'); };
        p.querySelector('#jfc-min').onclick = minimizePanel;
        p.querySelector('#jfc-max').onclick = toggleMaximize;
        p.querySelector('#jfc-send').onclick = doSend;
        p.querySelector('#jfc-gif').onclick = toggleGifPicker;
        p.querySelector('#jfc-emoji').onclick = toggleEmoji;
        var input = p.querySelector('#jfc-input');
        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); doSend(); }
        });

        // Empeche les touches tapees dans le chat de declencher les raccourcis Jellyfin
        // (espace = pause, f = plein ecran, fleches, etc.).
        ['keydown', 'keyup', 'keypress'].forEach(function (evt) {
            p.addEventListener(evt, function (e) { e.stopPropagation(); });
        });

        // Clic sur le bandeau reduit : re-ouvre.
        p.querySelector('#jfc-header').addEventListener('dblclick', function () {
            if (state.win.mode === 'minimized') { openPanel(); }
        });

        setupDrag(p.querySelector('#jfc-header'));
        setupResize(p.querySelector('#jfc-resize'));
        setupDockResize(p.querySelector('#jfc-dockresize'));

        applyWindowState();
        p.classList.add('open');

        // Init.
        checkAdminThenRender();
        loadSelf();
        state.tabs = [{ room: 'public', target: null, name: 'Public' }];
        renderTabs();
        selectRoom('public', null);
        startPolling();
    }

    /* ------------------------------------------------------------------ */
    /* Fenetre : deplacer, redimensionner, reduire, agrandir, ancrer.     */
    /* ------------------------------------------------------------------ */
    function applyWindowState() {
        var p = panel();
        if (!p) { return; }
        var w = state.win;
        p.classList.remove('docked', 'docked-left', 'docked-right', 'docked-top', 'docked-bottom', 'minimized', 'maximized');
        clearDockPush();

        if (w.mode === 'minimized') {
            p.classList.add('minimized');
            setFloatGeom(p);
        } else if (w.mode.indexOf('docked-') === 0) {
            var side = w.mode.split('-')[1]; // left | right | top | bottom
            p.classList.add('docked', 'docked-' + side);
            // Reset des offsets (auto pour ne pas heriter du top:60px/right:16px de base).
            p.style.left = p.style.right = p.style.top = p.style.bottom = 'auto';
            p.style.width = ''; p.style.height = '';
            if (side === 'left' || side === 'right') {
                // Largeur redimensionnable ; hauteur pleine geree par le CSS.
                p.style.width = w.dockW + 'px';
                p.style[side] = '0';
                document.documentElement.style.setProperty('--jfc-dock-w', w.dockW + 'px');
            } else {
                // Hauteur redimensionnable ; largeur pleine geree par le CSS.
                p.style.height = w.dockH + 'px';
                p.style[side] = '0';
                document.documentElement.style.setProperty('--jfc-dock-h', w.dockH + 'px');
            }
            document.documentElement.classList.add('jfc-docked-' + side);
        } else if (w.mode === 'maximized') {
            p.classList.add('maximized');
            p.style.left = '2vw'; p.style.top = '2vh';
            p.style.right = ''; p.style.bottom = '';
            p.style.width = '96vw'; p.style.height = '96vh';
        } else {
            setFloatGeom(p);
        }
        saveWin();
    }

    function setFloatGeom(p) {
        var w = state.win;
        p.style.right = ''; p.style.bottom = '';
        p.style.width = w.w + 'px';
        p.style.height = w.h + 'px';
        if (w.x == null || w.y == null) {
            p.style.left = ''; p.style.right = '16px'; p.style.top = '60px';
        } else {
            p.style.left = clamp(w.x, 0, window.innerWidth - 80) + 'px';
            p.style.top = clamp(w.y, 0, window.innerHeight - 40) + 'px';
        }
    }

    function clamp(v, min, max) { return Math.max(min, Math.min(max, v)); }

    function clearDockPush() {
        document.documentElement.classList.remove(
            'jfc-docked-left', 'jfc-docked-right', 'jfc-docked-top', 'jfc-docked-bottom');
    }

    function setupDockResize(handle) {
        handle.addEventListener('mousedown', function (e) {
            if (state.win.mode.indexOf('docked-') !== 0) { return; }
            e.preventDefault();
            e.stopPropagation();
            var p = panel();
            var mode = state.win.mode;
            var startX = e.clientX, startY = e.clientY;
            var startW = state.win.dockW, startH = state.win.dockH;

            function move(ev) {
                if (mode === 'docked-left') {
                    state.win.dockW = clamp(startW + (ev.clientX - startX), 200, window.innerWidth - 60);
                } else if (mode === 'docked-right') {
                    state.win.dockW = clamp(startW + (startX - ev.clientX), 200, window.innerWidth - 60);
                } else if (mode === 'docked-top') {
                    state.win.dockH = clamp(startH + (ev.clientY - startY), 150, window.innerHeight - 60);
                } else if (mode === 'docked-bottom') {
                    state.win.dockH = clamp(startH + (startY - ev.clientY), 150, window.innerHeight - 60);
                }
                if (mode === 'docked-left' || mode === 'docked-right') {
                    p.style.width = state.win.dockW + 'px';
                    document.documentElement.style.setProperty('--jfc-dock-w', state.win.dockW + 'px');
                } else {
                    p.style.height = state.win.dockH + 'px';
                    document.documentElement.style.setProperty('--jfc-dock-h', state.win.dockH + 'px');
                }
            }
            function up() {
                document.removeEventListener('mousemove', move);
                document.removeEventListener('mouseup', up);
                saveWin();
            }
            document.addEventListener('mousemove', move);
            document.addEventListener('mouseup', up);
        });
    }

    function toggleDock(side) {
        var target = 'docked-' + side;
        state.win.mode = (state.win.mode === target) ? 'float' : target;
        state.win.prevMode = state.win.mode;
        applyWindowState();
    }

    function toggleMaximize() {
        state.win.mode = (state.win.mode === 'maximized') ? 'float' : 'maximized';
        state.win.prevMode = state.win.mode;
        applyWindowState();
    }

    function minimizePanel() {
        if (state.win.mode !== 'minimized') {
            state.win.prevMode = state.win.mode;
            state.win.mode = 'minimized';
        }
        applyWindowState();
    }

    function setupDrag(handle) {
        handle.addEventListener('mousedown', function (e) {
            if (e.target.closest('.jfc-icon') || e.target.closest('button')) { return; }
            // En mode ancre ou reduit, deplacer repasse en flottant.
            if (state.win.mode !== 'float' && state.win.mode !== 'maximized') {
                state.win.mode = 'float';
            }
            var p = panel();
            var rect = p.getBoundingClientRect();
            var offX = e.clientX - rect.left;
            var offY = e.clientY - rect.top;
            state.win.w = rect.width; state.win.h = rect.height;
            e.preventDefault();

            function move(ev) {
                state.win.mode = 'float';
                p.classList.remove('docked', 'docked-left', 'docked-right', 'maximized');
                clearDockPush();
                state.win.x = clamp(ev.clientX - offX, 0, window.innerWidth - 80);
                state.win.y = clamp(ev.clientY - offY, 0, window.innerHeight - 40);
                p.style.left = state.win.x + 'px';
                p.style.top = state.win.y + 'px';
                p.style.right = ''; p.style.bottom = '';
                p.style.width = state.win.w + 'px';
                p.style.height = state.win.h + 'px';
            }
            function up() {
                document.removeEventListener('mousemove', move);
                document.removeEventListener('mouseup', up);
                saveWin();
            }
            document.addEventListener('mousemove', move);
            document.addEventListener('mouseup', up);
        });
    }

    function setupResize(handle) {
        handle.addEventListener('mousedown', function (e) {
            e.preventDefault();
            e.stopPropagation();
            var p = panel();
            var rect = p.getBoundingClientRect();
            var startX = e.clientX, startY = e.clientY;
            var startW = rect.width, startH = rect.height;
            var docked = state.win.mode === 'docked-left' || state.win.mode === 'docked-right';

            function move(ev) {
                if (docked) {
                    // En ancre, on ne redimensionne que la largeur.
                    var dw = state.win.mode === 'docked-right' ? (startX - ev.clientX) : (ev.clientX - startX);
                    state.win.dockW = clamp(startW + dw, 200, window.innerWidth - 60);
                    p.style.width = state.win.dockW + 'px';
                    document.documentElement.style.setProperty('--jfc-dock-w', state.win.dockW + 'px');
                } else {
                    state.win.w = clamp(startW + (ev.clientX - startX), 220, window.innerWidth - 20);
                    state.win.h = clamp(startH + (ev.clientY - startY), 180, window.innerHeight - 20);
                    p.style.width = state.win.w + 'px';
                    p.style.height = state.win.h + 'px';
                }
            }
            function up() {
                document.removeEventListener('mousemove', move);
                document.removeEventListener('mouseup', up);
                saveWin();
            }
            document.addEventListener('mousemove', move);
            document.addEventListener('mouseup', up);
        });
    }

    function checkAdminThenRender() {
        // On teste l'acces admin en tentant l'endpoint de moderation.
        api('ChatPlugin/admin/moderation').then(function () {
            state.isAdmin = true;
            if (!document.getElementById('jfc-admin')) {
                var ctrls = document.querySelector('.jfc-winctrls');
                if (!ctrls) { return; }
                var btn = document.createElement('button');
                btn.className = 'jfc-icon';
                btn.id = 'jfc-admin';
                btn.title = 'Moderation';
                btn.innerHTML = '⚙️';
                btn.onclick = openAdmin;
                ctrls.insertBefore(btn, document.getElementById('jfc-dockl'));
            }
        }).catch(function () { state.isAdmin = false; });
    }

    /* ------------------------------------------------------------------ */
    /* Onglets                                                            */
    /* ------------------------------------------------------------------ */
    function renderTabs() {
        var host = document.getElementById('jfc-tabs');
        host.innerHTML = '';
        state.tabs.forEach(function (t) {
            var tab = document.createElement('div');
            tab.className = 'jfc-tab' + (t.room === state.currentRoom ? ' active' : '');
            var label = document.createElement('span');
            label.textContent = t.name;
            tab.appendChild(label);
            if (t.target) {
                var x = document.createElement('span');
                x.className = 'jfc-tab-close';
                x.innerHTML = '×';
                x.onclick = function (e) { e.stopPropagation(); closeTab(t.room); };
                tab.appendChild(x);
            }
            tab.onclick = function () { selectRoom(t.room, t.target); };
            host.appendChild(tab);
        });
    }

    function closeTab(room) {
        state.tabs = state.tabs.filter(function (t) { return t.room !== room; });
        if (state.currentRoom === room) {
            selectRoom('public', null);
        }
        renderTabs();
    }

    function selectRoom(room, target) {
        state.currentRoom = room;
        state.currentTarget = target;
        if (!state.tabs.some(function (t) { return t.room === room; })) {
            state.tabs.push({ room: room, target: target, name: target ? state.usersById[target].Name : 'Public' });
        }
        renderTabs();
        var body = document.getElementById('jfc-body');
        body.innerHTML = '<div class="jfc-loading">Chargement...</div>';
        state.seen[room] = new Set();
        state.lastId[room] = 0;
        loadHistory(room, target);
    }

    function openDm(user) {
        var room = dmRoom(state.me, user.Id);
        state.usersById[user.Id] = user;
        selectRoom(room, user.Id);
        toggleSide(true);
    }

    function dmRoom(a, b) {
        var first = a < b ? a : b;
        var second = a < b ? b : a;
        return 'dm:' + first.replace(/-/g, '') + ':' + second.replace(/-/g, '');
    }

    /* ------------------------------------------------------------------ */
    /* Messages                                                           */
    /* ------------------------------------------------------------------ */
    function loadHistory(room, target) {
        var q = 'ChatPlugin/messages?history=true&roomId=' + encodeURIComponent(room);
        if (target) { q += '&targetUserId=' + encodeURIComponent(target); }
        api(q).then(function (res) {
            if (state.currentRoom !== room) { return; }
            var msgs = (res && res.messages) || [];
            state._lastPollTs = (res && res.serverNow) || Date.now();
            var body = document.getElementById('jfc-body');
            body.innerHTML = '';
            msgs.forEach(function (m) { appendMessage(m, room); });
            scrollBottom(true);
            if (target) { markRead(room, target); }
        }).catch(function (e) { showError(e.message); });
    }

    function poll() {
        var room = state.currentRoom;
        var target = state.currentTarget;
        var after = state.lastId[room] || 0;
        var since = state._lastPollTs || 0;
        var q = 'ChatPlugin/messages?roomId=' + encodeURIComponent(room) + '&after=' + after + '&delSince=' + since;
        if (target) { q += '&targetUserId=' + encodeURIComponent(target); }
        api(q).then(function (res) {
            if (state.currentRoom !== room) { return; }
            var msgs = (res && res.messages) || [];
            var deleted = (res && res.deleted) || [];
            var removed = (res && res.removed) || [];
            if (res && res.serverNow) { state._lastPollTs = res.serverNow; }
            var atBottom = isAtBottom();
            msgs.forEach(function (m) { appendMessage(m, room); });
            deleted.forEach(applyDeletion);
            removed.forEach(applyRemoval);
            if (msgs.length && atBottom) { scrollBottom(); }
            if (target && msgs.length) { markRead(room, target); }
        }).catch(function () { /* silencieux pendant le polling */ });
        updateOnline();
    }

    // Applique une suppression recue en direct : passe la bulle en "supprime".
    function applyDeletion(id) {
        var wrap = document.querySelector('#jfc-body .jfc-msg[data-id="' + id + '"]');
        if (!wrap) { return; }
        var bubble = wrap.querySelector('.jfc-bubble');
        if (bubble && !bubble.classList.contains('deleted')) {
            bubble.className = 'jfc-bubble deleted';
            bubble.textContent = 'Message supprime';
        }
        var del = wrap.querySelector('.jfc-msg-del');
        if (del) { del.remove(); }
    }

    // Retrait definitif (purge / vider salon) : enleve le message du DOM.
    function applyRemoval(id) {
        var wrap = document.querySelector('#jfc-body .jfc-msg[data-id="' + id + '"]');
        if (wrap) { wrap.remove(); }
    }

    function markRead(room, target) {
        var q = 'ChatPlugin/read?roomId=' + encodeURIComponent(room);
        if (target) { q += '&targetUserId=' + encodeURIComponent(target); }
        api(q, { method: 'POST' }).then(function () { fetchNotifications(); }).catch(function () {});
    }

    function appendMessage(m, room) {
        state.seen[room] = state.seen[room] || new Set();
        if (state.seen[room].has(m.Id)) { return; }
        state.seen[room].add(m.Id);
        state.lastId[room] = Math.max(state.lastId[room] || 0, m.Id);

        var body = document.getElementById('jfc-body');
        var wrap = document.createElement('div');
        wrap.className = 'jfc-msg' + (m.Mine ? ' mine' : '');
        wrap.dataset.id = m.Id;

        if (!m.Mine) {
            wrap.appendChild(avatar(m.SenderAvatarUrl, m.SenderName));
        }

        var col = document.createElement('div');
        col.className = 'jfc-msg-col';

        var meta = document.createElement('div');
        meta.className = 'jfc-msg-meta';
        meta.innerHTML = (m.Mine ? '' : '<b>' + esc(m.SenderName) + '</b> ')
            + '<span>' + timeAgo(m.Timestamp) + '</span>';
        col.appendChild(meta);

        var bubble = document.createElement('div');
        bubble.className = 'jfc-bubble';
        if (m.Deleted) {
            bubble.classList.add('deleted');
            bubble.textContent = 'Message supprime';
        } else if (m.Type === 'image') {
            var img = document.createElement('img');
            img.className = 'jfc-img';
            img.src = m.Content;
            img.loading = 'lazy';
            bubble.appendChild(img);
        } else {
            bubble.innerHTML = linkify(esc(m.Content));
        }
        col.appendChild(bubble);

        // Actions (supprimer mon message, ou moderer si admin).
        if (!m.Deleted && (m.Mine || state.isAdmin)) {
            var act = document.createElement('button');
            act.className = 'jfc-msg-del';
            act.title = 'Supprimer';
            act.innerHTML = '🗑';
            act.onclick = function () { deleteMessage(m, wrap); };
            col.appendChild(act);
        }

        wrap.appendChild(col);
        body.appendChild(wrap);
    }

    function linkify(html) {
        return html.replace(/(https?:\/\/[^\s]+)/g, '<a href="$1" target="_blank" rel="noopener">$1</a>');
    }

    function deleteMessage(m, wrap) {
        var path = state.isAdmin && !m.Mine
            ? 'ChatPlugin/admin/message/' + m.Id
            : 'ChatPlugin/messages/' + m.Id;
        api(path, { method: 'DELETE' }).then(function () {
            var bubble = wrap.querySelector('.jfc-bubble');
            bubble.className = 'jfc-bubble deleted';
            bubble.textContent = 'Message supprime';
            var del = wrap.querySelector('.jfc-msg-del');
            if (del) { del.remove(); }
        }).catch(function (e) { showError(e.message); });
    }

    function doSend() {
        var input = document.getElementById('jfc-input');
        var text = input.value.trim();
        if (!text) { return; }
        input.value = '';
        var payload = { content: text, type: 'text' };
        if (state.currentTarget) {
            payload.targetUserId = state.currentTarget;
        } else {
            payload.roomId = state.currentRoom;
        }
        api('ChatPlugin/messages', { method: 'POST', body: payload }).then(function (m) {
            if (m) { appendMessage(m, state.currentRoom); scrollBottom(); }
        }).catch(function (e) { showError(e.message); input.value = text; });
    }

    function loadSelf() {
        api('ChatPlugin/self').then(function (s) {
            state.gifEnabled = !!(s && s.GifEnabled);
        }).catch(function () {});
    }

    function toggleGifPicker() {
        var existing = document.getElementById('jfc-gifpicker');
        if (existing) { existing.remove(); return; }
        if (!state.gifEnabled) {
            showError('Recherche de GIF non configuree (cle Klipy manquante).');
            return;
        }
        var pick = document.createElement('div');
        pick.id = 'jfc-gifpicker';
        pick.innerHTML =
            '<div class="jfc-gif-top">' +
                '<input id="jfc-gif-q" placeholder="Rechercher un GIF..." autocomplete="off" />' +
                '<span class="jfc-gif-brand">via Klipy</span>' +
            '</div>' +
            '<div class="jfc-gif-grid" id="jfc-gif-grid"></div>';
        document.getElementById('jfc-panel').appendChild(pick);
        var q = pick.querySelector('#jfc-gif-q');
        var t;
        q.addEventListener('input', function () {
            clearTimeout(t);
            t = setTimeout(function () { searchGif(q.value); }, 350);
        });
        q.focus();
        searchGif(''); // tendances au depart
    }

    function searchGif(query) {
        var grid = document.getElementById('jfc-gif-grid');
        if (!grid) { return; }
        grid.innerHTML = '<div class="jfc-loading">Recherche...</div>';
        api('ChatPlugin/gif/search?q=' + encodeURIComponent(query || '')).then(function (res) {
            if (!document.getElementById('jfc-gif-grid')) { return; }
            grid.innerHTML = '';
            if (!res || !res.enabled) {
                grid.innerHTML = '<div class="jfc-side-empty">Recherche de GIF non configuree (cle Klipy manquante).</div>';
                return;
            }
            if (!res.items || !res.items.length) {
                grid.innerHTML = '<div class="jfc-side-empty">Aucun resultat.</div>';
                return;
            }
            res.items.forEach(function (g) {
                var img = document.createElement('img');
                img.src = g.preview || g.url;
                img.className = 'jfc-gif-item';
                img.loading = 'lazy';
                img.onclick = function () { sendGif(g.url); };
                grid.appendChild(img);
            });
        }).catch(function (e) {
            if (grid) { grid.innerHTML = '<div class="jfc-err">' + esc(e.message) + '</div>'; }
        });
    }

    function sendGif(url) {
        var payload = { content: url, type: 'image' };
        if (state.currentTarget) {
            payload.targetUserId = state.currentTarget;
        } else {
            payload.roomId = state.currentRoom;
        }
        api('ChatPlugin/messages', { method: 'POST', body: payload }).then(function (m) {
            if (m) { appendMessage(m, state.currentRoom); scrollBottom(); }
            var p = document.getElementById('jfc-gifpicker');
            if (p) { p.remove(); }
        }).catch(function (e) { showError(e.message); });
    }

    // Genere l'ensemble des emojis a partir des plages Unicode (construit une seule fois).
    function emojiGridHtml() {
        if (state._emojiHtml) { return state._emojiHtml; }
        var ranges = [
            [0x1F600, 0x1F64F], [0x1F900, 0x1F9FF], [0x1F300, 0x1F5FF],
            [0x1F680, 0x1F6FF], [0x1FA70, 0x1FAFF], [0x2600, 0x26FF],
            [0x2700, 0x27BF], [0x2B00, 0x2BFF], [0x1F1E6, 0x1F1FF]
        ];
        var html = '';
        ranges.forEach(function (r) {
            for (var c = r[0]; c <= r[1]; c++) {
                try { html += '<button type="button">' + String.fromCodePoint(c) + '</button>'; } catch (e) {}
            }
        });
        state._emojiHtml = html;
        return html;
    }

    function toggleEmoji() {
        var existing = document.getElementById('jfc-emoji-pop');
        if (existing) { existing.remove(); return; }
        var pop = document.createElement('div');
        pop.id = 'jfc-emoji-pop';
        pop.innerHTML = '<div class="jfc-emoji-grid">' + emojiGridHtml() + '</div>';
        pop.querySelector('.jfc-emoji-grid').addEventListener('click', function (ev) {
            if (ev.target.tagName === 'BUTTON') {
                var input = document.getElementById('jfc-input');
                input.value += ev.target.textContent;
                input.focus();
            }
        });
        document.getElementById('jfc-panel').appendChild(pop);
    }

    /* ------------------------------------------------------------------ */
    /* Panneau lateral : utilisateurs, amis, blocages                     */
    /* ------------------------------------------------------------------ */
    function toggleSide(forceClose) {
        var side = document.getElementById('jfc-side');
        if (forceClose === true) { side.classList.remove('open'); return; }
        side.classList.toggle('open');
        if (side.classList.contains('open')) { loadSide(); }
    }

    function loadSide() {
        var side = document.getElementById('jfc-side');
        side.innerHTML = '<div class="jfc-loading">Chargement...</div>';
        Promise.all([
            api('ChatPlugin/users'),
            api('ChatPlugin/relations')
        ]).then(function (res) {
            state.users = res[0] || [];
            state.users.forEach(function (u) { state.usersById[u.Id] = u; });
            renderSide(res[0] || [], res[1] || {});
        }).catch(function (e) { side.innerHTML = '<div class="jfc-err">' + esc(e.message) + '</div>'; });
    }

    function renderSide(users, rel) {
        var side = document.getElementById('jfc-side');
        side.innerHTML = '';

        var head = document.createElement('div');
        head.className = 'jfc-side-head';
        head.innerHTML = '<span>Membres</span>';
        var close = document.createElement('button');
        close.className = 'jfc-icon';
        close.innerHTML = '✕';
        close.onclick = function () { toggleSide(true); };
        head.appendChild(close);
        side.appendChild(head);

        // Conversations privees (avec compteur de non-lus).
        var convos = state.notif.conversations || [];
        if (convos.length) {
            side.appendChild(sectionTitle('Conversations'));
            convos.forEach(function (c) { side.appendChild(convRow(c)); });
        }

        // Demandes recues.
        if (rel.incoming && rel.incoming.length) {
            side.appendChild(sectionTitle('Demandes recues'));
            rel.incoming.forEach(function (u) { side.appendChild(userRow(u, 'incoming')); });
        }

        // Amis.
        side.appendChild(sectionTitle('Amis'));
        var friends = (rel.friends || []);
        if (!friends.length) {
            side.appendChild(emptyRow('Aucun ami pour l\'instant'));
        }
        friends.forEach(function (u) { side.appendChild(userRow(u, 'friend')); });

        // Tous les membres.
        side.appendChild(sectionTitle('Tous les membres'));
        users.forEach(function (u) {
            if (u.Relation === 'friend' || u.Relation === 'blocked') { return; }
            side.appendChild(userRow(u, u.Relation));
        });

        // Bloques.
        if (rel.blocked && rel.blocked.length) {
            side.appendChild(sectionTitle('Bloques'));
            rel.blocked.forEach(function (u) { side.appendChild(userRow(u, 'blocked')); });
        }
    }

    function sectionTitle(t) {
        var d = document.createElement('div');
        d.className = 'jfc-side-section';
        d.textContent = t;
        return d;
    }

    function emptyRow(t) {
        var d = document.createElement('div');
        d.className = 'jfc-side-empty';
        d.textContent = t;
        return d;
    }

    function userRow(u, relation) {
        var row = document.createElement('div');
        row.className = 'jfc-user';
        row.appendChild(avatar(u.AvatarUrl, u.Name));

        var info = document.createElement('div');
        info.className = 'jfc-user-info';
        info.innerHTML = '<span class="jfc-user-name">' + esc(u.Name) + '</span>'
            + (u.IsAdmin ? '<span class="jfc-badge">admin</span>' : '');
        row.appendChild(info);

        var actions = document.createElement('div');
        actions.className = 'jfc-user-actions';

        if (relation !== 'blocked') {
            actions.appendChild(iconAction('💬', 'Message', function () { openDm(u); }));
        }

        if (relation === 'friend') {
            actions.appendChild(iconAction('✖', 'Retirer', function () { rel('remove', u); }));
        } else if (relation === 'pending') {
            var p = document.createElement('span'); p.className = 'jfc-tagmini'; p.textContent = 'en attente';
            actions.appendChild(p);
            actions.appendChild(iconAction('✖', 'Annuler', function () { rel('remove', u); }));
        } else if (relation === 'incoming') {
            actions.appendChild(iconAction('✔', 'Accepter', function () { rel('accept', u); }));
            actions.appendChild(iconAction('✖', 'Refuser', function () { rel('remove', u); }));
        } else if (relation === 'blocked') {
            actions.appendChild(iconAction('🔓', 'Debloquer', function () { rel('unblock', u); }));
        } else {
            actions.appendChild(iconAction('➕', 'Ajouter en ami', function () { rel('request', u); }));
        }

        if (relation !== 'blocked') {
            actions.appendChild(iconAction('🚫', 'Bloquer', function () { rel('block', u); }));
        }

        row.appendChild(actions);
        return row;
    }

    function convRow(c) {
        var row = document.createElement('div');
        row.className = 'jfc-user jfc-conv';
        row.appendChild(avatar(c.avatarUrl, c.name));
        var info = document.createElement('div');
        info.className = 'jfc-user-info';
        info.innerHTML = '<span class="jfc-user-name">' + esc(c.name) + '</span>';
        row.appendChild(info);
        if (c.unread > 0) {
            var b = document.createElement('span');
            b.className = 'jfc-btnbadge';
            b.textContent = c.unread > 99 ? '99+' : c.unread;
            row.appendChild(b);
        }
        row.onclick = function () {
            openDm({ Id: c.userId, Name: c.name, AvatarUrl: c.avatarUrl });
        };
        return row;
    }

    function iconAction(icon, title, fn) {
        var b = document.createElement('button');
        b.className = 'jfc-uaction';
        b.title = title;
        b.innerHTML = icon;
        b.onclick = fn;
        return b;
    }

    function rel(action, u) {
        api('ChatPlugin/relations/' + action + '/' + u.Id, { method: 'POST' })
            .then(function () { loadSide(); })
            .catch(function (e) { showError(e.message); });
    }

    /* ------------------------------------------------------------------ */
    /* Panneau admin (moderation)                                         */
    /* ------------------------------------------------------------------ */
    function openAdmin() {
        var modal = document.createElement('div');
        modal.className = 'jfc-modal';
        modal.innerHTML =
            '<div class="jfc-modal-box">' +
                '<div class="jfc-modal-head"><b>Moderation</b><button class="jfc-icon jfc-modal-close">✕</button></div>' +
                '<div class="jfc-modal-body">' +
                    '<div class="jfc-mod-actions">' +
                        '<button id="jfc-clear-public" class="jfc-btn danger">Vider le salon public</button>' +
                        (state.currentTarget ? '<button id="jfc-clear-dm" class="jfc-btn danger">Vider cette conversation</button>' : '') +
                    '</div>' +
                    '<div class="jfc-mod-title">Sanctionner un membre</div>' +
                    '<div id="jfc-mod-users"></div>' +
                    '<div class="jfc-mod-title">Sanctions actives</div>' +
                    '<div id="jfc-mod-list"></div>' +
                '</div>' +
            '</div>';
        document.body.appendChild(modal);
        modal.querySelector('.jfc-modal-close').onclick = function () { modal.remove(); };
        modal.onclick = function (e) { if (e.target === modal) { modal.remove(); } };

        modal.querySelector('#jfc-clear-public').onclick = function () {
            if (confirm('Vider tout le salon public ?')) {
                api('ChatPlugin/admin/room/public', { method: 'DELETE' })
                    .then(function () { if (state.currentRoom === 'public') { selectRoom('public', null); } })
                    .catch(function (e) { showError(e.message); });
            }
        };
        var clearDm = modal.querySelector('#jfc-clear-dm');
        if (clearDm) {
            clearDm.onclick = function () {
                if (confirm('Vider cette conversation ?')) {
                    api('ChatPlugin/admin/room/' + encodeURIComponent(state.currentRoom), { method: 'DELETE' })
                        .then(function () { selectRoom(state.currentRoom, state.currentTarget); })
                        .catch(function (e) { showError(e.message); });
                }
            };
        }

        renderModUsers(modal);
        renderModList(modal);
    }

    function renderModUsers(modal) {
        var host = modal.querySelector('#jfc-mod-users');
        api('ChatPlugin/users').then(function (users) {
            host.innerHTML = '';
            (users || []).forEach(function (u) {
                var row = document.createElement('div');
                row.className = 'jfc-mod-row';
                row.appendChild(avatar(u.AvatarUrl, u.Name));
                var n = document.createElement('span'); n.className = 'jfc-user-name'; n.textContent = u.Name;
                row.appendChild(n);
                row.appendChild(modBtn('Mute 1h', function () { sanction('mute', u.Id, 60); modal.remove(); openAdmin(); }));
                row.appendChild(modBtn('Ban', function () { sanction('ban', u.Id, 0); modal.remove(); openAdmin(); }));
                row.appendChild(modBtn('Purger', function () {
                    if (confirm('Supprimer TOUS les messages de ' + u.Name + ' ?')) {
                        api('ChatPlugin/admin/purge/' + u.Id, { method: 'POST' })
                            .then(function () { showError('Messages de ' + u.Name + ' purges.'); })
                            .catch(function (e) { showError(e.message); });
                    }
                }));
                host.appendChild(row);
            });
        });
    }

    function renderModList(modal) {
        var host = modal.querySelector('#jfc-mod-list');
        api('ChatPlugin/admin/moderation').then(function (list) {
            host.innerHTML = '';
            if (!list || !list.length) { host.innerHTML = '<div class="jfc-side-empty">Aucune sanction active</div>'; return; }
            list.forEach(function (m) {
                var row = document.createElement('div');
                row.className = 'jfc-mod-row';
                var tags = (m.banned ? '<span class="jfc-badge red">ban</span>' : '')
                    + (m.muted ? '<span class="jfc-badge orange">mute</span>' : '');
                row.innerHTML = '<span class="jfc-user-name">' + esc(m.name) + '</span>' + tags;
                row.appendChild(modBtn('Lever', function () {
                    api('ChatPlugin/admin/clear/' + m.userId, { method: 'POST' })
                        .then(function () { modal.remove(); openAdmin(); });
                }));
                host.appendChild(row);
            });
        });
    }

    function modBtn(label, fn) {
        var b = document.createElement('button');
        b.className = 'jfc-btn small';
        b.textContent = label;
        b.onclick = fn;
        return b;
    }

    function sanction(kind, userId, minutes) {
        var reason = window.prompt('Raison (optionnel) :', '') || '';
        api('ChatPlugin/admin/' + kind, {
            method: 'POST',
            body: { userId: userId, durationMinutes: minutes, reason: reason }
        }).catch(function (e) { showError(e.message); });
    }

    /* ------------------------------------------------------------------ */
    /* Divers                                                             */
    /* ------------------------------------------------------------------ */
    function updateOnline() {
        var el = document.getElementById('jfc-online-count');
        if (!el) { return; }
        var n = state.notif.online || 0;
        el.textContent = n + ' en ligne';
    }

    function startPolling() {
        stopPolling();
        poll();
        loadSideCount();
        state.polling = setInterval(poll, POLL_MS);
    }

    function stopPolling() {
        if (state.polling) { clearInterval(state.polling); state.polling = null; }
    }

    function loadSideCount() {
        api('ChatPlugin/users').then(function (users) {
            state.users = users || [];
            state.users.forEach(function (u) { state.usersById[u.Id] = u; });
            updateOnline();
        }).catch(function () {});
    }

    function body() { return document.getElementById('jfc-body'); }
    function isAtBottom() {
        var b = body();
        return b && (b.scrollHeight - b.scrollTop - b.clientHeight) < 60;
    }
    function scrollBottom(instant) {
        var b = body();
        if (b) { b.scrollTop = b.scrollHeight; }
    }

    function showError(msg) {
        var t = document.createElement('div');
        t.className = 'jfc-toast';
        t.textContent = msg;
        document.body.appendChild(t);
        setTimeout(function () { t.classList.add('show'); }, 10);
        setTimeout(function () { t.remove(); }, 3500);
    }

    boot();
})();
