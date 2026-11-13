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
    allowAdministrators: true, emailLogin: true, sessionsOnReset: 'All', sessionInactiveDays: 30, seerrEnabled: true, seerrUrl: '', seerrApiKey: '', seerrApiKeySet: false,
    templateOverrides: {}
};

async function open(url, { session = {}, created = {}, createError = null, strings = {}, invitations = [], regenerated = {}, eraseError = null, assign = { assigned: 0, skippedAdmins: 0, skippedExternal: 0 }, assignRejection = null, config = {}, status = {}, preview = [], importResult = { imported: 0, changed: 0 }, focus = null } = {}) {
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
                w.__calls.push({ method: req.type, url: req.url, body, data: req.data, contentType: req.contentType });
                const path = req.url.split('?')[0];
                if (path === 'MailConcierge/Admin/AssignProvider') { return assignRejection ? Promise.reject(assignRejection) : Promise.resolve(assign); }
                if (path === 'MailConcierge/Admin/Config' && req.type === 'GET') { return Promise.resolve({ ...CONFIG, ...config }); }
                if (path === 'MailConcierge/Admin/Config') { return Promise.resolve({ ok: true }); }
                if (path === 'MailConcierge/Admin/Strings') { return Promise.resolve({ language: 'en', strings: { title: 'T', save: 'Save', tabInvitations: 'Invitations', username: 'User', send: 'Send', copyLink: 'Copy link', 'inv.sentTo': 'SENT {0}', emailLoginUnavailable: 'NOT AVAILABLE', emailLoginShared: '{0} SHARED', 'inv.notSent': 'NOT SENT', 'inv.notSentLimit': 'LIMIT REACHED', 'inv.notSentQueueFull': 'QUEUE FULL', 'inv.newLink': 'NEW LINK', 'inv.newLinkSent': 'NEW LINK SENT', 'inv.newLinkNotSentLimit': 'NEW LINK LIMIT REACHED', 'inv.regenerate': 'Regenerate', ...strings } }); }
                if (path === 'MailConcierge/Seerr/ImportPreview') { return Promise.resolve(preview); }
                if (path === 'MailConcierge/Seerr/Import') { return Promise.resolve(importResult); }
                if (path === 'MailConcierge/Admin/Templates') { return Promise.resolve({}); }
                if (path === 'MailConcierge/Admin/Status') { return Promise.resolve({ proxyGuardTripped: false, canSendEmail: true, emailLoginAvailable: true, sharedEmailCount: 0, ...status }); }
                if (path === 'MailConcierge/Libraries') { return Promise.resolve([{ id: 'l1', name: 'Films' }, { id: 'l2', name: 'Series' }]); }
                if (path === 'MailConcierge/Invitations' && req.type === 'POST') { return createError ? Promise.reject(createError) : Promise.resolve(created); }
                if (path === 'MailConcierge/Invitations') { return Promise.resolve(invitations); }
                if (path.startsWith('MailConcierge/Invitations/') && req.type === 'DELETE' && eraseError) { return Promise.reject(eraseError); }
                if (path.endsWith('/Regenerate') && req.type === 'POST') { return Promise.resolve(regenerated); }
                return Promise.resolve({});
            }
        };
    } });
    const w = dom.window;
    // Jellyfin's view auto-focus: the first focusable element has the focus before pageshow runs.
    if (focus) { w.document.querySelector(focus).focus(); }
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
    ['queue full', { link: 'L', emailed: false, emailStatus: 'queue_full' }, 'QUEUE FULL'],
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

for (const [name, response, expected] of [
    ['e-mailed', { link: 'L', emailed: true, emailStatus: 'queued' }, 'NEW LINK SENT'],
    ['limit reached', { link: 'L', emailed: false, emailStatus: 'limit_reached' }, 'NEW LINK LIMIT REACHED'],
    ['queue full', { link: 'L', emailed: false, emailStatus: 'queue_full' }, 'QUEUE FULL'],
    ['no address', { link: 'L', emailed: false, emailStatus: 'none' }, 'NEW LINK'],
]) {
    test(`Invitations tab: a regeneration tells whether the new link was e-mailed (${name})`, async () => {
        const invitations = [{ id: 'i1', proposedUsername: 'alice', email: 'a@b.c', state: 'Pending', expiresAt: '2026-11-20T00:00:00Z' }];
        const { w, d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { session: { 'mailconcierge.cfgTab': 'invitations' }, invitations, regenerated: response });
        const button = [...d.querySelectorAll('#ucInvList button')].find((b) => b.textContent === 'Regenerate');
        assert.ok(button);
        button.click();
        await wait();
        assert.equal(d.querySelector('#ucInvResultText').textContent, expected);
    });
}

const ASSIGN_STRINGS = { assigned: '{0} ASSIGNED, {1} ADMINS, {2} EXTERNAL', errGeneric: 'WENT WRONG' };
const assignCalls = (w) => w.__calls.filter((c) => c.url === 'MailConcierge/Admin/AssignProvider');
const openAccounts = (extra = {}) => open('http://jf.test/web/#/configurationpage?name=MailConcierge', { session: { 'mailconcierge.cfgTab': 'accounts' }, strings: ASSIGN_STRINGS, ...extra });

test('Assign button: one click sends exactly one POST MailConcierge/Admin/AssignProvider, with no body and no content type', async () => {
    const { w, d, errors } = await openAccounts();
    assert.equal(assignCalls(w).length, 0);

    d.querySelector('#ucAssign').click();
    await wait();

    const [call] = assignCalls(w);
    assert.equal(assignCalls(w).length, 1);
    assert.equal(call.method, 'POST');
    assert.equal(call.data, undefined);
    assert.equal(call.body, undefined);
    assert.equal(call.contentType, undefined);
    assert.deepEqual(errors, []);
});

test('Assign button: a 200 answer shows the three numbers in the result line, not as an error', async () => {
    const { w, d } = await openAccounts({ assign: { assigned: 4, skippedAdmins: 1, skippedExternal: 2 } });

    d.querySelector('#ucAssign').click();
    await wait();

    const result = d.querySelector('#ucAssignResult');
    assert.equal(result.hidden, false);
    assert.equal(result.textContent, '4 ASSIGNED, 1 ADMINS, 2 EXTERNAL');
    assert.notEqual(result.style.color, 'rgb(255, 138, 128)');
    assert.equal(assignCalls(w).length, 1);
});

for (const [name, rejection] of [
    ['a 500 answer with a JSON body', { status: 500, json: () => Promise.resolve({ code: 'whatever' }) }],
    ['a 500 answer with a body that is not JSON', { status: 500, json: () => Promise.reject(new SyntaxError('Unexpected token')) }],
    ['a 500 answer that cannot be read as a response', { status: 500 }],
    ['a rejected network call', new TypeError('Failed to fetch')],
]) {
    test(`Assign button: ${name} shows the generic error in the result line`, async () => {
        const { w, d } = await openAccounts({ assignRejection: rejection });

        d.querySelector('#ucAssign').click();
        await wait();

        const result = d.querySelector('#ucAssignResult');
        assert.equal(result.hidden, false);
        assert.equal(result.textContent, 'WENT WRONG');
        assert.equal(result.style.color, 'rgb(255, 138, 128)');
        assert.equal(assignCalls(w).length, 1);
    });
}

const STATES = ['Pending', 'Accepted', 'Revoked', 'Expired'];
const HISTORY = STATES.map((state, i) => ({ id: 'i' + (i + 1), proposedUsername: 'user' + (i + 1), email: null, state, expiresAt: '2026-11-20T00:00:00Z' }));
const ERASE_STRINGS = { 'inv.revoke': 'Revoke', 'inv.erase': 'ERASE', 'inv.confirmErasePending': 'ERASE PENDING?', 'inv.list': 'HISTORY' };
const openHistory = (extra = {}) => open('http://jf.test/web/#/configurationpage?name=MailConcierge', { session: { 'mailconcierge.cfgTab': 'invitations' }, invitations: HISTORY, strings: ERASE_STRINGS, ...extra });
const eraseButtonOf = (d, id) => {
    const rows = [...d.querySelectorAll('#ucInvList > div')];
    return rows[+id.slice(1) - 1].querySelector('button[is="paper-icon-button-light"]');
};
const invDeletes = (w) => w.__calls.filter((c) => c.method === 'DELETE');

test('Invitations tab: every line, whatever its state, ends with an erase icon button; the heading is the history', async () => {
    const { d } = await openHistory();
    assert.equal(d.querySelector('[data-t="inv.list"]').textContent, 'HISTORY');
    const rows = [...d.querySelectorAll('#ucInvList > div')];
    assert.equal(rows.length, 4);
    for (const row of rows) {
        const buttons = [...row.querySelectorAll('button[is="paper-icon-button-light"]')];
        assert.equal(buttons.length, 1);
        assert.equal(buttons[0].title, 'ERASE');
        assert.equal(buttons[0].getAttribute('aria-label'), 'ERASE');
        assert.equal(buttons[0].classList.contains('paper-icon-button-light'), true);
        assert.equal(row.lastElementChild, buttons[0]);
        const icon = buttons[0].querySelector('span.material-icons.delete');
        assert.ok(icon);
        assert.equal(icon.getAttribute('aria-hidden'), 'true');
    }

    // The regenerate and revoke buttons stay on the lines that had them.
    const labels = rows.map((r) => [...r.querySelectorAll('button:not([is="paper-icon-button-light"])')].map((b) => b.textContent));
    assert.deepEqual(labels, [['Regenerate', 'Revoke'], [], [], ['Regenerate', 'Revoke']]);
});

test('Invitations tab: the history heading and the erase texts are the fixed English and French copy', () => {
    const read = (l) => JSON.parse(readFileSync(here(`../../Jellyfin.Plugin.MailConcierge/Resources/Strings/${l}.json`), 'utf8'));
    const en = read('en');
    const fr = read('fr');
    assert.deepEqual([en['inv.list'], en['inv.erase'], en['inv.confirmErasePending']], ['Invitation history', 'Erase', 'Erase this invitation? Its link will stop working.']);
    assert.deepEqual([fr['inv.list'], fr['inv.erase'], fr['inv.confirmErasePending']], ['Historique des invitations', 'Effacer', 'Effacer cette invitation ? Son lien ne fonctionnera plus.']);
    for (const l of ['es', 'de']) {
        for (const k of ['inv.list', 'inv.erase', 'inv.confirmErasePending']) { assert.ok(read(l)[k] && read(l)[k] !== en[k], `${l} ${k}`); }
    }
});

for (const [name, answer, expectedDeletes] of [['declined', false, 0], ['accepted', true, 1]]) {
    test(`Invitations tab: erasing a pending invitation asks first (${name})`, async () => {
        const { w, d } = await openHistory();
        const questions = [];
        w.confirm = (q) => { questions.push(q); return answer; };
        d.querySelector('#ucInvResult').hidden = false;
        const listings = w.__calls.filter((c) => c.method === 'GET' && c.url === 'MailConcierge/Invitations').length;

        eraseButtonOf(d, 'i1').click();
        await wait();

        assert.deepEqual(questions, ['ERASE PENDING?']);
        assert.equal(invDeletes(w).length, expectedDeletes);
        assert.equal(d.querySelector('#ucInvResult').hidden, expectedDeletes === 1);
        assert.equal(w.__calls.filter((c) => c.method === 'GET' && c.url === 'MailConcierge/Invitations').length, listings + expectedDeletes);
        if (expectedDeletes) {
            assert.equal(invDeletes(w)[0].url, 'MailConcierge/Invitations/i1?erase=true');
            assert.equal(invDeletes(w)[0].body, undefined);
        }
    });
}

for (const id of ['i2', 'i3', 'i4']) {
    test(`Invitations tab: erasing a ${HISTORY[+id.slice(1) - 1].state} invitation asks nothing and sends DELETE ...?erase=true`, async () => {
        const { w, d } = await openHistory();
        let asked = 0;
        w.confirm = () => { asked++; return false; };
        const listings = w.__calls.filter((c) => c.method === 'GET' && c.url === 'MailConcierge/Invitations').length;

        eraseButtonOf(d, id).click();
        await wait();

        assert.equal(asked, 0);
        assert.deepEqual(invDeletes(w).map((c) => c.url), [`MailConcierge/Invitations/${id}?erase=true`]);
        assert.equal(w.__calls.filter((c) => c.method === 'GET' && c.url === 'MailConcierge/Invitations').length, listings + 1);
    });
}

test('Invitations tab: the revoke button still sends a DELETE without the erase parameter', async () => {
    const { w, d } = await openHistory();
    w.confirm = () => true;
    const revoke = [...d.querySelectorAll('#ucInvList > div')][0].querySelector('button:not([is="paper-icon-button-light"]):nth-of-type(2)');

    revoke.click();
    await wait();

    assert.deepEqual(invDeletes(w).map((c) => c.url), ['MailConcierge/Invitations/i1']);
});

test('Invitations tab: a failed erase shows the error of the invitations tab', async () => {
    const response = { json: () => Promise.resolve({ code: 'service_closed' }) };
    const { w, d } = await openHistory({ eraseError: response, strings: { ...ERASE_STRINGS, 'inv.errServiceClosed': 'CLOSED' } });

    eraseButtonOf(d, 'i2').click();
    await wait();

    assert.equal(d.querySelector('#ucInvError').hidden, false);
    assert.equal(d.querySelector('#ucInvError').textContent, 'CLOSED');
    assert.equal(invDeletes(w).length, 1);
});

test('Invitations tab: a closed service (409 service_closed) shows its own sentence', async () => {
    const en = JSON.parse(readFileSync(here('../../Jellyfin.Plugin.MailConcierge/Resources/Strings/en.json'), 'utf8'));
    const sentence = en['inv.errServiceClosed'];
    assert.ok(sentence);
    const { w, d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', {
        session: { 'mailconcierge.cfgTab': 'invitations' },
        strings: { 'inv.errServiceClosed': sentence, 'inv.errGeneric': 'GENERIC' },
        createError: { status: 409, json: () => Promise.resolve({ code: 'service_closed' }) },
    });
    d.querySelector('#ucInvUsername').value = 'alice';
    d.querySelector('#ucInvLibraries input').checked = true;
    d.querySelector('#ucInvForm').dispatchEvent(new w.Event('submit', { cancelable: true, bubbles: true }));
    await wait();
    assert.equal(d.querySelector('#ucInvError').textContent, sentence);
    assert.equal(d.querySelector('#ucInvError').hidden, false);
});

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

test('SMTP tab: the clear-password button is hidden when no password is stored', async () => {
    const { d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { config: { smtpPasswordSet: false } });
    assert.equal(d.querySelector('#ucClearSmtpPassword').hidden, true);
});

test('SMTP tab: the clear-password button asks, then posts exactly { clearSmtpPassword: true }', async () => {
    const { w, d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { strings: { smtpPasswordClear: 'Clear', smtpPasswordClearConfirm: 'Sure?' } });
    const button = d.querySelector('#ucClearSmtpPassword');
    assert.equal(button.hidden, false);

    w.confirm = () => false;
    button.dispatchEvent(new w.Event('click', { bubbles: true }));
    await wait();
    assert.equal(posts(w).length, 0);

    let question = '';
    w.confirm = (q) => { question = q; return true; };
    button.dispatchEvent(new w.Event('click', { bubbles: true }));
    await wait();
    assert.equal(question, 'Sure?');
    assert.equal(posts(w).length, 1);
    assert.deepEqual(posts(w)[0].body, { clearSmtpPassword: true });
});

test('E-mail sign-in: the checkbox loads, and only the General tab saves it', async () => {
    const { w, d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge');
    const box = d.querySelector('#ucTab-general [data-f="emailLogin"]');
    assert.ok(box, 'the checkbox is in the General tab');
    assert.equal(box.checked, true);
    assert.equal(box.disabled, false);
    assert.equal(d.querySelector('[data-help="help.emailLogin"]') !== null, true);

    box.checked = false;
    submit(w, d, 'general');
    await wait();
    assert.equal(posts(w)[0].body.emailLogin, false);

    submit(w, d, 'smtp');
    await wait();
    assert.ok(!('emailLogin' in posts(w)[1].body));
});

test('E-mail sign-in: unavailable disables the checkbox, shows the sentence and never posts a changed value', async () => {
    const { w, d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { status: { emailLoginAvailable: false } });
    const box = d.querySelector('[data-f="emailLogin"]');
    assert.equal(box.disabled, true);
    assert.equal(box.checked, true);
    const note = d.querySelector('#ucEmailLoginUnavailable');
    assert.equal(note.hidden, false);
    assert.equal(note.textContent, 'NOT AVAILABLE');

    submit(w, d, 'general');
    await wait();
    assert.ok(!('emailLogin' in posts(w)[0].body));
});

test('E-mail sign-in: available hides the unavailable sentence; the shared sentence shows the count only when above 0', async () => {
    const none = await open('http://jf.test/web/#/configurationpage?name=MailConcierge');
    assert.equal(none.d.querySelector('#ucEmailLoginUnavailable').hidden, true);
    assert.equal(none.d.querySelector('#ucEmailLoginShared').hidden, true);

    const shared = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { status: { sharedEmailCount: 3 } });
    assert.equal(shared.d.querySelector('#ucEmailLoginShared').hidden, false);
    assert.equal(shared.d.querySelector('#ucEmailLoginShared').textContent, '3 SHARED');
});

test('E-mail sign-in: the accounts sharing an address are named under the count, as text, with an ellipsis when the list is cut', async () => {
    const SHARED = { strings: { emailLoginSharedAccounts: 'ACCOUNTS {0}' } };
    const none = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', SHARED);
    assert.equal(none.d.querySelector('#ucEmailLoginSharedAccounts').hidden, true);
    assert.equal(none.d.querySelector('#ucEmailLoginSharedAccounts').textContent, '');

    const noNames = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { ...SHARED, status: { sharedEmailCount: 2, sharedEmailAccounts: [] } });
    assert.equal(noNames.d.querySelector('#ucEmailLoginSharedAccounts').hidden, true);

    const countZero = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { ...SHARED, status: { sharedEmailCount: 0, sharedEmailAccounts: [['alice', 'bob']] } });
    assert.equal(countZero.d.querySelector('#ucEmailLoginSharedAccounts').hidden, true);

    const two = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { ...SHARED, status: { sharedEmailCount: 2, sharedEmailAccounts: [['alice', 'bob'], ['carol', 'dave', 'erin']] } });
    const line = two.d.querySelector('#ucEmailLoginSharedAccounts');
    assert.equal(line.hidden, false);
    assert.equal(line.textContent, 'ACCOUNTS alice, bob ; carol, dave, erin');
    assert.equal(line.previousElementSibling.id, 'ucEmailLoginShared');

    const cut = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { ...SHARED, status: { sharedEmailCount: 25, sharedEmailAccounts: [['alice', 'bob']] } });
    assert.equal(cut.d.querySelector('#ucEmailLoginSharedAccounts').textContent, 'ACCOUNTS alice, bob ; \u2026');

    const hostile = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { ...SHARED, status: { sharedEmailCount: 1, sharedEmailAccounts: [['<b>x</b>', 'bob']] } });
    const text = hostile.d.querySelector('#ucEmailLoginSharedAccounts');
    assert.equal(text.textContent, 'ACCOUNTS <b>x</b>, bob');
    assert.equal(text.querySelector('b'), null);
});

const ROWS = [
    { userId: '11111111-1111-1111-1111-111111111111', username: 'alice', email: 'alice@example.org', currentEmail: null },
    { userId: '22222222-2222-2222-2222-222222222222', username: 'bob', email: 'bob@example.org', currentEmail: 'bob@example.com' }
];

test('Seerr import: rows are unticked, the warning is shown with the preview,', async () => {
    const { w, d } = await open('http://jf.example/web/#/configurationpage?name=MailConcierge', { preview: ROWS, strings: { importWarning: 'WARN IMPORT' } });
    d.querySelector('#ucPreview').click();
    await wait();
    const boxes = [...d.querySelectorAll('#ucImportRows input[type=checkbox]')];
    assert.equal(boxes.length, 2);
    assert.ok(boxes.every((b) => b.checked === false));
    assert.equal(d.querySelector('#ucImportBox').hidden, false);
    assert.equal(d.querySelector('#ucImportWarn').textContent, 'WARN IMPORT');
    assert.equal(d.querySelector('#ucImportBox').contains(d.querySelector('#ucImportWarn')), true);
});

test('Seerr import: the pairs seen in the preview are posted, and a changed count adds the sentence', async () => {
    const { w, d } = await open('http://jf.example/web/#/configurationpage?name=MailConcierge', { preview: ROWS, importResult: { imported: 1, changed: 2 }, strings: { imported: 'IMPORTED {0}', importChanged: 'CHANGED {0}' } });
    d.querySelector('#ucPreview').click();
    await wait();
    d.querySelectorAll('#ucImportRows input[type=checkbox]')[1].click();
    d.querySelector('#ucImport').click();
    await wait();
    const call = w.__calls.find((c) => c.url === 'MailConcierge/Seerr/Import');
    assert.deepEqual(call.body, { items: [{ userId: ROWS[1].userId, email: 'bob@example.org' }] });
    assert.equal(d.querySelector('#ucSeerrResult').textContent, 'IMPORTED 1 CHANGED 2');
});

test('Seerr import: nothing changed, no extra sentence', async () => {
    const { w, d } = await open('http://jf.example/web/#/configurationpage?name=MailConcierge', { preview: ROWS, importResult: { imported: 1, changed: 0 }, strings: { imported: 'IMPORTED {0}', importChanged: 'CHANGED {0}' } });
    d.querySelector('#ucPreview').click();
    await wait();
    d.querySelectorAll('#ucImportRows input[type=checkbox]')[0].click();
    d.querySelector('#ucImport').click();
    await wait();
    assert.equal(d.querySelector('#ucSeerrResult').textContent, 'IMPORTED 1');
});

test('General tab: the first block is enabled, publicUrl, emailLogin, allowAdministrators, nativeForgotPassword, sessionsOnReset, sessionInactiveDays', async () => {
    const { d, errors } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge');
    const keys = [...d.querySelectorAll('#ucTab-general [data-f]')].map((el) => el.getAttribute('data-f'));
    assert.deepEqual(keys.slice(0, 7), ['enabled', 'publicUrl', 'emailLogin', 'allowAdministrators', 'nativeForgotPassword', 'sessionsOnReset', 'sessionInactiveDays']);
    assert.ok(!keys.includes('defaultLanguage'));
    // The help, hint and status lines travel with their field.
    const url = d.querySelector('[data-f="publicUrl"]').closest('.inputContainer');
    assert.ok(url.querySelector('[data-t="publicUrlHint"]') && url.querySelector('#ucWarnPublicUrl'));
    const login = d.querySelector('[data-f="emailLogin"]').closest('.checkboxContainer');
    assert.ok(login.querySelector('#ucEmailLoginUnavailable') && login.querySelector('#ucEmailLoginShared'));
    assert.deepEqual(errors, []);
});

test('E-mail language: first field of the Templates tab, loaded, saved with the templates only', async () => {
    const { w, d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge');
    assert.equal(d.querySelector('#ucTab-templates [data-f]').getAttribute('data-f'), 'defaultLanguage');
    assert.equal(d.querySelector('#ucTab-templates form [data-f="defaultLanguage"]').closest('form').getAttribute('data-tab-form'), 'templates');
    assert.equal(d.querySelector('[data-f="defaultLanguage"]').value, 'fr');

    submit(w, d, 'general');
    await wait();
    assert.ok(!('defaultLanguage' in posts(w)[0].body));

    d.querySelector('[data-f="defaultLanguage"]').value = 'en';
    submit(w, d, 'templates');
    await wait();
    const body = posts(w)[1].body;
    assert.equal(body.defaultLanguage, 'en');
    assert.ok(!('enabled' in body) && !('publicUrl' in body) && !('nativeForgotPassword' in body));
});

test('E-mail language: changing the select rebuilds the template list, and the sentence no longer points at the General tab', async () => {
    const { w, d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { strings: { templatesLang: 'SHOWN {0}' } });
    assert.ok(d.querySelector('#ucTemplates textarea[data-key^="fr/"]'));
    const select = d.querySelector('[data-f="defaultLanguage"]');
    select.value = 'en';
    select.dispatchEvent(new w.Event('change'));
    assert.ok(d.querySelector('#ucTemplates textarea[data-key^="en/"]'));
    assert.equal(d.querySelector('#ucTemplates textarea[data-key^="fr/"]'), null);
    assert.equal(d.querySelector('#ucTemplatesLang').textContent, 'SHOWN English');
});

test('the shipped strings do not send the e-mail language to the General tab, and the forgot-password switch is renamed', () => {
    const dir = here('../../Jellyfin.Plugin.MailConcierge/Resources/Strings/');
    const expected = {
        en: { native: 'Do not use Mail Concierge for “Forgot password”', templatesLang: 'Templates shown: {0}. Templates of other languages are kept as they are.' },
        fr: { native: 'Ne pas utiliser Mail Concierge pour « Mot de passe oublié »', templatesLang: 'Modèles affichés : {0}. Les modèles des autres langues sont conservés tels quels.' },
    };
    for (const lang of ['en', 'fr', 'es', 'de']) {
        const strings = JSON.parse(readFileSync(dir + lang + '.json', 'utf8'));
        assert.ok(!/General|Général|Allgemein/.test(strings['cfg.templatesLang']), `${lang}: templatesLang`);
        assert.ok(!/General|Général|Allgemein/.test(strings['cfg.help.defaultLanguage'] + strings['cfg.defaultLanguageHint']), `${lang}: language help`);
        if (expected[lang]) {
            assert.equal(strings['cfg.native'], expected[lang].native);
            assert.equal(strings['cfg.templatesLang'], expected[lang].templatesLang);
        }
    }
    const en = JSON.parse(readFileSync(dir + 'en.json', 'utf8'));
    assert.equal(en['cfg.nativeHint'], 'Jellyfin then keeps its own behaviour: it writes a PIN to a file on the server, and only for requests from the local network. No e-mail is sent.');
    const fr = JSON.parse(readFileSync(dir + 'fr.json', 'utf8'));
    assert.equal(fr['cfg.nativeHint'], "Jellyfin garde alors son fonctionnement d’origine : il écrit un code PIN dans un fichier sur le serveur, et seulement pour une demande venant du réseau local. Aucun e-mail n’est envoyé.");
});

const tabState = (d) => [...d.querySelectorAll('.ucTabBtn')].filter((b) => b.classList.contains('button-submit')).map((b) => b.getAttribute('data-tab'));

test('open tab: only its button is accent-coloured (button-submit) and selected, and it has the focus that Jellyfin put on General', async () => {
    const { w, d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { session: { 'mailconcierge.cfgTab': 'invitations' }, focus: '#ucTabBtn-general' });
    assert.deepEqual(tabState(d), ['invitations']);
    assert.deepEqual([...d.querySelectorAll('.ucTabBtn[aria-selected="true"]')].map((b) => b.getAttribute('data-tab')), ['invitations']);
    assert.equal(d.activeElement.id, 'ucTabBtn-invitations');

    d.querySelector('#ucTabBtn-smtp').dispatchEvent(new w.Event('click', { bubbles: true }));
    assert.deepEqual(tabState(d), ['smtp']);
    assert.equal(d.querySelector('#ucTabBtn-invitations').getAttribute('aria-selected'), 'false');
    assert.equal(d.querySelector('#ucTabBtn-smtp').getAttribute('aria-selected'), 'true');
});

test('open tab: the save buttons are still the only submit buttons of the forms, and stay enabled once loaded', async () => {
    const { d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { session: { 'mailconcierge.cfgTab': 'smtp' } });
    assert.ok([...d.querySelectorAll('.ucTabBtn')].every((b) => b.type === 'button' && !b.closest('form')));
    const saves = [...d.querySelectorAll('form[data-tab-form] button[type="submit"]')];
    assert.equal(saves.length, 4);
    assert.ok(saves.every((b) => !b.disabled && !b.classList.contains('ucTabBtn')));
});

test('open tab: selecting a tab does not take the focus from a field', async () => {
    const { w, d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { session: { 'mailconcierge.cfgTab': 'smtp' } });
    const input = d.querySelector('[data-f="smtpHost"]');
    input.focus();
    assert.equal(d.activeElement, input);
    w.eval("document.querySelector('#ucTabBtn-seerr').dispatchEvent(new Event('click', { bubbles: true }))");
    assert.deepEqual(tabState(d), ['seerr']);
    assert.equal(d.activeElement, input);
});
