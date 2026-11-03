// Tests of the Mail Concierge settings page (Configuration/configPage.html) in jsdom, with a fake ApiClient.
// Run from this folder: npm ci && node --test --test-timeout=30000
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { JSDOM, VirtualConsole } from 'jsdom';

const here = (p) => fileURLToPath(new URL(p, import.meta.url));
const html = readFileSync(here('../../Jellyfin.Plugin.MailConcierge/Configuration/configPage.html'), 'utf8');
const wait = (ms = 60) => new Promise((resolve) => setTimeout(resolve, ms));

const CONFIG = {
    enabled: true, nativeForgotPassword: true, publicUrl: 'https://jf.example', defaultLanguage: 'fr',
    languages: [{ code: 'en', name: 'English' }, { code: 'fr', name: 'Français' }],
    smtpHost: 'smtp.example', smtpPort: 587, smtpSecurity: 'StartTls', smtpUsername: '', smtpPassword: '', smtpPasswordSet: true,
    fromAddress: 'a@example.org', fromName: 'Mail Concierge', pinMinutes: 15, invitationDays: 9, confirmationHours: 24, cancelEmailChangeHours: 24,
    maxRecoveryEmailsPerAccountPerHour: 3, maxForgotRequestsPerIpPer15Min: 10, maxRecoveryEmailsPerHour: 10, maxInvitationEmailsPerHour: 10, maxAccountEmailsPerHour: 10, maxPinFailures: 5,
    allowAdministrators: true, sessionsOnReset: 'All', sessionInactiveDays: 30, seerrEnabled: true, seerrUrl: '', seerrApiKey: '', seerrApiKeySet: false,
    templateOverrides: {}
};

async function open(url, { session = {}, created = {} } = {}) {
    const errors = [];
    const virtualConsole = new VirtualConsole();
    virtualConsole.on('jsdomError', (e) => errors.push(e.message));
    const dom = new JSDOM(html, { url, runScripts: 'dangerously', pretendToBeVisual: true, virtualConsole, beforeParse(w) {
        for (const [k, v] of Object.entries(session)) { w.sessionStorage.setItem(k, v); }
        w.__calls = [];
        w.Dashboard = { showLoadingMsg() {}, hideLoadingMsg() {}, alert() {} };
        w.ApiClient = {
            getUrl: (p) => p,
            getCurrentUserId: () => 'u1',
            ajax(req) {
                const body = req.data ? JSON.parse(req.data) : undefined;
                w.__calls.push({ method: req.type, url: req.url, body });
                const path = req.url.split('?')[0];
                if (path === 'MailConcierge/Admin/Config' && req.type === 'GET') { return Promise.resolve(CONFIG); }
                if (path === 'MailConcierge/Admin/Config') { return Promise.resolve({ ok: true }); }
                if (path === 'MailConcierge/Admin/Strings') { return Promise.resolve({ language: 'en', strings: { title: 'T', save: 'Save', tabInvitations: 'Invitations', username: 'User', send: 'Send', copyLink: 'Copy link', 'inv.sentTo': 'SENT {0}', 'inv.notSent': 'NOT SENT', 'inv.notSentLimit': 'LIMIT REACHED' } }); }
                if (path === 'MailConcierge/Admin/Templates') { return Promise.resolve({}); }
                if (path === 'MailConcierge/Admin/Status') { return Promise.resolve({ proxyGuardTripped: false, canSendEmail: true }); }
                if (path === 'MailConcierge/Libraries') { return Promise.resolve([{ id: 'l1', name: 'Films' }, { id: 'l2', name: 'Series' }]); }
                if (path === 'MailConcierge/Invitations' && req.type === 'POST') { return Promise.resolve(created); }
                if (path === 'MailConcierge/Invitations') { return Promise.resolve([]); }
                return Promise.resolve({});
            }
        };
    } });
    const w = dom.window;
    w.document.querySelector('#MailConciergeConfigPage').dispatchEvent(new w.Event('pageshow'));
    await wait();
    return { w, d: w.document, errors };
}

const visibleTab = (d) => d.querySelector('.ucTab:not([hidden])').id;
const submit = (w, d, tab) => d.querySelector(`form[data-tab-form="${tab}"]`).dispatchEvent(new w.Event('submit', { cancelable: true, bubbles: true }));
const posts = (w) => w.__calls.filter((c) => c.method === 'POST' && c.url === 'MailConcierge/Admin/Config');

test('the Invitations tab comes after Accounts, and the old separate page link is gone', async () => {
    const { d, errors } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge');
    const order = [...d.querySelectorAll('.ucTabBtn')].map((b) => b.getAttribute('data-tab'));
    assert.deepEqual(order, ['general', 'smtp', 'templates', 'seerr', 'accounts', 'invitations']);
    assert.equal(d.querySelector('a[href*="MailConciergeInvitations"]'), null);
    assert.equal(d.querySelector('#ucTabBtn-invitations').textContent, 'Invitations');
    assert.deepEqual(errors, []);
});

test('selected tab comes from sessionStorage (mailconcierge.cfgTab); else General; the URL is ignored', async () => {
    assert.equal(visibleTab((await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { session: { 'mailconcierge.cfgTab': 'invitations' } })).d), 'ucTab-invitations');
    assert.equal(visibleTab((await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { session: { 'mailconcierge.cfgTab': 'smtp' } })).d), 'ucTab-smtp');
    assert.equal(visibleTab((await open('http://jf.test/web/#/configurationpage?name=MailConcierge')).d), 'ucTab-general');
    assert.equal(visibleTab((await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { session: { 'mailconcierge.cfgTab': 'bogus"x' } })).d), 'ucTab-general');
});

test('Invitations tab: libraries with select-all, validity prefilled from the config, language list', async () => {
    const { w, d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { session: { 'mailconcierge.cfgTab': 'invitations' } });
    assert.equal(d.querySelector('#ucInvDays').value, '9');
    assert.deepEqual([...d.querySelectorAll('#ucInvLanguage option')].map((o) => o.value), ['en', 'fr']);
    assert.equal(d.querySelector('#ucInvLanguage').value, 'en');
    assert.equal(d.querySelectorAll('#ucInvLibraries input').length, 2);
    const all = d.querySelector('#ucInvSelectAll');
    all.checked = true;
    all.dispatchEvent(new w.Event('change'));
    assert.ok([...d.querySelectorAll('#ucInvLibraries input')].every((b) => b.checked));
    assert.equal(d.querySelector('#ucInvSubmit').textContent.trim(), 'Copy link');
    d.querySelector('#ucInvEmail').value = 'a@b.c';
    d.querySelector('#ucInvEmail').dispatchEvent(new w.Event('input'));
    assert.equal(d.querySelector('#ucInvSubmit').textContent.trim(), 'Send');
});

test('Invitations tab: creating posts to MailConcierge/Invitations and never touches the settings', async () => {
    const { w, d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { session: { 'mailconcierge.cfgTab': 'invitations' } });
    d.querySelector('#ucInvUsername').value = 'alice';
    d.querySelector('#ucInvLibraries input').checked = true;
    d.querySelector('#ucInvForm').dispatchEvent(new w.Event('submit', { cancelable: true, bubbles: true }));
    await wait();
    const created = w.__calls.find((c) => c.method === 'POST' && c.url === 'MailConcierge/Invitations');
    assert.equal(created.body.proposedUsername, 'alice');
    assert.equal(created.body.days, 9);
    assert.equal(created.body.language, 'en');
    assert.equal(posts(w).length, 0);
});

for (const [name, answer, expectedPosts] of [['declined', false, 0], ['accepted', true, 1]]) {
    test(`Invitations tab: no library ticked asks for confirmation first (${name})`, async () => {
        const { w, d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { session: { 'mailconcierge.cfgTab': 'invitations' } });
        let asked = 0;
        w.confirm = () => { asked++; return answer; };
        d.querySelector('#ucInvUsername').value = 'alice';
        d.querySelector('#ucInvForm').dispatchEvent(new w.Event('submit', { cancelable: true, bubbles: true }));
        await wait();
        assert.equal(asked, 1);
        assert.equal(w.__calls.filter((c) => c.method === 'POST' && c.url === 'MailConcierge/Invitations').length, expectedPosts);
    });
}

test('Invitations tab: a ticked library is sent without any confirmation', async () => {
    const { w, d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { session: { 'mailconcierge.cfgTab': 'invitations' } });
    w.confirm = () => { throw new Error('must not ask'); };
    d.querySelector('#ucInvUsername').value = 'alice';
    d.querySelector('#ucInvLibraries input').checked = true;
    d.querySelector('#ucInvForm').dispatchEvent(new w.Event('submit', { cancelable: true, bubbles: true }));
    await wait();
    const created = w.__calls.find((c) => c.method === 'POST' && c.url === 'MailConcierge/Invitations');
    assert.deepEqual(created.body.libraryIds, ['l1']);
});

for (const [name, response, expected] of [
    ['queued', { link: 'L', emailed: true, emailStatus: 'queued' }, 'SENT a@b.c'],
    ['limit reached', { link: 'L', emailed: false, emailStatus: 'limit_reached' }, 'LIMIT REACHED'],
    ['not configured', { link: 'L', emailed: false, emailStatus: 'not_configured' }, 'NOT SENT'],
]) {
    test(`Invitations tab: the admin sees the real outcome of the e-mail (${name})`, async () => {
        const { w, d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { session: { 'mailconcierge.cfgTab': 'invitations' }, created: response });
        d.querySelector('#ucInvUsername').value = 'alice';
        d.querySelector('#ucInvEmail').value = 'a@b.c';
        d.querySelector('#ucInvLibraries input').checked = true;
        d.querySelector('#ucInvForm').dispatchEvent(new w.Event('submit', { cancelable: true, bubbles: true }));
        await wait();
        assert.equal(d.querySelector('#ucInvResultText').textContent, expected);
    });
}

test('saving another tab never posts the General checkboxes, and the loaded checkboxes are ticked', async () => {
    const { w, d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge');
    assert.equal(d.querySelector('[data-f="enabled"]').checked, true);
    assert.equal(d.querySelector('[data-f="nativeForgotPassword"]').checked, true);
    assert.equal(d.querySelector('[data-f="allowAdministrators"]').checked, true);

    submit(w, d, 'smtp');
    await wait();
    submit(w, d, 'seerr');
    await wait();

    const [smtp, seerr] = posts(w).map((c) => Object.keys(c.body));
    for (const keys of [smtp, seerr]) {
        for (const forbidden of ['enabled', 'nativeForgotPassword', 'allowAdministrators']) {
            assert.ok(!keys.includes(forbidden), `${forbidden} must not be posted by another tab`);
        }
    }
    assert.ok(smtp.includes('smtpHost'));
    assert.ok(seerr.includes('seerrEnabled'));
    // After the re-load of the saved tab, the General checkboxes keep the loaded value.
    assert.equal(d.querySelector('[data-f="enabled"]').checked, true);
});

test('saving General posts the checkboxes as loaded', async () => {
    const { w, d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge');
    submit(w, d, 'general');
    await wait();
    const body = posts(w)[0].body;
    assert.equal(body.enabled, true);
    assert.equal(body.nativeForgotPassword, true);
    assert.equal(body.allowAdministrators, true);
});

test('warnings: http:// URLs and SMTP security None show a non-blocking notice, https and STARTTLS do not', async () => {
    const { w, d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge');
    const hidden = (id) => d.querySelector(id).hidden;
    // Loaded config: https public URL, StartTls, empty Seerr URL.
    assert.equal(hidden('#ucWarnPublicUrl'), true);
    assert.equal(hidden('#ucWarnSmtp'), true);
    assert.equal(hidden('#ucWarnSeerr'), true);

    const type = (selector, value) => {
        const field = d.querySelector(selector);
        field.value = value;
        field.dispatchEvent(new w.Event('input', { bubbles: true }));
        field.dispatchEvent(new w.Event('change', { bubbles: true }));
    };
    type('[data-f="publicUrl"]', 'http://jf.lan');
    type('[data-f="seerrUrl"]', ' HTTP://seerr.lan:5055');
    type('[data-f="smtpSecurity"]', 'None');
    assert.equal(hidden('#ucWarnPublicUrl'), false);
    assert.equal(hidden('#ucWarnSeerr'), false);
    assert.equal(hidden('#ucWarnSmtp'), false);

    type('[data-f="publicUrl"]', 'https://jf.example');
    type('[data-f="seerrUrl"]', 'https://seerr.example');
    type('[data-f="smtpSecurity"]', 'StartTls');
    assert.equal(hidden('#ucWarnPublicUrl'), true);
    assert.equal(hidden('#ucWarnSeerr'), true);
    assert.equal(hidden('#ucWarnSmtp'), true);
});

test('Invitations tab: the one-time link is hidden when another tab is selected', async () => {
    const { w, d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { session: { 'mailconcierge.cfgTab': 'invitations' }, created: { link: 'L', emailed: false, emailStatus: 'not_configured' } });
    w.confirm = () => true;
    d.querySelector('#ucInvUsername').value = 'alice';
    d.querySelector('#ucInvForm').dispatchEvent(new w.Event('submit', { cancelable: true, bubbles: true }));
    await wait();
    assert.equal(d.querySelector('#ucInvResult').hidden, false);
    d.querySelector('#ucTabBtn-general').dispatchEvent(new w.Event('click', { bubbles: true }));
    await wait();
    assert.equal(d.querySelector('#ucInvResult').hidden, true);
});
