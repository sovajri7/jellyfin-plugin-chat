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
        blockedByMe: []
    };

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
        injectStyle();
        buildLauncher();
    }

    function injectStyle() {
        if (document.getElementById('jf-chat-style')) {
            return;
        }
        var link = document.createElement('link');
        link.id = 'jf-chat-style';
        link.rel = 'stylesheet';
        link.href = apiUrl('ChatPlugin/client.css');
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
        if (document.getElementById('jfc-launcher')) {
            return;
        }
        var btn = document.createElement('button');
        btn.id = 'jfc-launcher';
        btn.title = 'Chat en Direct';
        btn.innerHTML = '💬';
        btn.onclick = togglePanel;
        document.body.appendChild(btn);
    }

    function togglePanel() {
        var panel = document.getElementById('jfc-panel');
        if (panel) {
            var closing = panel.classList.contains('open');
            panel.classList.toggle('open');
            if (closing) {
                stopPolling();
            } else {
                startPolling();
            }
            return;
        }
        buildPanel();
    }

    function buildPanel() {
        var panel = document.createElement('div');
        panel.id = 'jfc-panel';
        panel.innerHTML =
            '<div class="jfc-header">' +
                '<span class="jfc-title">Chat en Direct</span>' +
                '<span class="jfc-online"><i></i><span id="jfc-online-count"></span></span>' +
                '<button class="jfc-icon" id="jfc-people" title="Utilisateurs">👥</button>' +
                (state.isAdmin ? '<button class="jfc-icon" id="jfc-admin" title="Moderation">⚙️</button>' : '') +
                '<button class="jfc-icon" id="jfc-close">✕</button>' +
            '</div>' +
            '<div class="jfc-tabs" id="jfc-tabs"></div>' +
            '<div class="jfc-body" id="jfc-body"></div>' +
            '<div class="jfc-side" id="jfc-side"></div>' +
            '<div class="jfc-inputbar">' +
                '<input id="jfc-input" placeholder="Tapez un message..." autocomplete="off" />' +
                '<button class="jfc-icon" id="jfc-emoji" title="Emoji">😀</button>' +
                '<button class="jfc-gif" id="jfc-gif" title="Envoyer un GIF / image">GIF</button>' +
                '<button class="jfc-send" id="jfc-send" title="Envoyer">➤</button>' +
            '</div>';
        document.body.appendChild(panel);

        panel.querySelector('#jfc-close').onclick = togglePanel;
        panel.querySelector('#jfc-people').onclick = toggleSide;
        var adminBtn = panel.querySelector('#jfc-admin');
        if (adminBtn) { adminBtn.onclick = openAdmin; }
        panel.querySelector('#jfc-send').onclick = doSend;
        panel.querySelector('#jfc-gif').onclick = doGif;
        panel.querySelector('#jfc-emoji').onclick = toggleEmoji;
        var input = panel.querySelector('#jfc-input');
        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); doSend(); }
        });

        requestAnimationFrame(function () { panel.classList.add('open'); });

        // Init.
        checkAdminThenRender();
        state.tabs = [{ room: 'public', target: null, name: 'Public' }];
        renderTabs();
        selectRoom('public', null);
        startPolling();
    }

    function checkAdminThenRender() {
        // On teste l'acces admin en tentant l'endpoint de moderation.
        api('ChatPlugin/admin/moderation').then(function () {
            state.isAdmin = true;
            if (!document.getElementById('jfc-admin')) {
                var header = document.querySelector('.jfc-header');
                var btn = document.createElement('button');
                btn.className = 'jfc-icon';
                btn.id = 'jfc-admin';
                btn.title = 'Moderation';
                btn.innerHTML = '⚙️';
                btn.onclick = openAdmin;
                header.insertBefore(btn, document.getElementById('jfc-close'));
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
        api(q).then(function (msgs) {
            if (state.currentRoom !== room) { return; }
            var body = document.getElementById('jfc-body');
            body.innerHTML = '';
            (msgs || []).forEach(function (m) { appendMessage(m, room); });
            scrollBottom(true);
        }).catch(function (e) { showError(e.message); });
    }

    function poll() {
        var room = state.currentRoom;
        var target = state.currentTarget;
        var after = state.lastId[room] || 0;
        var q = 'ChatPlugin/messages?roomId=' + encodeURIComponent(room) + '&after=' + after;
        if (target) { q += '&targetUserId=' + encodeURIComponent(target); }
        api(q).then(function (msgs) {
            if (state.currentRoom !== room) { return; }
            var atBottom = isAtBottom();
            (msgs || []).forEach(function (m) { appendMessage(m, room); });
            if ((msgs || []).length && atBottom) { scrollBottom(); }
        }).catch(function () { /* silencieux pendant le polling */ });
        updateOnline();
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

    function doGif() {
        var url = window.prompt('Colle l\'URL d\'un GIF ou d\'une image :');
        if (!url) { return; }
        var payload = { content: url, type: 'image' };
        if (state.currentTarget) {
            payload.targetUserId = state.currentTarget;
        } else {
            payload.roomId = state.currentRoom;
        }
        api('ChatPlugin/messages', { method: 'POST', body: payload }).then(function (m) {
            if (m) { appendMessage(m, state.currentRoom); scrollBottom(); }
        }).catch(function (e) { showError(e.message); });
    }

    var EMOJIS = ['😀','😂','😍','😎','😅','😢','😡','👍','👎','❤️','🔥','🎉','🙏','💯','👀','🤡'];
    function toggleEmoji() {
        var existing = document.getElementById('jfc-emoji-pop');
        if (existing) { existing.remove(); return; }
        var pop = document.createElement('div');
        pop.id = 'jfc-emoji-pop';
        EMOJIS.forEach(function (e) {
            var b = document.createElement('button');
            b.textContent = e;
            b.onclick = function () {
                var input = document.getElementById('jfc-input');
                input.value += e;
                input.focus();
                pop.remove();
            };
            pop.appendChild(b);
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
        el.textContent = (state.users.length || '') + ' membres';
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
