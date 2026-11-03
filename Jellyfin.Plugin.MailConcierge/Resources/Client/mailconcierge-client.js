/*
 * Mail Concierge web client integration (Jellyfin 12.1, jellyfin-web 12.1).
 *
 * Injected in index.html by IndexInjectionStartupFilter. It must never break the web client:
 * every hook runs through safe(), every anchor is checked, and when an anchor is missing nothing happens.
 * Elements are found by route (location.hash) and by stable ids or static class names written in the
 * jellyfin-web sources, never by generated CSS class names. All text is set with textContent.
 */
(function () {
    'use strict';

    // Replaced by ClientScriptController with { "<lang>": { "<key>": "<text>" } }.
    const STRINGS = /*MAILCONCIERGE_STRINGS*/null;
    const SELF = document.currentScript;
    const MARK = 'data-mailconcierge';
    const USERNAME_KEY = 'mailconcierge.username';

    function log() {
        try {
            console.debug.apply(console, ['[Mail Concierge]'].concat(Array.prototype.slice.call(arguments)));
        } catch (e) {
            // Nothing to do.
        }
    }

    function safe(fn) {
        return function () {
            try {
                const result = fn.apply(this, arguments);
                if (result && typeof result.catch === 'function') {
                    result.catch(log);
                }
                return result;
            } catch (e) {
                log(e);
                return undefined;
            }
        };
    }

    if (!STRINGS || typeof STRINGS !== 'object' || !SELF || !SELF.src || typeof MutationObserver !== 'function') {
        return;
    }

    // Base of this server (origin + base URL), from the URL this script was loaded from.
    let BASE;
    try {
        const own = new URL(SELF.src, window.location.href);
        BASE = own.origin + own.pathname.replace(/\/MailConcierge\/client\.js$/i, '');
    } catch (e) {
        log(e);
        return;
    }

    // ---- Helpers -------------------------------------------------------------------------------------------

    // The display language chosen in Jellyfin (jellyfin-web keeps it in localStorage as "<userId>-language", or
    // "language" without a user), else the lang attribute it maintains, else the browser. The first one that is set
    // decides: an unsupported language gives English, not the next candidate.
    function language() {
        const candidates = [];
        try {
            const userId = window.ApiClient && typeof window.ApiClient.getCurrentUserId === 'function' ? window.ApiClient.getCurrentUserId() : null;
            if (userId) {
                candidates.push(window.localStorage.getItem(userId + '-language'));
            }
            candidates.push(window.localStorage.getItem('language'));
        } catch (e) {
            log(e);
        }
        candidates.push(document.documentElement.getAttribute('lang'), navigator.language);
        const raw = (candidates.find(function (c) { return c && String(c).trim(); }) || 'en').toLowerCase();
        const code = raw.split(/[-_]/)[0];
        return Object.prototype.hasOwnProperty.call(STRINGS, code) ? code : 'en';
    }

    function t(key) {
        const own = STRINGS[language()] || {};
        const en = STRINGS.en || {};
        return own[key] || en[key] || key;
    }

    function el(tag, attrs, children) {
        const node = document.createElement(tag);
        Object.keys(attrs || {}).forEach(function (name) {
            const value = attrs[name];
            if (value === true) {
                node.setAttribute(name, '');
            } else if (value !== false && value !== null && value !== undefined) {
                node.setAttribute(name, String(value));
            }
        });
        (Array.isArray(children) ? children : [children]).forEach(function (child) {
            if (child === null || child === undefined) {
                return;
            }
            node.appendChild(typeof child === 'string' ? document.createTextNode(child) : child);
        });
        return node;
    }

    function setText(node, text) {
        if (node && node.textContent !== text) {
            node.textContent = text;
        }
    }

    function normId(id) {
        return String(id || '').replace(/-/g, '').toLowerCase();
    }

    function route() {
        const hash = (window.location.hash || '').replace(/^#!?/, '');
        const q = hash.indexOf('?');
        return {
            path: (q < 0 ? hash : hash.slice(0, q)) || '/',
            params: new URLSearchParams(q < 0 ? '' : hash.slice(q + 1))
        };
    }

    function storeGet(key) {
        try {
            return window.sessionStorage.getItem(key) || '';
        } catch (e) {
            return '';
        }
    }

    function storeSet(key, value) {
        try {
            if (value) {
                window.sessionStorage.setItem(key, value);
            } else {
                window.sessionStorage.removeItem(key);
            }
        } catch (e) {
            // Storage may be blocked: the user name is then typed again.
        }
    }

    function apiClient() {
        const c = window.ApiClient;
        return c && typeof c.ajax === 'function' && typeof c.getUrl === 'function' ? c : null;
    }

    function currentUserId() {
        const c = apiClient();
        return c && typeof c.getCurrentUserId === 'function' ? c.getCurrentUserId() : null;
    }

    // Authenticated call through the web client's ApiClient. Rejects with the fetch Response on HTTP errors.
    function api(type, path, body) {
        const c = apiClient();
        if (!c || !(typeof c.accessToken !== 'function' || c.accessToken())) {
            return Promise.reject(new Error('No signed-in ApiClient'));
        }
        const request = { type: type, url: c.getUrl(path) };
        if (body !== undefined) {
            request.data = JSON.stringify(body);
            request.contentType = 'application/json';
        }
        if (type === 'GET') {
            request.dataType = 'json';
        }
        return Promise.resolve(c.ajax(request));
    }

    function statusOf(error) {
        return error && typeof error.status === 'number' ? error.status : 0;
    }

    function codeOf(error) {
        if (!error || typeof error.json !== 'function') {
            return Promise.resolve(null);
        }
        return Promise.resolve(error.json()).then(function (b) {
            return b && typeof b.code === 'string' ? b.code : null;
        }, function () {
            return null;
        });
    }

    function inputField(id, label, attrs, help) {
        return el('div', { class: 'inputContainer' }, [
            el('label', { class: 'inputLabel inputLabelUnfocused', for: id }, label),
            el('input', Object.assign({ id: id, class: 'emby-input' }, attrs)),
            help ? el('div', { class: 'fieldDescription' }, help) : null
        ]);
    }

    function submitButton(text) {
        return el('button', { type: 'submit', class: 'raised button-submit block emby-button' }, el('span', {}, text));
    }

    // ---- Shared state (the e-mail list is reloaded on each route change) -----------------------------------

    let admin = { id: null, value: undefined };
    let emails = { value: undefined, loading: false };

    // True, false, or undefined while unknown (the answer then triggers a new pass).
    function isAdmin() {
        const c = apiClient();
        const id = currentUserId();
        if (!c || !id || typeof c.getCurrentUser !== 'function') {
            return false;
        }
        if (admin.id !== id) {
            const state = { id: id, value: undefined };
            admin = state;
            Promise.resolve(c.getCurrentUser()).then(function (user) {
                state.value = !!(user && user.Policy && user.Policy.IsAdministrator);
            }, function () {
                state.value = false;
            }).then(schedule);
        }
        return admin.value;
    }

    // Map of normalized user id to { email, confirmed }, or undefined while loading. Users without a record are absent.
    function emailMap() {
        if (emails.value === undefined && !emails.loading) {
            const state = { value: undefined, loading: true };
            emails = state;
            api('GET', 'MailConcierge/Users/Emails').then(function (list) {
                const map = new Map();
                (Array.isArray(list) ? list : []).forEach(function (e) {
                    if (e && e.userId) {
                        map.set(normId(e.userId), { email: e.email || '', confirmed: e.confirmed === true });
                    }
                });
                state.value = map;
            }, function (e) {
                log(e);
                state.value = null;
            }).then(function () {
                state.loading = false;
                schedule();
            });
        }
        return emails.value;
    }

    // ---- 1. Users list ------------------------------------------------------------------------------------------
    // Route #/dashboard/users (src/apps/dashboard/routes/users/index.tsx): <Page id='userProfilesPage'>,
    // add-user button <button id='btnAddUser' class='fab submit sectionTitleButton'> rendered by
    // SectionTitleContainer/IconButtonElement through dangerouslySetInnerHTML (React does not reconcile its children).
    // User cards (src/components/dashboard/users/UserCardBox.tsx): <div data-userid=...> in '.localUsers',
    // with a '.cardFooter'.
    function applyUsersList(r) {
        if (r.path !== '/dashboard/users' || isAdmin() !== true) {
            return;
        }
        const page = document.getElementById('userProfilesPage');
        if (!page) {
            return;
        }

        const add = page.querySelector('#btnAddUser');
        if (add && add.parentNode && !page.querySelector('[' + MARK + '="invite"]')) {
            // Two icons do not fit in the round fab: the link icon alone.
            // The href must equal the menu entry exactly (else the dashboard also highlights "Extensions"), so the
            // Invitations tab is requested through sessionStorage. Key shared with Configuration/configPage.html (TAB_KEY).
            const link = el('a', {
                'data-mailconcierge': 'invite',
                href: '#/configurationpage?name=MailConcierge',
                title: t('invite.button'),
                'aria-label': t('invite.button'),
                class: 'fab submit sectionTitleButton emby-button'
            }, el('span', { class: 'material-icons link', 'aria-hidden': 'true' }));
            link.addEventListener('click', function () {
                try { window.sessionStorage.setItem('mailconcierge.cfgTab', 'invitations'); } catch (e) { /* not available */ }
            });
            add.after(link);
        }

        const map = emailMap();
        if (!map) {
            return;
        }
        page.querySelectorAll('.localUsers [data-userid]').forEach(function (card) {
            const record = map.get(normId(card.getAttribute('data-userid')));
            const missing = !(record && record.confirmed);
            const badge = card.querySelector('[' + MARK + '="noemail"]');
            if (missing && !badge) {
                const footer = card.querySelector('.cardFooter');
                if (footer) {
                    footer.appendChild(el('div', { 'data-mailconcierge': 'noemail', class: 'cardText cardText-secondary', style: 'opacity:.75;font-size:.85em' }, t('users.noEmail')));
                }
            } else if (!missing && badge) {
                badge.remove();
            }
        });
    }

    // ---- 2. User edit page (administrator) ---------------------------------------------------------------------
    // Route #/dashboard/users/:userId/profile (src/apps/dashboard/routes/_asyncRoutes.ts 'users/:userId/:tab',
    // routes/users/edit.tsx): <Page id='usersEditPage'>; the profile tab renders
    // features/users/components/Profile.tsx <form className='editUserProfileForm'>. Our own form goes just before it.
    function applyUserEdit(r) {
        const m = /^\/dashboard\/users\/([0-9a-f-]{32,36})\/profile$/i.exec(r.path);
        if (!m || isAdmin() !== true) {
            return;
        }
        const uid = normId(m[1]);
        const page = document.getElementById('usersEditPage');
        const form = page && page.querySelector('form.editUserProfileForm');
        if (!form || !form.parentNode) {
            return;
        }

        let block = page.querySelector('[' + MARK + '="admin-email"]');
        if (block && block.getAttribute('data-userid') !== uid) {
            block.remove();
            block = null;
        }
        if (!block) {
            const input = el('input', { id: 'mailConciergeAdminEmail', class: 'emby-input', type: 'email', autocomplete: 'off', maxlength: '254' });
            const status = el('div', { class: 'fieldDescription', role: 'status', 'aria-live': 'polite' });
            block = el('form', { 'data-mailconcierge': 'admin-email', 'data-userid': uid, class: 'verticalSection', style: 'margin-bottom:2em' }, [
                el('div', { class: 'inputContainer' }, [
                    el('label', { class: 'inputLabel inputLabelUnfocused', for: 'mailConciergeAdminEmail' }, t('admin.email.label')),
                    input,
                    el('div', { class: 'fieldDescription' }, t('admin.email.help'))
                ]),
                submitButton(t('save')),
                status
            ]);
            block.addEventListener('submit', safe(function (e) {
                e.preventDefault();
                const email = input.value.trim();
                setText(status, '');
                return api('PUT', 'MailConcierge/Users/' + encodeURIComponent(uid) + '/Email', { email: email, language: '' }).then(function () {
                    setText(status, t('saved'));
                    if (emails.value) {
                        emails.value.set(uid, { email: email, confirmed: email !== '' });
                    }
                }, function (err) {
                    return codeOf(err).then(function (code) {
                        setText(status, code === 'invalidEmail' ? t('error.invalidEmail') : t('error.generic'));
                    });
                });
            }));
            form.parentNode.insertBefore(block, form);
        }

        const map = emailMap();
        if (map && !block.hasAttribute('data-loaded')) {
            block.setAttribute('data-loaded', '');
            const input = block.querySelector('input');
            const record = map.get(uid);
            if (input && !input.value) {
                input.value = record ? record.email : '';
            }
        }
    }

    // ---- 3. My profile -----------------------------------------------------------------------------------------
    // Route #/userprofile?userId= (reached from #/mypreferencesmenu, src/apps/legacy/routes/user/settings/index.tsx;
    // page src/apps/legacy/routes/user/userprofile.tsx): <Page id='userProfilePage'>, which renders
    // components/dashboard/users/UserPasswordForm.tsx (<div><form className='updatePasswordForm'>). Our form goes
    // before that component. Only for the signed-in user's own profile: the endpoint changes the caller's address.
    function applyMyProfile(r) {
        if (r.path !== '/userprofile') {
            return;
        }
        const me = currentUserId();
        const asked = r.params.get('userId');
        if (!me || (asked && normId(asked) !== normId(me))) {
            return;
        }
        const page = document.getElementById('userProfilePage');
        const passwordForm = page && page.querySelector('form.updatePasswordForm');
        const host = passwordForm && passwordForm.parentNode;
        if (!host || !host.parentNode || page.querySelector('[' + MARK + '="me-email"]')) {
            return;
        }

        const current = el('p', {});
        const pending = el('p', { hidden: true });
        const email = el('input', { id: 'mailConciergeMyEmail', class: 'emby-input', type: 'email', autocomplete: 'email', required: true, maxlength: '254' });
        const password = el('input', { id: 'mailConciergeMyPassword', class: 'emby-input', type: 'password', autocomplete: 'current-password', required: true });
        const status = el('div', { class: 'fieldDescription', role: 'status', 'aria-live': 'polite' });

        // Hidden until the current state is known; it stays hidden when the endpoint is not there.
        const block = el('form', { 'data-mailconcierge': 'me-email', class: 'detailSection', style: 'margin:0 auto 2em', hidden: true }, [
            el('h2', {}, t('profile.title')),
            current,
            pending,
            el('div', { class: 'inputContainer' }, [
                el('label', { class: 'inputLabel inputLabelUnfocused', for: 'mailConciergeMyEmail' }, t('profile.email')),
                email
            ]),
            el('div', { class: 'inputContainer' }, [
                el('label', { class: 'inputLabel inputLabelUnfocused', for: 'mailConciergeMyPassword' }, t('profile.password')),
                password,
                // Jellyfin locks the account after LoginAttemptsBeforeLockout wrong passwords.
                el('div', { class: 'fieldDescription' }, t('profile.passwordWarning'))
            ]),
            submitButton(t('profile.submit')),
            status
        ]);

        function showState(address, pendingAddress) {
            setText(current, address ? t('profile.current') + ' ' + address : t('profile.none'));
            if (pendingAddress) {
                setText(pending, t('profile.pending') + ' ' + pendingAddress);
                pending.hidden = false;
            } else {
                setText(pending, '');
                pending.hidden = true;
            }
        }

        let address = '';
        block.addEventListener('submit', safe(function (e) {
            e.preventDefault();
            setText(status, '');
            const wanted = email.value.trim();
            return api('POST', 'MailConcierge/Me/Email', { email: wanted, currentPassword: password.value }).then(function () {
                password.value = '';
                email.value = '';
                showState(address, wanted);
                setText(status, t('profile.sent'));
            }, function (err) {
                password.value = '';
                const code = statusOf(err);
                if (code === 403) {
                    setText(status, t('profile.error.wrongPassword'));
                } else if (code === 423) {
                    setText(status, t('profile.error.passwordLocked'));
                } else if (code === 429) {
                    setText(status, t('profile.error.rateLimited'));
                } else if (code === 503) {
                    setText(status, t('profile.error.unavailable'));
                } else if (code === 400) {
                    setText(status, t('error.invalidEmail'));
                } else {
                    setText(status, t('error.generic'));
                }
            });
        }));

        host.parentNode.insertBefore(block, host);
        api('GET', 'MailConcierge/Me/Email').then(function (state) {
            address = (state && state.email) || '';
            showState(address, state && state.pendingEmail);
            block.hidden = false;
        }, log);
    }

    // ---- 4. Forgot password ------------------------------------------------------------------------------------
    // Route #/forgotpassword (src/apps/legacy/routes/session/forgotPassword/index.tsx): <Page id='forgotPasswordPage'>,
    // user name <Input id='txtName'>. On a PinCode answer it calls components/alert with the "file created on
    // your server" sentences plus result.PinFile; the dialog (components/dialog/dialog.template.html) shows them in
    // '.formDialog .dialogContentInner .text'. PinFile holds Mail Concierge's forgot.neutral sentence: we look for it there.
    function applyForgotDialog(r) {
        if (r.path !== '/forgotpassword') {
            return;
        }
        const sentences = Object.keys(STRINGS).map(function (code) {
            return STRINGS[code]['forgot.neutral'];
        }).filter(Boolean);
        document.querySelectorAll('.formDialog .dialogContentInner .text').forEach(function (node) {
            if (node.hasAttribute(MARK)) {
                return;
            }
            const text = node.textContent || '';
            if (!sentences.some(function (s) { return text.indexOf(s) >= 0; })) {
                return;
            }
            node.setAttribute(MARK, 'forgot');
            node.textContent = '';
            node.appendChild(el('p', {}, t('forgot.neutral')));
            node.appendChild(el('p', {}, t('forgot.next')));
        });
    }

    // ---- 5. PIN page -------------------------------------------------------------------------------------------
    // Route #/forgotpasswordpin (legacy view src/apps/legacy/controllers/session/resetPassword/index.html):
    // <div data-role="page" class="... forgotPasswordPinPage"><form class="forgotPasswordPinForm"> with
    // <input is="emby-input" id="txtPin"> in a '.inputContainer'. The legacy controller (index.js) listens to
    // 'submit' on the form; our capture listener on document runs first and stops it.
    function applyPinPage(r) {
        if (r.path !== '/forgotpasswordpin') {
            return;
        }
        document.querySelectorAll('input#txtPin').forEach(function (pin) {
            const form = pin.closest('form');
            if (!form || form.querySelector('[' + MARK + '="pin-user"]')) {
                return;
            }
            const anchor = pin.closest('.inputContainer') || pin;
            if (!anchor.parentNode) {
                return;
            }
            const field = inputField('mailConciergePinUser', t('pin.username'), { type: 'text', autocomplete: 'username', required: true, value: storeGet(USERNAME_KEY) });
            field.setAttribute(MARK, 'pin-user');
            anchor.parentNode.insertBefore(field, anchor);
            anchor.after(el('div', { 'data-mailconcierge': 'pin-error', class: 'fieldDescription', role: 'alert', hidden: true }));
        });
    }

    function submitPin(form, username, pin) {
        const error = form.querySelector('[' + MARK + '="pin-error"]');
        const button = form.querySelector('button[type="submit"]');
        function fail() {
            if (error) {
                setText(error, t('pin.error'));
                error.hidden = false;
            }
            if (button) {
                button.disabled = false;
            }
        }
        if (error) {
            error.hidden = true;
        }
        if (!username || !pin) {
            fail();
            return Promise.resolve();
        }
        if (button) {
            button.disabled = true;
        }
        return fetch(BASE + '/MailConcierge/Reset/Pin', {
            method: 'POST',
            credentials: 'omit',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ username: username, pin: pin })
        }).then(function (response) {
            return response.ok ? response.json() : null;
        }).then(function (body) {
            if (!body || typeof body.session !== 'string' || !body.session) {
                fail();
                return;
            }
            storeSet(USERNAME_KEY, '');
            window.location.href = BASE + '/MailConcierge/Reset?s=' + encodeURIComponent(body.session);
        }, function (e) {
            log(e);
            fail();
        });
    }

    function onSubmitCapture(e) {
        const form = e.target;
        if (!(form instanceof HTMLFormElement)) {
            return;
        }
        const r = route();
        if (r.path === '/forgotpassword') {
            // Remember the user name for the PIN page. The native submit goes on.
            const name = form.closest('#forgotPasswordPage') && form.querySelector('#txtName');
            if (name) {
                storeSet(USERNAME_KEY, String(name.value || '').trim());
            }
            return;
        }
        if (r.path !== '/forgotpasswordpin') {
            return;
        }
        const pin = form.querySelector('input#txtPin');
        const user = form.querySelector('[' + MARK + '="pin-user"] input');
        if (!pin || !user) {
            return;
        }
        e.preventDefault();
        e.stopImmediatePropagation();
        const username = String(user.value || '').trim();
        storeSet(USERNAME_KEY, username);
        submitPin(form, username, String(pin.value || '').trim()).catch(log);
    }

    // ---- Driver ------------------------------------------------------------------------------------------------

    const hooks = [applyUsersList, applyUserEdit, applyMyProfile, applyForgotDialog, applyPinPage].map(safe);

    // The hash router navigates with history.pushState, which fires no hashchange: route changes are seen here.
    let lastPath = null;

    function apply() {
        const r = route();
        if (r.path !== lastPath) {
            // Fresh data for each page visit.
            lastPath = r.path;
            emails = { value: undefined, loading: false };
        }
        hooks.forEach(function (hook) {
            hook(r);
        });
    }

    // One pass per animation frame at most: our own insertions trigger one more pass, which finds everything
    // in place and changes nothing, so the observer settles.
    let scheduled = false;
    const runScheduled = safe(function () {
        scheduled = false;
        apply();
    });

    function schedule() {
        if (scheduled) {
            return;
        }
        scheduled = true;
        if (typeof window.requestAnimationFrame === 'function') {
            window.requestAnimationFrame(runScheduled);
        } else {
            window.setTimeout(runScheduled, 100);
        }
    }

    safe(function () {
        document.addEventListener('submit', safe(onSubmitCapture), true);
        window.addEventListener('hashchange', safe(schedule));
        window.addEventListener('popstate', safe(schedule));
        new MutationObserver(safe(schedule)).observe(document.documentElement, { childList: true, subtree: true });
        schedule();
    })();
})();
