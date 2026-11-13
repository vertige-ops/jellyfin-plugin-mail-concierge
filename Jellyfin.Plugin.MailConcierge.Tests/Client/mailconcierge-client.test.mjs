// Tests of the script injected in the web client (Review Focus 1: a changed or absent web client must not break).
// Run from this folder: npm ci && node --test --test-timeout=30000
// Fixtures are trimmed copies of the jellyfin-web 12.1 markup the script anchors to (source named in each file).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { JSDOM, VirtualConsole } from 'jsdom';

const here = (p) => fileURLToPath(new URL(p, import.meta.url));
const resources = here('../../Jellyfin.Plugin.MailConcierge/Resources/');
const fixture = (name) => readFileSync(here(`./fixtures/${name}.html`), 'utf8');

// Same assembly as ClientScriptController: client strings plus the two server sentences, inlined in place of the placeholder.
const STRINGS = {};
// Languages are discovered from Resources/Strings/{lang}.json, like the server does.
for (const lang of readdirSync(`${resources}Strings`).filter((f) => f.endsWith('.json')).map((f) => f.slice(0, -5)).sort()) {
    const clientFile = `${resources}Client/strings.${lang}.json`;
    const client = existsSync(clientFile) ? JSON.parse(readFileSync(clientFile, 'utf8')) : {};
    const server = JSON.parse(readFileSync(`${resources}Strings/${lang}.json`, 'utf8'));
    STRINGS[lang] = { ...client, 'language.name': server['language.name'], 'forgot.neutral': server['forgot.neutral'], 'pin.error': server['page.error.invalid'] };
}
const raw = readFileSync(`${resources}Client/mailconcierge-client.js`, 'utf8');
assert.ok(raw.includes('/*MAILCONCIERGE_STRINGS*/null'));
assert.ok(raw.includes('const FLAGS = /*MAILCONCIERGE_FLAGS*/null || {};'));
const scriptFor = (flags) => raw.replace('/*MAILCONCIERGE_STRINGS*/null', JSON.stringify(STRINGS)).replace('/*MAILCONCIERGE_FLAGS*/null', JSON.stringify(flags));
const SCRIPT = scriptFor({ emailLogin: false });

const frame = () => new Promise((resolve) => setTimeout(resolve, 40));
async function settle(n = 3) {
    for (let i = 0; i < n; i++) {
        await frame();
    }
}

const ADMIN_ID = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';

/**
 * Loads the script in a jsdom window showing `body` at `hash`.
 * options: lang, anon (no ApiClient), admin, ajax(req), fetch(url, init), before(window).
 */
async function load(hash, body, options = {}) {
    const jsdomErrors = [];
    const virtualConsole = new VirtualConsole();
    virtualConsole.on('jsdomError', (e) => jsdomErrors.push(e.message));
    const dom = new JSDOM(`<!doctype html><html lang="${options.lang || 'en'}"><head></head><body>${body}</body></html>`, {
        url: 'http://jf.test/jf/web/index.html' + hash,
        runScripts: 'outside-only',
        pretendToBeVisual: true,
        virtualConsole
    });
    const w = dom.window;
    const calls = [];
    const logs = [];
    const pageErrors = [];
    w.addEventListener('error', (e) => pageErrors.push(e.error || e.message));
    w.console.debug = (...args) => logs.push(args.map(String).join(' '));
    if (!options.anon) {
        w.ApiClient = {
            getUrl: (p) => 'http://jf.test/jf/' + p,
            accessToken: () => 'token',
            getCurrentUserId: () => ADMIN_ID,
            getCurrentUser: options.getCurrentUser || (() => Promise.resolve({ Policy: { IsAdministrator: options.admin !== false } })),
            ajax: (req) => {
                calls.push({ kind: 'ajax', ...req });
                return options.ajax ? options.ajax(req) : Promise.resolve(null);
            }
        };
    }
    w.fetch = (url, init) => {
        calls.push({ kind: 'fetch', url, init });
        return Promise.resolve(options.fetch ? options.fetch(url, init) : { ok: false, json: () => Promise.resolve({}) });
    };
    if (options.before) {
        options.before(w);
    }

    const script = w.document.createElement('script');
    script.src = 'http://jf.test/jf/MailConcierge/client.js';
    w.document.head.appendChild(script);
    Object.defineProperty(w.document, 'currentScript', { configurable: true, get: () => script });
    w.eval(options.flags ? scriptFor(options.flags) : SCRIPT);
    Object.defineProperty(w.document, 'currentScript', { configurable: true, get: () => null });
    await settle();
    return { w, d: w.document, calls, logs, pageErrors, jsdomErrors };
}

const marked = (d) => d.querySelectorAll('[data-mailconcierge]').length;
const submit = (w, form) => {
    const event = new w.Event('submit', { cancelable: true, bubbles: true });
    form.dispatchEvent(event);
    return event;
};

// Records the DOM mutations made after the page settled: none means the observer does not loop on its own insertions.
async function mutationsAfterSettle(w) {
    const records = [];
    const observer = new w.MutationObserver((r) => records.push(...r));
    observer.observe(w.document.documentElement, { childList: true, subtree: true, attributes: true, characterData: true });
    await settle(5);
    observer.disconnect();
    return records.length;
}

const emailList = () => Promise.resolve([{ userId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', email: 'alice@example.org', confirmed: true }]);

test('users list: invite link after the add-user button, badge only for users without a confirmed address', async () => {
    const { d, w, pageErrors } = await load('#/dashboard/users', fixture('users'), { ajax: emailList });

    const invite = d.querySelectorAll('[data-mailconcierge="invite"]');
    assert.equal(invite.length, 1);
    // Exact URL: jellyfin-web only un-highlights "Extensions" when pathname+search equals the menu entry.
    assert.equal(invite[0].getAttribute('href'), '#/configurationpage?name=MailConcierge');
    assert.equal(w.sessionStorage.getItem('mailconcierge.cfgTab'), null);
    invite[0].addEventListener('click', (e) => e.preventDefault());
    invite[0].dispatchEvent(new w.MouseEvent('click', { bubbles: true, cancelable: true }));
    assert.equal(w.sessionStorage.getItem('mailconcierge.cfgTab'), 'invitations');
    assert.equal(invite[0].previousElementSibling.id, 'btnAddUser');
    assert.equal(d.querySelector(`[data-userid="${ADMIN_ID}"] [data-mailconcierge="noemail"]`), null);
    assert.equal(d.querySelector('[data-userid^="b"] [data-mailconcierge="noemail"]').textContent, 'No e-mail');
    assert.equal(await mutationsAfterSettle(w), 0);
    assert.deepEqual(pageErrors, []);
});

test('users list: nothing for a non-administrator', async () => {
    const { d } = await load('#/dashboard/users', fixture('users'), { admin: false, ajax: emailList });
    assert.equal(marked(d), 0);
});

test('idempotent: unrelated mutations re-run apply without duplicates or new requests', async () => {
    const { d, w, calls } = await load('#/dashboard/users', fixture('users'), { ajax: emailList });
    const before = marked(d);
    const requests = calls.length;
    assert.equal(before, 2);

    for (let i = 0; i < 5; i++) {
        d.querySelector('.localUsers').appendChild(d.createElement('span'));
        d.body.appendChild(d.createElement('div'));
        await frame();
    }
    await settle();

    assert.equal(marked(d), before);
    assert.equal(d.querySelectorAll('[data-mailconcierge="invite"]').length, 1);
    assert.equal(calls.length, requests);
    assert.equal(await mutationsAfterSettle(w), 0);
});

test('re-applies when the framework redraws the page and removes our elements', async () => {
    const { d } = await load('#/dashboard/users', fixture('users'), { ajax: emailList });
    d.querySelector('[data-mailconcierge="invite"]').remove();
    d.querySelector('[data-mailconcierge="noemail"]').remove();
    await settle();
    assert.equal(d.querySelectorAll('[data-mailconcierge="invite"]').length, 1);
    assert.equal(d.querySelectorAll('[data-mailconcierge="noemail"]').length, 1);
});

test('anchors missing: no throw and nothing inserted, on every route', async () => {
    for (const hash of ['#/dashboard/users', `#/dashboard/users/${ADMIN_ID}/profile`, `#/userprofile?userId=${ADMIN_ID}`, '#/forgotpassword', '#/forgotpasswordpin']) {
        const { d, w, pageErrors } = await load(hash, '<div class="page"><form><input id="other"></form></div>', { ajax: emailList });
        assert.equal(marked(d), 0, hash);
        assert.deepEqual(pageErrors, [], hash);
        assert.equal(await mutationsAfterSettle(w), 0, hash);
        // A submit on a foreign form is left alone.
        assert.equal(submit(w, d.querySelector('form')).defaultPrevented, false, hash);
    }
});

test('no ApiClient and no strings: the script does nothing', async () => {
    const { d, pageErrors } = await load('#/dashboard/users', fixture('users'), { anon: true });
    assert.equal(marked(d), 0);
    assert.deepEqual(pageErrors, []);

    const dom = new JSDOM(`<body>${fixture('users')}</body>`, { url: 'http://jf.test/web/index.html#/dashboard/users', runScripts: 'outside-only' });
    assert.doesNotThrow(() => dom.window.eval(raw));
    assert.equal(dom.window.document.querySelectorAll('[data-mailconcierge]').length, 0);
});

test('a hook throwing on purpose does not break the others', async () => {
    // getCurrentUser throws synchronously: the users-list hook fails, inside safe().
    const { d, w, calls, logs, pageErrors } = await load('#/dashboard/users', fixture('users'), {
        getCurrentUser: () => {
            throw new Error('boom');
        }
    });
    assert.equal(marked(d), 0);
    assert.deepEqual(pageErrors, []);

    // DOM insertion throwing in one hook: the PIN hook (next route, same window) still works.
    const after = w.Element.prototype.after;
    w.Element.prototype.after = function () {
        throw new Error('after broken');
    };
    d.body.innerHTML = fixture('forgotpasswordpin');
    w.location.hash = '#/forgotpasswordpin';
    await settle();
    assert.ok(logs.some((l) => l.includes('boom')));
    assert.ok(logs.some((l) => l.includes('after broken')));
    // The username field was inserted before the error div insertion failed; the capture listener is still alive.
    assert.ok(d.querySelector('#mailConciergePinUser'));
    d.querySelector('#mailConciergePinUser').value = 'bob';
    const event = submit(w, d.querySelector('form'));
    await settle();
    assert.ok(event.defaultPrevented);
    assert.ok(calls.some((c) => c.kind === 'fetch' && c.url.endsWith('/MailConcierge/Reset/Pin')));
    w.Element.prototype.after = after;
    assert.deepEqual(pageErrors, []);
});

test('admin user page: e-mail form before the profile form, prefilled, PUT on save', async () => {
    const { d, w, calls } = await load(`#/dashboard/users/${ADMIN_ID}/profile`, fixture('user-edit'), {
        ajax: (req) => (req.type === 'GET' ? emailList() : Promise.resolve({ status: 204 }))
    });
    const form = d.querySelector('[data-mailconcierge="admin-email"]');
    assert.ok(form);
    assert.ok(form.nextElementSibling.classList.contains('editUserProfileForm'));
    assert.equal(form.querySelector('input').value, 'alice@example.org');

    form.querySelector('input').value = ' new@example.org ';
    submit(w, form);
    await settle();
    const put = calls.find((c) => c.type === 'PUT');
    assert.equal(put.url, `http://jf.test/jf/MailConcierge/Users/${ADMIN_ID}/Email`);
    assert.deepEqual(JSON.parse(put.data), { email: 'new@example.org', language: '' });
    assert.equal(form.querySelector('[role="status"]').textContent, 'Saved.');
});

test('my profile (fr): state, lockout warning, POST and error mapping', async () => {
    const { d, w, calls } = await load(`#/userprofile?userId=${ADMIN_ID}`, fixture('userprofile'), {
        lang: 'fr',
        ajax: (req) => (req.type === 'GET'
            ? Promise.resolve({ email: 'me@example.org', pendingEmail: 'next@example.org', language: 'fr' })
            : Promise.reject({ status: 403, json: () => Promise.resolve({ code: 'wrongPassword' }) }))
    });
    const form = d.querySelector('[data-mailconcierge="me-email"]');
    assert.ok(form && !form.hidden);
    assert.ok(form.textContent.includes('Adresse actuelle : me@example.org'));
    assert.ok(form.textContent.includes('En attente de confirmation : next@example.org'));
    assert.ok(form.textContent.includes(STRINGS.fr['profile.passwordWarning']));
    // The e-mail language is the Mail Concierge default language: the profile has no language field.
    assert.equal(d.querySelector('#mailConciergeMyLanguage'), null);
    assert.equal(form.querySelector('select'), null);

    form.querySelector('#mailConciergeMyEmail').value = 'n@example.org';
    form.querySelector('#mailConciergeMyPassword').value = 'secret';
    submit(w, form);
    await settle();
    const post = calls.find((c) => c.type === 'POST');
    assert.deepEqual(JSON.parse(post.data), { email: 'n@example.org', currentPassword: 'secret' });
    assert.equal(form.querySelector('[role="status"]').textContent, STRINGS.fr['profile.error.wrongPassword']);
    assert.equal(form.querySelector('#mailConciergeMyPassword').value, '');
});

test('my profile: a locked password (423) shows the dedicated message', async () => {
    const { d, w } = await load(`#/userprofile?userId=${ADMIN_ID}`, fixture('userprofile'), {
        lang: 'en',
        ajax: (req) => (req.type === 'GET'
            ? Promise.resolve({ email: 'me@example.org', pendingEmail: null })
            : Promise.reject({ status: 423, json: () => Promise.resolve({ code: 'passwordLocked' }) }))
    });
    const form = d.querySelector('[data-mailconcierge="me-email"]');
    form.querySelector('#mailConciergeMyEmail').value = 'n@example.org';
    form.querySelector('#mailConciergeMyPassword').value = 'secret';
    submit(w, form);
    await settle();
    assert.equal(form.querySelector('[role="status"]').textContent, STRINGS.en['profile.error.passwordLocked']);
});

for (const [lang, code, key] of [['en', 'closed', 'profile.error.closed'], ['fr', 'closed', 'profile.error.closed'], ['en', 'unavailable', 'profile.error.unavailable']]) {
    test(`my profile: a 503 with code ${code} shows ${key} (${lang})`, async () => {
        const { d, w } = await load(`#/userprofile?userId=${ADMIN_ID}`, fixture('userprofile'), {
            lang,
            ajax: (req) => (req.type === 'GET'
                ? Promise.resolve({ email: 'me@example.org', pendingEmail: null })
                : Promise.reject({ status: 503, json: () => Promise.resolve({ code }) }))
        });
        const form = d.querySelector('[data-mailconcierge="me-email"]');
        form.querySelector('#mailConciergeMyEmail').value = 'n@example.org';
        form.querySelector('#mailConciergeMyPassword').value = 'secret';
        submit(w, form);
        await settle();
        assert.ok(STRINGS[lang][key]);
        assert.equal(form.querySelector('[role="status"]').textContent, STRINGS[lang][key]);
    });
}

test('language: the Jellyfin display language wins over the browser language, an unsupported one gives English', async () => {
    const profile = (lang, stored) => load(`#/userprofile?userId=${ADMIN_ID}`, fixture('userprofile'), {
        lang,
        before: (w) => { Object.defineProperty(w.navigator, 'language', { value: 'ja-JP', configurable: true }); if (stored) { w.localStorage.setItem(`${ADMIN_ID}-language`, stored); } },
        ajax: (req) => (req.type === 'GET' ? Promise.resolve({ email: 'me@example.org', pendingEmail: 'next@example.org', language: 'fr' }) : Promise.resolve(null))
    });
    const stored = await profile('en', 'fr-FR');
    assert.ok(stored.d.querySelector('[data-mailconcierge="me-email"]').textContent.includes('Adresse actuelle'));
    const attribute = await profile('fr', null);
    assert.ok(attribute.d.querySelector('[data-mailconcierge="me-email"]').textContent.includes('Adresse actuelle'));
    const unsupported = await profile('en', 'ja');
    assert.ok(unsupported.d.querySelector('[data-mailconcierge="me-email"]').textContent.includes('Current address'));
    const browser = await load(`#/userprofile?userId=${ADMIN_ID}`, fixture('userprofile'), {
        before: (w) => { w.document.documentElement.removeAttribute('lang'); Object.defineProperty(w.navigator, 'language', { value: 'fr-FR', configurable: true }); },
        ajax: (req) => (req.type === 'GET' ? Promise.resolve({ email: 'me@example.org', language: 'fr' }) : Promise.resolve(null))
    });
    assert.ok(browser.d.querySelector('[data-mailconcierge="me-email"]').textContent.includes('Adresse actuelle'));
});

test('my profile: nothing on another user\'s profile', async () => {
    const { d } = await load('#/userprofile?userId=cccccccccccccccccccccccccccccccc', fixture('userprofile'));
    assert.equal(marked(d), 0);
});

test('forgot password: user name remembered, dialog text replaced once', async () => {
    const { d, w } = await load('#/forgotpassword', fixture('forgotpassword'), { anon: true });
    const event = submit(w, d.querySelector('form'));
    assert.equal(event.defaultPrevented, false);
    assert.equal(w.sessionStorage.getItem('mailconcierge.username'), 'bob');

    d.body.insertAdjacentHTML('beforeend', fixture('dialog'));
    d.querySelector('.formDialog .text').innerHTML = 'The following file has been created on your server and contains instructions on how to proceed<br/><br/>'
        + 'Enter PIN here to finish Password reset<br/><br/>' + STRINGS.en['forgot.neutral'] + '<br/>';
    await settle();
    const text = d.querySelector('.formDialog .text');
    assert.equal(text.textContent, STRINGS.en['forgot.neutral'] + STRINGS.en['forgot.next']);
    assert.ok(!text.textContent.includes('created on your server'));
    assert.equal(await mutationsAfterSettle(w), 0);
});

test('PIN page: user name field, native submit stopped, anonymous Reset/Pin, then reset page', async () => {
    const { d, w, calls, jsdomErrors } = await load('#/forgotpasswordpin', fixture('forgotpasswordpin'), {
        anon: true,
        before: (win) => win.sessionStorage.setItem('mailconcierge.username', 'bob'),
        fetch: () => ({ ok: true, json: () => Promise.resolve({ session: 'S1/x' }) })
    });
    const user = d.querySelector('#mailConciergePinUser');
    assert.equal(user.value, 'bob');
    assert.ok(user.closest('.inputContainer').nextElementSibling.querySelector('#txtPin'));

    const native = [];
    d.querySelector('form').addEventListener('submit', () => native.push(1));
    const event = submit(w, d.querySelector('form'));
    await settle();

    assert.ok(event.defaultPrevented);
    assert.equal(native.length, 0);
    const call = calls.find((c) => c.kind === 'fetch');
    assert.equal(call.url, 'http://jf.test/jf/MailConcierge/Reset/Pin');
    assert.equal(call.init.credentials, 'omit');
    assert.deepEqual(JSON.parse(call.init.body), { username: 'bob', pin: '12345678' });
    // jsdom does not implement navigation: the attempt to leave for the reset page is reported here.
    assert.ok(jsdomErrors.some((m) => m.includes('navigation')));
});

test('PIN page: generic error shown inline on failure', async () => {
    const { d, w } = await load('#/forgotpasswordpin', fixture('forgotpasswordpin'), { anon: true });
    d.querySelector('#mailConciergePinUser').value = 'bob';
    submit(w, d.querySelector('form'));
    await settle();
    const error = d.querySelector('[data-mailconcierge="pin-error"]');
    assert.equal(error.hidden, false);
    assert.equal(error.textContent, STRINGS.en['pin.error']);
    assert.equal(d.querySelector('button[type="submit"]').disabled, false);
});

for (const [lang, label] of [['fr', 'Nom d’utilisateur ou e-mail'], ['en', 'User name or e-mail']]) {
    test(`sign-in: label is rewritten when the flag is on (${lang})`, async () => {
        const { d } = await load('#/login', fixture('login'), { lang, anon: true, flags: { emailLogin: true } });
        assert.equal(d.querySelector('label[for="txtManualName"]').textContent, STRINGS[lang]['login.usernameOrEmail']);
        assert.equal(STRINGS[lang]['login.usernameOrEmail'], label);
        assert.equal(d.querySelector('label[for="txtManualPassword"]').textContent, 'Password');
        assert.equal(d.querySelector('button span').textContent, 'Sign In');
    });
}

test('sign-in: label is untouched when the flag is off', async () => {
    const { d } = await load('#/login', fixture('login'), { anon: true, flags: { emailLogin: false } });
    assert.equal(d.querySelector('label[for="txtManualName"]').textContent, 'User');
    const none = await load('#/login', fixture('login'), { anon: true });
    assert.equal(none.d.querySelector('label[for="txtManualName"]').textContent, 'User');
});

test('sign-in: rewriting twice changes nothing, and a redrawn label is rewritten again', async () => {
    const { d, w } = await load('#/login', fixture('login'), { anon: true, flags: { emailLogin: true } });
    const label = d.querySelector('label[for="txtManualName"]');
    assert.equal(label.textContent, 'User name or e-mail');
    assert.equal(await mutationsAfterSettle(w), 0);
    label.textContent = 'User';
    await settle();
    assert.equal(label.textContent, 'User name or e-mail');
    assert.equal(await mutationsAfterSettle(w), 0);
});

test('sign-in: no label on the page, no throw, nothing touched', async () => {
    const { d, pageErrors, logs } = await load('#/home', '<label for="other">Other</label>', { anon: true, flags: { emailLogin: true } });
    assert.equal(d.querySelector('label').textContent, 'Other');
    assert.deepEqual(pageErrors, []);
    assert.deepEqual(logs, []);
});
