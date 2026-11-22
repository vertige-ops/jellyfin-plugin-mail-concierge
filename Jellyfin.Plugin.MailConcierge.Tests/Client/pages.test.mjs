// Tests of the public pages script (Resources/Pages/pages.js) in jsdom: what the visitor sees after a success.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { JSDOM } from 'jsdom';

const here = (p) => fileURLToPath(new URL(p, import.meta.url));
const script = readFileSync(here('../../Jellyfin.Plugin.MailConcierge/Resources/Pages/pages.js'), 'utf8');
const pageHtml = readFileSync(here('../../Jellyfin.Plugin.MailConcierge/Resources/Pages/cancel.html'), 'utf8')
    .replaceAll('{{s:page.cancel.title}}', 'Cancel the e-mail change')
    .replaceAll('{{s:page.cancel.intro}}', 'Intro')
    .replaceAll('{{s:page.cancel.password}}', 'Password')
    .replaceAll('{{s:page.cancel.submit}}', 'Cancel the change')
    .replaceAll('{{s:page.cancel.success.title}}', 'Change cancelled')
    .replaceAll('{{s:page.cancel.success}}', 'Restored.')
    .replaceAll('{{s:page.signin}}', 'Go to sign in')
    .replaceAll('{{base}}', '');

test('after a success the form title is hidden, the success stays, the tab is renamed and nothing redirects', async () => {
    const data = JSON.stringify({ page: 'cancel', lang: 'en', base: '', minPasswordLength: 8, strings: {} });
    const dom = new JSDOM(`<!DOCTYPE html><html><head><title>Cancel the e-mail change - Pet Cinema</title></head><body>${pageHtml}<script type="application/json" id="mailconcierge-data">${data}</script></body></html>`,
        { url: 'http://jf.test/MailConcierge/Cancel?t=abc', runScripts: 'outside-only', pretendToBeVisual: true });
    const w = dom.window;
    w.fetch = () => Promise.resolve({ ok: true, json: () => Promise.resolve({}) });
    w.eval(script);
    const d = w.document;
    d.getElementById('password').value = 'secret';
    d.getElementById('action-form').dispatchEvent(new w.Event('submit', { cancelable: true, bubbles: true }));
    await new Promise((r) => setTimeout(r, 100));
    assert.equal(d.getElementById('form-title').hidden, true);
    assert.equal(d.getElementById('done').hidden, false);
    assert.equal(d.title, 'Change cancelled - Pet Cinema');
    await new Promise((r) => setTimeout(r, 300));
    // Nothing redirected, and the legacy query was removed from the address.
    assert.equal(w.location.href, 'http://jf.test/MailConcierge/Cancel');
});

const entryHtml = readFileSync(here('../../Jellyfin.Plugin.MailConcierge/Resources/Pages/entry.html'), 'utf8')
    .replace(/\{\{s:([^}]+)\}\}/g, (_, key) => key).replaceAll('{{base}}', '/jf');

// Opens a page with the given script data and address; form submissions are recorded because jsdom does not navigate.
function openPage(html, extra, url) {
    const data = JSON.stringify({ lang: 'en', base: '/jf', minPasswordLength: 8, strings: {}, ...extra });
    const dom = new JSDOM(`<!DOCTYPE html><html><head><title>T</title></head><body>${html}<script type="application/json" id="mailconcierge-data">${data}</script></body></html>`,
        { url, runScripts: 'outside-only', pretendToBeVisual: true });
    const w = dom.window;
    const submitted = [];
    const fetched = [];
    w.HTMLFormElement.prototype.submit = function () {
        submitted.push({
            method: this.method, action: this.getAttribute('action'), hidden: this.hidden,
            fields: Object.fromEntries([...this.elements].map((e) => [e.name, e.value])),
            types: [...this.elements].map((e) => e.type)
        });
    };
    w.fetch = (u, init) => { fetched.push({ u, body: JSON.parse(init.body) }); return Promise.resolve({ ok: true, json: () => Promise.resolve({}) }); };
    w.eval(script);
    return { w, d: w.document, submitted, fetched };
}

test('entry page: the fragment is posted to Open/<target>, removed from the address, and nothing is put in the URL', () => {
    const { w, d, submitted } = openPage(entryHtml, { page: 'entry', target: 'reset' }, 'http://jf.test/jf/MailConcierge/Reset#t=tok_1-A&s=sess');
    assert.equal(submitted.length, 1);
    assert.equal(submitted[0].method, 'post');
    assert.equal(submitted[0].action, '/jf/MailConcierge/Open/reset');
    assert.deepEqual(submitted[0].fields, { t: 'tok_1-A', s: 'sess' });
    assert.deepEqual(submitted[0].types, ['hidden', 'hidden']);
    assert.equal(w.location.href, 'http://jf.test/jf/MailConcierge/Reset');
    assert.equal(d.getElementById('entry-invalid').hidden, true);
});

test('entry page: only the present field is sent', () => {
    const { submitted } = openPage(entryHtml, { page: 'entry', target: 'accept' }, 'http://jf.test/jf/MailConcierge/Accept#t=abc');
    assert.equal(submitted[0].action, '/jf/MailConcierge/Open/accept');
    assert.deepEqual(submitted[0].fields, { t: 'abc' });
});

test('entry page: without a fragment nothing is posted and the invalid block shows', () => {
    for (const url of ['http://jf.test/jf/MailConcierge/Cancel', 'http://jf.test/jf/MailConcierge/Cancel#', 'http://jf.test/jf/MailConcierge/Cancel#x=1']) {
        const { d, submitted } = openPage(entryHtml, { page: 'entry', target: 'cancel' }, url);
        assert.equal(submitted.length, 0);
        assert.equal(d.getElementById('entry-invalid').hidden, false);
        assert.equal(d.getElementById('entry-loading').hidden, true);
    }
});

test('entry page: the loading text is hidden in the markup and a noscript explains that JavaScript is needed', () => {
    const dom = new JSDOM(`<!DOCTYPE html><body>${entryHtml}</body>`);
    assert.equal(dom.window.document.getElementById('entry-loading').hidden, true);
    assert.ok(entryHtml.includes('<noscript><p>page.entry.noscript</p></noscript>'));
});

test('entry page: the loading text shows while the link is opened', () => {
    const { d } = openPage(entryHtml, { page: 'entry', target: 'reset' }, 'http://jf.test/jf/MailConcierge/Reset#t=abc');
    assert.equal(d.getElementById('entry-loading').hidden, false);
});

test('entry page: a duplicated field uses its first value', () => {
    const { submitted } = openPage(entryHtml, { page: 'entry', target: 'accept' }, 'http://jf.test/jf/MailConcierge/Accept#t=a&t=b');
    assert.equal(submitted.length, 1);
    assert.deepEqual(submitted[0].fields, { t: 'a' });
});

test('entry page: percent-encoded values are decoded, and the plus sign is a space', () => {
    const { submitted } = openPage(entryHtml, { page: 'entry', target: 'reset' }, 'http://jf.test/jf/MailConcierge/Reset#t=a%2Fb%3D&s=x%20y+z');
    assert.deepEqual(submitted[0].fields, { t: 'a/b=', s: 'x y z' });
});

test('entry page: an empty token posts nothing and shows the invalid block', () => {
    const { d, submitted } = openPage(entryHtml, { page: 'entry', target: 'confirm' }, 'http://jf.test/jf/MailConcierge/Confirm#t=');
    assert.equal(submitted.length, 0);
    assert.equal(d.getElementById('entry-invalid').hidden, false);
    assert.equal(d.getElementById('entry-loading').hidden, true);
});

test('entry page: a session alone is posted whatever the target, and the server ignores it where it does not apply', () => {
    const { submitted } = openPage(entryHtml, { page: 'entry', target: 'cancel' }, 'http://jf.test/jf/MailConcierge/Cancel#s=sess');
    assert.equal(submitted.length, 1);
    assert.equal(submitted[0].action, '/jf/MailConcierge/Open/cancel');
    assert.deepEqual(submitted[0].fields, { s: 'sess' });
});

test('entry page: the address is cleared before the form is submitted', () => {
    const data = JSON.stringify({ lang: 'en', base: '/jf', minPasswordLength: 8, strings: {}, page: 'entry', target: 'reset' });
    const dom = new JSDOM(`<!DOCTYPE html><html><head><title>T</title></head><body>${entryHtml}<script type="application/json" id="mailconcierge-data">${data}</script></body></html>`,
        { url: 'http://jf.test/jf/MailConcierge/Reset#t=tok', runScripts: 'outside-only', pretendToBeVisual: true });
    const w = dom.window;
    const seen = [];
    w.HTMLFormElement.prototype.submit = function () { seen.push({ href: w.location.href, attached: w.document.body.contains(this) }); };
    w.eval(script);
    assert.deepEqual(seen, [{ href: 'http://jf.test/jf/MailConcierge/Reset', attached: true }]);
});

test('entry page: an unknown target is never posted', () => {
    const { d, submitted } = openPage(entryHtml, { page: 'entry', target: '../x' }, 'http://jf.test/jf/MailConcierge/Reset#t=abc');
    assert.equal(submitted.length, 0);
    assert.equal(d.getElementById('entry-invalid').hidden, false);
});

test('the token of the page data has priority over the address, and the legacy query is stripped', async () => {
    const { w, d, fetched } = openPage(pageHtml, { page: 'cancel', token: 'from-data' }, 'http://jf.test/jf/MailConcierge/Cancel?t=from-query&s=zzz');
    assert.equal(w.location.href, 'http://jf.test/jf/MailConcierge/Cancel');
    d.getElementById('password').value = 'secret';
    d.getElementById('action-form').dispatchEvent(new w.Event('submit', { cancelable: true, bubbles: true }));
    await new Promise((r) => setTimeout(r, 50));
    assert.equal(fetched[0].body.token, 'from-data');
});

test('a legacy query still provides the token when the data has none', async () => {
    const { w, d, fetched } = openPage(pageHtml, { page: 'cancel' }, 'http://jf.test/jf/MailConcierge/Cancel?t=from-query');
    assert.equal(w.location.search, '');
    d.getElementById('password').value = 'secret';
    d.getElementById('action-form').dispatchEvent(new w.Event('submit', { cancelable: true, bubbles: true }));
    await new Promise((r) => setTimeout(r, 50));
    assert.equal(fetched[0].body.token, 'from-query');
});

test('the user name input of the invitation page is limited to 64 UTF-16 code units', () => {
    const accept = readFileSync(here('../../Jellyfin.Plugin.MailConcierge/Resources/Pages/accept.html'), 'utf8');
    const dom = new JSDOM(`<!DOCTYPE html><body>${accept}</body>`);
    assert.equal(dom.window.document.getElementById('username').getAttribute('maxlength'), '64');
});

// --- Invitation page: translated photo button with a round preview ---

const resources = (p) => here('../../Jellyfin.Plugin.MailConcierge/Resources/' + p);
const acceptHtml = readFileSync(resources('Pages/accept.html'), 'utf8');
const enStrings = JSON.parse(readFileSync(resources('Strings/en.json'), 'utf8'));
const avatarKeys = ['page.accept.avatarChoose', 'page.accept.avatarChange', 'page.accept.avatarRemove', 'page.accept.avatarError'];
const JPEG_ME = 'JPEGme';

// Each picture decodes to its own JPEG ('JPEG' + the file name without extension), after the delay set for its name.
function openAccept({ unreadable = [], delays = {}, holdAccept = false } = {}) {
    const html = acceptHtml
        .replace(/\{\{s:([^}]+)\}\}/g, (_, key) => enStrings[key] ?? key)
        .replaceAll('{{username}}', 'alice').replaceAll('{{email}}', 'alice@example.test').replaceAll('{{base}}', '');
    const data = JSON.stringify({ page: 'accept', lang: 'en', base: '', minPasswordLength: 8, strings: enStrings });
    const dom = new JSDOM(`<!DOCTYPE html><html><head><title>Accept - Pet Cinema</title></head><body>${html}<script type="application/json" id="mailconcierge-data">${data}</script></body></html>`,
        { url: 'http://jf.test/MailConcierge/Accept?t=abc', runScripts: 'outside-only', pretendToBeVisual: true });
    const w = dom.window;
    const posted = [];
    const counters = { reads: 0, decodes: 0 };
    let release;
    const gate = new Promise((r) => { release = r; });
    w.fetch = (url, init) => {
        posted.push({ url, body: JSON.parse(init.body) });
        const response = { ok: true, json: () => Promise.resolve({}) };
        return holdAccept && url.endsWith('/MailConcierge/Accept') ? gate.then(() => response) : Promise.resolve(response);
    };
    // jsdom has no image decoding or canvas: stub the three browser pieces readAvatar relies on.
    w.FileReader = class {
        readAsDataURL(file) {
            counters.reads++;
            const stem = file.name.replace(/\.[^.]+$/, '');
            setTimeout(() => {
                this.result = 'data:image/png;base64,' + stem;
                if (unreadable.includes(file.name)) { this.onerror(); } else { this.onload(); }
            }, delays[file.name] ?? 0);
        }
    };
    w.Image = class {
        set src(value) {
            counters.decodes++;
            this.tag = 'JPEG' + String(value).split(',')[1];
            this.naturalWidth = 1000;
            this.naturalHeight = 500;
            setTimeout(() => this.onload(), 0);
        }
    };
    w.HTMLCanvasElement.prototype.getContext = function () { return { fillRect() {}, drawImage: (image) => { this.drawn = image; } }; };
    w.HTMLCanvasElement.prototype.toDataURL = function () { return 'data:image/jpeg;base64,' + this.drawn.tag; };
    w.eval(script);
    const d = w.document;
    const input = d.getElementById('avatar');
    let chosen = [];
    Object.defineProperty(input, 'files', { get: () => chosen, configurable: true });
    Object.defineProperty(input, 'value', { get: () => (chosen.length ? 'C:\\fakepath\\' + chosen[0].name : ''), set: (v) => { if (v === '') { chosen = []; } }, configurable: true });
    const choose = (name = 'me.png') => {
        chosen = [new w.File(['x'], name, { type: 'image/png' })];
        input.dispatchEvent(new w.Event('change', { bubbles: true }));
    };
    // The picker was opened and cancelled: the browser keeps no file and still fires change.
    const cancelPicker = () => {
        chosen = [];
        input.dispatchEvent(new w.Event('change', { bubbles: true }));
    };
    const settle = (ms = 60) => new Promise((r) => setTimeout(r, ms));
    const fire = () => {
        d.getElementById('password').value = 'secret-password';
        d.getElementById('confirm').value = 'secret-password';
        d.getElementById('accept-form').dispatchEvent(new w.Event('submit', { cancelable: true, bubbles: true }));
    };
    const accepted = () => posted.filter((p) => p.url.endsWith('/MailConcierge/Accept')).at(-1);
    const submit = async () => {
        fire();
        await settle(250);
        return accepted();
    };
    return { w, d, input, choose, cancelPicker, settle, fire, accepted, submit, release, counters, posted };
}

test('the invitation page has a labelled photo button, and the native input stays in the form and reachable by keyboard', () => {
    const dom = new JSDOM(`<!DOCTYPE html><body>${acceptHtml}</body>`);
    const d = dom.window.document;
    const input = d.getElementById('avatar');
    assert.equal(input.closest('form').id, 'accept-form');
    assert.equal(input.type, 'file');
    assert.ok(d.querySelector('label#avatar-choose[for="avatar"]'));
    assert.ok(input.classList.contains('visually-hidden'));
    assert.notEqual(input.getAttribute('hidden'), '');
    assert.notEqual(input.getAttribute('tabindex'), '-1');
    assert.equal(d.getElementById('avatar-preview').hidden, true);
    assert.equal(d.getElementById('avatar-remove').hidden, true);
    assert.equal(d.getElementById('avatar-remove').type, 'button');
});

// The declarations of the rule whose selector list contains exactly this selector (none when there is no such rule).
function cssRule(css, selector) {
    for (const match of css.matchAll(/([^{}]+)\{([^}]*)\}/g)) {
        if (match[1].split(',').map((x) => x.trim()).includes(selector)) { return match[2]; }
    }

    return null;
}

test('the visually hidden input is not removed from the focus order by the stylesheet', () => {
    const css = readFileSync(resources('Pages/pages.css'), 'utf8');
    const rule = cssRule(css, '.visually-hidden');
    assert.ok(rule, 'a .visually-hidden rule exists');
    assert.doesNotMatch(rule, /display\s*:\s*none|visibility\s*:\s*hidden/);
});

test('the photo button and the Remove button are at least 44 px tall and the focused field shows on the button', () => {
    const css = readFileSync(resources('Pages/pages.css'), 'utf8');
    for (const selector of ['.avatar-choose', '.avatar-remove']) {
        const rule = cssRule(css, selector);
        assert.ok(rule, `${selector} has a rule`);
        const height = rule.match(/min-height\s*:\s*([\d.]+)rem/);
        assert.ok(height && Number(height[1]) * 16 >= 44, `${selector} min-height is at least 44 px`);
    }

    const focus = cssRule(css, '.visually-hidden:focus-visible + .avatar-row .avatar-choose');
    assert.ok(focus, 'keyboard focus on the hidden input is shown on the button');
    assert.match(focus, /border-color|box-shadow|outline/);
});

test('the photo button strings exist and are filled in the four languages', () => {
    for (const lang of ['fr', 'en', 'de', 'es']) {
        const strings = JSON.parse(readFileSync(resources(`Strings/${lang}.json`), 'utf8'));
        for (const key of avatarKeys) {
            assert.ok(typeof strings[key] === 'string' && strings[key].trim().length > 0, `${lang} ${key}`);
        }
    }
});

test('choosing a photo shows a round data:image/jpeg preview and the button says change', async () => {
    const t = openAccept();
    t.choose();
    await t.settle();
    const preview = t.d.getElementById('avatar-preview');
    assert.equal(preview.hidden, false);
    assert.equal(preview.src, 'data:image/jpeg;base64,' + JPEG_ME);
    assert.equal(t.d.getElementById('avatar-choose').textContent, enStrings['page.accept.avatarChange']);
    assert.equal(t.d.getElementById('avatar-remove').hidden, false);
    assert.equal(t.d.getElementById('avatar-remove').textContent, enStrings['page.accept.avatarRemove']);
});

test('the sent photo is the same resized JPEG as the preview, read and resized only once', async () => {
    const t = openAccept();
    t.choose();
    await t.settle();
    const sent = await t.submit();
    assert.equal(sent.body.avatar, JPEG_ME);
    assert.equal(t.counters.reads, 1);
    assert.equal(t.counters.decodes, 1);
});

test('Remove empties the field, hides the preview and the account is created without a photo', async () => {
    const t = openAccept();
    t.choose();
    await t.settle();
    t.d.getElementById('avatar-remove').dispatchEvent(new t.w.Event('click', { bubbles: true }));
    assert.equal(t.input.value, '');
    assert.equal(t.d.getElementById('avatar-preview').hidden, true);
    assert.equal(t.d.getElementById('avatar-preview').getAttribute('src'), null);
    assert.equal(t.d.getElementById('avatar-remove').hidden, true);
    assert.equal(t.d.getElementById('avatar-choose').textContent, enStrings['page.accept.avatarChoose']);
    const sent = await t.submit();
    assert.equal('avatar' in sent.body, false);
});

test('an unreadable picture shows no preview, a translated message, and the account is still created without it', async () => {
    const t = openAccept({ unreadable: ['broken.png'] });
    t.choose('broken.png');
    await t.settle();
    assert.equal(t.d.getElementById('avatar-preview').hidden, true);
    assert.equal(t.d.getElementById('avatar-remove').hidden, true);
    const error = t.d.getElementById('avatar-error');
    assert.equal(error.hidden, false);
    assert.equal(error.textContent, enStrings['page.accept.avatarError']);
    assert.equal(t.d.getElementById('avatar-choose').textContent, enStrings['page.accept.avatarChoose']);
    const sent = await t.submit();
    assert.ok(sent, 'the form was submitted');
    assert.equal('avatar' in sent.body, false);
    assert.equal(t.d.getElementById('done').hidden, false);
});

test('Remove clears the photo error', async () => {
    const t = openAccept({ unreadable: ['broken.png'] });
    t.choose('broken.png');
    await t.settle();
    assert.equal(t.d.getElementById('avatar-error').hidden, false);
    t.d.getElementById('avatar-remove').click();
    assert.equal(t.d.getElementById('avatar-error').hidden, true);
});

test('submitting without any photo works as before', async () => {
    const t = openAccept();
    const sent = await t.submit();
    assert.equal('avatar' in sent.body, false);
    assert.equal(sent.body.username, 'alice');
});

test('after Remove the keyboard focus goes back to the photo field instead of the page body', async () => {
    const t = openAccept();
    t.choose();
    await t.settle();
    t.d.getElementById('avatar-remove').focus();
    t.d.getElementById('avatar-remove').click();
    assert.equal(t.d.activeElement, t.input);
});

test('choosing B after A hides the preview of A at once and then shows and sends B', async () => {
    const t = openAccept();
    t.choose('a.png');
    await t.settle();
    assert.equal(t.d.getElementById('avatar-preview').src, 'data:image/jpeg;base64,JPEGa');
    t.choose('b.png');
    assert.equal(t.d.getElementById('avatar-preview').hidden, true);
    assert.equal(t.d.getElementById('avatar-remove').hidden, true);
    await t.settle();
    assert.equal(t.d.getElementById('avatar-preview').src, 'data:image/jpeg;base64,JPEGb');
    const sent = await t.submit();
    assert.equal(sent.body.avatar, 'JPEGb');
});

test('a slow read of A that ends after B was chosen does not overwrite the preview or the sent photo', async () => {
    const t = openAccept({ delays: { 'a.png': 120, 'b.png': 5 } });
    t.choose('a.png');
    t.choose('b.png');
    await t.settle(60);
    assert.equal(t.d.getElementById('avatar-preview').src, 'data:image/jpeg;base64,JPEGb');
    await t.settle(200);
    assert.equal(t.d.getElementById('avatar-preview').src, 'data:image/jpeg;base64,JPEGb');
    assert.equal(t.d.getElementById('avatar-remove').hidden, false);
    const sent = await t.submit();
    assert.equal(sent.body.avatar, 'JPEGb');
});

test('Remove during a slow read leaves no preview and no photo, even when the read ends later', async () => {
    const t = openAccept({ delays: { 'a.png': 100 } });
    t.choose('a.png');
    t.d.getElementById('avatar-remove').dispatchEvent(new t.w.Event('click', { bubbles: true }));
    await t.settle(200);
    assert.equal(t.d.getElementById('avatar-preview').hidden, true);
    assert.equal(t.d.getElementById('avatar-remove').hidden, true);
    assert.equal(t.input.value, '');
    const sent = await t.submit();
    assert.equal('avatar' in sent.body, false);
});

test('cancelling the file picker leaves a clean state without photo', async () => {
    const t = openAccept();
    t.choose('a.png');
    await t.settle();
    t.cancelPicker();
    assert.equal(t.d.getElementById('avatar-preview').hidden, true);
    assert.equal(t.d.getElementById('avatar-remove').hidden, true);
    assert.equal(t.d.getElementById('avatar-error').hidden, true);
    assert.equal(t.d.getElementById('avatar-choose').textContent, enStrings['page.accept.avatarChoose']);
    await t.settle(100);
    assert.equal(t.d.getElementById('avatar-preview').hidden, true);
    const sent = await t.submit();
    assert.equal('avatar' in sent.body, false);
});

test('submitting during the read waits for it and sends the photo that ends up displayed', async () => {
    const t = openAccept({ delays: { 'a.png': 80 } });
    t.choose('a.png');
    const sent = await t.submit();
    assert.equal(sent.body.avatar, 'JPEGa');
    assert.equal(t.d.getElementById('avatar-preview').src, 'data:image/jpeg;base64,JPEGa');
    assert.equal(t.counters.reads, 1);
    assert.equal(t.counters.decodes, 1);
});

test('while the account is being created the photo can be neither removed nor replaced', async () => {
    const t = openAccept({ holdAccept: true });
    t.choose('a.png');
    await t.settle();
    t.fire();
    await t.settle();
    assert.equal(t.d.getElementById('avatar-remove').disabled, true);
    assert.equal(t.input.disabled, true);
    t.d.getElementById('avatar-remove').click();
    assert.equal(t.accepted().body.avatar, 'JPEGa');
    t.release();
    await t.settle();
    assert.equal(t.d.getElementById('done').hidden, false);
});

test('the photo can be removed again after a failed creation', async () => {
    const t = openAccept();
    t.w.fetch = () => Promise.resolve({ ok: false, json: () => Promise.resolve({ message: 'No.' }) });
    t.choose('a.png');
    await t.settle();
    t.fire();
    await t.settle();
    assert.equal(t.d.getElementById('avatar-remove').disabled, false);
    assert.equal(t.input.disabled, false);
});

// ---- Final fix wave: success focus, username rules, contrast and keyboard focus.

test('after a success the focus moves to the success title (not lost on the hidden button)', async () => {
    const data = JSON.stringify({ page: 'cancel', lang: 'en', base: '', minPasswordLength: 8, strings: {} });
    const dom = new JSDOM(`<!DOCTYPE html><html><head><title>Cancel - Pet Cinema</title></head><body>${pageHtml}<script type="application/json" id="mailconcierge-data">${data}</script></body></html>`,
        { url: 'http://jf.test/MailConcierge/Cancel', runScripts: 'outside-only', pretendToBeVisual: true });
    const w = dom.window;
    w.fetch = () => Promise.resolve({ ok: true, json: () => Promise.resolve({}) });
    w.eval(script);
    const d = w.document;
    d.getElementById('password').value = 'secret';
    d.getElementById('submit').focus();
    d.getElementById('action-form').dispatchEvent(new w.Event('submit', { cancelable: true, bubbles: true }));
    await new Promise((r) => setTimeout(r, 100));
    const title = d.querySelector('#done h1');
    assert.equal(title.getAttribute('tabindex'), '-1');
    assert.equal(d.activeElement, title);
});

test('the account page states the user name rules, and the availability hint is announced and described by the field', () => {
    const accept = readFileSync(here('../../Jellyfin.Plugin.MailConcierge/Resources/Pages/accept.html'), 'utf8');
    assert.match(accept, /<p id="username-rules" class="hint">\{\{s:page\.accept\.usernameRules\}\}<\/p>/);
    assert.match(accept, /<p id="username-hint" class="hint" role="status" aria-live="polite" hidden>/);
    assert.match(accept, /id="username"[^>]*aria-describedby="username-rules username-hint"/);
});

function luminance(hex) {
    const c = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255).map((v) => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4));
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
}
const contrast = (a, b) => { const [x, y] = [luminance(a), luminance(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };

test('the primary button has at least 4.5:1 contrast (also hovered) and every button and link has a visible keyboard focus outline', () => {
    const css = readFileSync(resources('Pages/pages.css'), 'utf8');
    const rule = cssRule(css, '.button');
    const bg = rule.match(/background\s*:\s*(#[0-9a-f]{6})/i)[1];
    const fg = rule.match(/(?:^|[;\s])color\s*:\s*(#[0-9a-f]{6})/i)[1];
    assert.ok(contrast(fg, bg) >= 4.5, `button contrast ${contrast(fg, bg)}`);
    const hover = css.match(/\.button:hover[^{]*\{[^}]*background\s*:\s*(#[0-9a-f]{6})/i)[1];
    assert.ok(contrast(fg, hover) >= 4.5, `hover contrast ${contrast(fg, hover)}`);
    const focus = cssRule(css, '.button:focus-visible');
    assert.ok(focus, 'a :focus-visible rule covers the buttons and the links');
    assert.match(focus, /outline\s*:\s*3px solid/);
});

test('a page served by Open/<page> shows its plain address and keeps no token', () => {
    const { w } = openPage(pageHtml, { page: 'cancel', token: 'abc', strings: {} }, 'http://jf.test/jf/MailConcierge/Open/cancel');
    assert.equal(w.location.href, 'http://jf.test/jf/MailConcierge/Cancel');
});
