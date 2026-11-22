// Tests of the Mail Concierge settings page (Configuration/configPage.html) in jsdom, with a fake ApiClient.
// Run from this folder: npm ci && node --test --test-timeout=30000
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { JSDOM, VirtualConsole } from 'jsdom';

const here = (p) => fileURLToPath(new URL(p, import.meta.url));
const html = readFileSync(here('../../Jellyfin.Plugin.MailConcierge/Configuration/configPage.html'), 'utf8');
// The fake ApiClient answers with resolved promises and the page has no timer: a short wait is enough for the whole load chain.
const wait = (ms = 5) => new Promise((resolve) => setTimeout(resolve, ms));

const CONFIG = {
    enabled: true, nativeForgotPassword: true, publicUrl: 'https://jf.example', defaultLanguage: 'fr',
    languages: [{ code: 'en', name: 'English' }, { code: 'fr', name: 'Français' }],
    smtpHost: 'smtp.example', smtpPort: 587, smtpSecurity: 'StartTls', smtpUsername: '', smtpPassword: '', smtpPasswordSet: true,
    fromAddress: 'a@example.org', fromName: 'Mail Concierge', pinMinutes: 15, invitationDays: 9, confirmationHours: 24, cancelEmailChangeHours: 24,
    maxForgotRequestsPerIpPer15Min: 10, maxEmailsPerRecipientPerDay: 10, maxEmailsPerDay: 200, adminNotificationAddress: 'admin@example.org', maxPinFailures: 5,
    allowAdministrators: true, emailLogin: true, sessionsOnReset: 'All', sessionInactiveDays: 30, emailLogo: 'Embedded', seerrEnabled: true, seerrUrl: '', seerrApiKey: '', seerrApiKeySet: false,
    templateOverrides: {}
};

// What MailConcierge/Admin/SmtpPresets serves (same shape as Resources/Data/smtp-presets.json).
const PRESETS = [
    { id: 'gmail', name: 'Gmail', host: 'smtp.gmail.com', port: 587, security: 'starttls', docUrl: 'https://support.example.org/gmail', checkedOn: '2026-11-15' },
    { id: 'brevo', name: 'Brevo', host: 'smtp-relay.brevo.com', port: 587, security: 'starttls', docUrl: 'https://help.example.org/brevo', checkedOn: '2026-11-15' },
    { id: 'smtp2go', name: 'SMTP2GO', host: 'mail.smtp2go.com', port: 587, security: 'starttls', docUrl: 'https://support.example.org/smtp2go', checkedOn: '2026-11-15' },
    { id: 'mailgun', name: 'Mailgun', host: 'smtp.eu.mailgun.org', port: 587, security: 'starttls', docUrl: 'https://docs.example.org/mailgun', checkedOn: '2026-11-15' },
    { id: 'mailjet', name: 'Mailjet', host: 'in-v3.mailjet.com', port: 465, security: 'ssl', docUrl: 'https://dev.example.org/mailjet', checkedOn: '2026-11-15' },
];

// The shipped English texts of the settings page, as MailConcierge/Admin/Strings serves them (the "cfg." prefix removed).
const REAL_EN = (() => {
    const all = JSON.parse(readFileSync(fileURLToPath(new URL('../../Jellyfin.Plugin.MailConcierge/Resources/Strings/en.json', import.meta.url)), 'utf8'));
    return Object.fromEntries(Object.entries(all).filter(([k]) => k.startsWith('cfg.')).map(([k, v]) => [k.slice(4), v]));
})();

// What Jellyfin's GET Users serves: ids without dashes.
const USERS = [{ Id: '11111111111111111111111111111111', Name: 'alice' }, { Id: '22222222222222222222222222222222', Name: 'bob' }];

async function open(url, { session = {}, created = {}, createError = null, strings = {}, invitations = [], regenerated = {}, eraseError = null, assign = { assigned: 0, skippedAdmins: 0, skippedExternal: 0 }, assignRejection = null, config = {}, status = {}, preview = [], importResult = { imported: 0, changed: 0 }, focus = null, liveTv = { available: false }, liveTvError = null, presets = PRESETS, presetsError = null, saveError = null, emailStats = { since: '2026-11-20T08:00:00+00:00', invitations: { sent: 0, failed: 0 }, users: [] }, emailStatsError = null, users = USERS } = {}) {
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
                if (path === 'MailConcierge/Admin/SmtpPresets') { return presetsError ? Promise.reject(presetsError) : Promise.resolve(presets); }
                if (path === 'MailConcierge/Admin/Config') { return saveError ? Promise.reject(saveError) : Promise.resolve({ ok: true }); }
                if (path === 'MailConcierge/Admin/Strings') { return Promise.resolve({ language: 'en', strings: { title: 'T', save: 'Save', tabInvitations: 'Invitations', username: 'User', send: 'Send', copyLink: 'Copy link', 'inv.sentTo': 'SENT {0}', emailLoginUnavailable: 'NOT AVAILABLE', emailLoginShared: '{0} SHARED', 'inv.notSent': 'NOT SENT', 'inv.notSentLimit': 'LIMIT REACHED', 'inv.notSentQueueFull': 'QUEUE FULL', 'inv.newLink': 'NEW LINK', 'inv.newLinkSent': 'NEW LINK SENT', 'inv.newLinkNotSentLimit': 'NEW LINK LIMIT REACHED', 'inv.regenerate': 'Regenerate', ...strings } }); }
                if (path === 'MailConcierge/Seerr/ImportPreview') { return Promise.resolve(preview); }
                if (path === 'MailConcierge/Seerr/Import') { return Promise.resolve(importResult); }
                if (path === 'MailConcierge/Admin/Templates') { return Promise.resolve({}); }
                if (path === 'MailConcierge/Admin/Status') { return Promise.resolve({ proxyGuardTripped: false, canSendEmail: true, emailLoginAvailable: true, sharedEmailCount: 0, ...(typeof status === 'function' ? status() : status) }); }
                if (path === 'MailConcierge/Admin/EmailStats') { return emailStatsError ? Promise.reject(emailStatsError) : Promise.resolve(emailStats); }
                if (path === 'Users') { return Promise.resolve(users); }
                if (path === 'MailConcierge/Libraries') { return Promise.resolve([{ id: 'l1', name: 'Films' }, { id: 'l2', name: 'Series' }]); }
                if (path === 'MailConcierge/LiveTv') { return liveTvError ? Promise.reject(liveTvError) : Promise.resolve(liveTv); }
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
        const button = [...d.querySelectorAll('#ucInvTable button')].find((b) => b.textContent === 'Regenerate');
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
    assert.equal(result.classList.contains('ucErr'), false);
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
        assert.equal(result.classList.contains('ucErr'), true);
        assert.equal(assignCalls(w).length, 1);
    });
}

const STATES = ['Pending', 'Accepted', 'Revoked', 'Expired'];
const HISTORY = STATES.map((state, i) => ({ id: 'i' + (i + 1), proposedUsername: 'user' + (i + 1), email: null, state, expiresAt: '2026-11-20T00:00:00Z' }));
const ERASE_STRINGS = { 'inv.revoke': 'Revoke', 'inv.erase': 'ERASE', 'inv.confirmErasePending': 'ERASE PENDING?', 'inv.confirmErase': 'ERASE LINE?', 'inv.list': 'HISTORY' };
const openHistory = (extra = {}) => open('http://jf.test/web/#/configurationpage?name=MailConcierge', { session: { 'mailconcierge.cfgTab': 'invitations' }, invitations: HISTORY, strings: ERASE_STRINGS, ...extra });
const eraseButtonOf = (d, id) => d.querySelector(`#ucInvTable tbody tr[data-id="${id}"] button[is="paper-icon-button-light"]`);
const invDeletes = (w) => w.__calls.filter((c) => c.method === 'DELETE');

test('Invitations tab: every line, whatever its state, ends with an erase icon button; the heading is the history', async () => {
    const { d } = await openHistory();
    assert.equal(d.querySelector('[data-t="inv.list"]').textContent, 'HISTORY');
    const rows = [...d.querySelectorAll('#ucInvTable tbody tr')];
    assert.equal(rows.length, 4);
    for (const row of rows) {
        const buttons = [...row.querySelectorAll('button[is="paper-icon-button-light"]')];
        assert.equal(buttons.length, 1);
        assert.equal(buttons[0].title, 'ERASE');
        assert.equal(buttons[0].getAttribute('aria-label'), 'ERASE');
        assert.equal(buttons[0].classList.contains('paper-icon-button-light'), true);
        assert.equal(row.querySelector('td:last-child > div').lastElementChild, buttons[0]);
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

for (const [id, answer, expectedDeletes] of [['i2', true, 1], ['i3', true, 1], ['i4', true, 1], ['i2', false, 0], ['i3', false, 0], ['i4', false, 0]]) {
    test(`Invitations tab: erasing a ${HISTORY[+id.slice(1) - 1].state} invitation asks to confirm, then sends DELETE ...?erase=true only if accepted (${answer ? 'accepted' : 'declined'})`, async () => {
        const { w, d } = await openHistory();
        const questions = [];
        w.confirm = (q) => { questions.push(q); return answer; };
        const listings = w.__calls.filter((c) => c.method === 'GET' && c.url === 'MailConcierge/Invitations').length;

        eraseButtonOf(d, id).click();
        await wait();

        assert.deepEqual(questions, ['ERASE LINE?']);
        assert.deepEqual(invDeletes(w).map((c) => c.url), expectedDeletes ? [`MailConcierge/Invitations/${id}?erase=true`] : []);
        assert.equal(w.__calls.filter((c) => c.method === 'GET' && c.url === 'MailConcierge/Invitations').length, listings + expectedDeletes);
    });
}

test('Invitations tab: the revoke button still sends a DELETE without the erase parameter', async () => {
    const { w, d } = await openHistory();
    w.confirm = () => true;
    const revoke = d.querySelector('#ucInvTable tbody tr[data-id="i1"]').querySelector('button:not([is="paper-icon-button-light"]):nth-of-type(2)');

    revoke.click();
    await wait();

    assert.deepEqual(invDeletes(w).map((c) => c.url), ['MailConcierge/Invitations/i1']);
});

test('Invitations tab: a failed erase shows the error of the invitations tab', async () => {
    const response = { json: () => Promise.resolve({ code: 'service_closed' }) };
    const { w, d } = await openHistory({ eraseError: response, strings: { ...ERASE_STRINGS, 'inv.errServiceClosed': 'CLOSED' } });
    w.confirm = () => true;

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

// The page reads the Status again each time the General tab opens: one window can show several answers.
const reopenGeneral = async (d) => {
    d.querySelector('#ucTabBtn-smtp').click();
    d.querySelector('#ucTabBtn-general').click();
    await wait();
};

test('E-mail sign-in: available hides the unavailable sentence; the shared sentence shows the count only when above 0', async () => {
    let status = {};
    const { d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { status: () => status });
    assert.equal(d.querySelector('#ucEmailLoginUnavailable').hidden, true);
    assert.equal(d.querySelector('#ucEmailLoginShared').hidden, true);

    status = { sharedEmailCount: 3 };
    await reopenGeneral(d);
    assert.equal(d.querySelector('#ucEmailLoginShared').hidden, false);
    assert.equal(d.querySelector('#ucEmailLoginShared').textContent, '3 SHARED');
});

test('E-mail sign-in: the accounts sharing an address are named under the count, as text, with an ellipsis when the list is cut', async () => {
    let status = {};
    const { d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { strings: { emailLoginSharedAccounts: 'ACCOUNTS {0}' }, status: () => status });
    const line = d.querySelector('#ucEmailLoginSharedAccounts');
    assert.equal(line.hidden, true);
    assert.equal(line.textContent, '');

    status = { sharedEmailCount: 2, sharedEmailAccounts: [] };
    await reopenGeneral(d);
    assert.equal(line.hidden, true);

    status = { sharedEmailCount: 0, sharedEmailAccounts: [['alice', 'bob']] };
    await reopenGeneral(d);
    assert.equal(line.hidden, true);

    status = { sharedEmailCount: 2, sharedEmailAccounts: [['alice', 'bob'], ['carol', 'dave', 'erin']] };
    await reopenGeneral(d);
    assert.equal(line.hidden, false);
    assert.equal(line.textContent, 'ACCOUNTS alice, bob ; carol, dave, erin');
    assert.equal(line.previousElementSibling.id, 'ucEmailLoginShared');

    status = { sharedEmailCount: 25, sharedEmailAccounts: [['alice', 'bob']] };
    await reopenGeneral(d);
    assert.equal(line.textContent, 'ACCOUNTS alice, bob ; \u2026');

    status = { sharedEmailCount: 1, sharedEmailAccounts: [['<b>x</b>', 'bob']] };
    await reopenGeneral(d);
    assert.equal(line.textContent, 'ACCOUNTS <b>x</b>, bob');
    assert.equal(line.querySelector('b'), null);
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
    // The help travels with its field, in its (i); the warning stays under it.
    const url = d.querySelector('[data-f="publicUrl"]').closest('.inputContainer');
    assert.equal(url.querySelector('.ucHelp').getAttribute('data-help'), 'help.publicUrl publicUrlHint');
    assert.equal(url.querySelector('[data-t="publicUrlHint"]'), null);
    assert.ok(url.querySelector('#ucWarnPublicUrl'));
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

const LOGO_HINTS = { Embedded: 'logoEmbeddedHint', Hosted: 'logoHostedHint', None: 'logoNoneHint' };
// The sentence of the chosen mode is in the (i) of the logo list, after the general help.
const logoHint = (d) => paras(iconOf(d, 'emailLogo'));

test('E-mail logo: a three-choice list in the Templates tab, after the language, with the Embedded choice first', async () => {
    const { d, errors } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { strings: REAL_EN });
    const select = d.querySelector('#ucTab-templates select[data-f="emailLogo"]');
    assert.ok(select, 'the list is in the Templates tab');
    assert.deepEqual([...select.options].map((o) => o.value), ['Embedded', 'Hosted', 'None']);
    assert.deepEqual([...select.options].map((o) => o.textContent), [REAL_EN.logoEmbedded, REAL_EN.logoHosted, REAL_EN.logoNone]);
    const keys = [...d.querySelectorAll('#ucTab-templates [data-f]')].map((el) => el.getAttribute('data-f'));
    assert.deepEqual(keys, ['defaultLanguage', 'emailLogo']);
    assert.equal(select.closest('form').getAttribute('data-tab-form'), 'templates');
    assert.equal(select.closest('.selectContainer').querySelector('.ucHelpRow label').textContent, REAL_EN.emailLogo);
    assert.ok(select.closest('.selectContainer').querySelector('[data-help="help.emailLogo"]'));
    assert.deepEqual(errors, []);
});

test('E-mail logo: the initial value is the configured one, and only the sentence of that choice is in the (i)', async () => {
    for (const mode of ['Embedded', 'Hosted', 'None']) {
        const { d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { strings: REAL_EN, config: { emailLogo: mode } });
        assert.equal(d.querySelector('[data-f="emailLogo"]').value, mode);
        assert.deepEqual(logoHint(d), [REAL_EN['help.emailLogo'], REAL_EN[LOGO_HINTS[mode]]]);
        assert.equal(d.querySelector('[data-logo-hint]'), null, 'no loose sentence under the list');
    }
});

test('E-mail logo: changing the choice switches the sentence and sends nothing; saving the Templates tab sends the value', async () => {
    const { w, d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { strings: REAL_EN });
    const select = d.querySelector('[data-f="emailLogo"]');
    for (const mode of ['Hosted', 'None', 'Embedded']) {
        select.value = mode;
        select.dispatchEvent(new w.Event('change', { bubbles: true }));
        assert.deepEqual(logoHint(d), [REAL_EN['help.emailLogo'], REAL_EN[LOGO_HINTS[mode]]]);
    }
    assert.equal(posts(w).length, 0);

    select.value = 'None';
    submit(w, d, 'templates');
    await wait();
    assert.equal(posts(w).length, 1);
    assert.equal(posts(w)[0].body.emailLogo, 'None');
    assert.equal(posts(w)[0].body.defaultLanguage, 'fr');

    // The other tabs do not carry it.
    submit(w, d, 'general');
    await wait();
    assert.ok(!('emailLogo' in posts(w)[1].body));
});

test('E-mail logo: the texts exist in the four languages (labels, one sentence per choice, tooltip), translated', () => {
    const read = (l) => JSON.parse(readFileSync(here(`../../Jellyfin.Plugin.MailConcierge/Resources/Strings/${l}.json`), 'utf8'));
    const keys = ['cfg.emailLogo', 'cfg.logoEmbedded', 'cfg.logoHosted', 'cfg.logoNone', 'cfg.logoEmbeddedHint', 'cfg.logoHostedHint', 'cfg.logoNoneHint', 'cfg.help.emailLogo', 'cfg.err.invalid_email_logo'];
    const en = read('en');
    for (const l of ['fr', 'en', 'de', 'es']) {
        const strings = read(l);
        for (const k of keys) {
            assert.ok(typeof strings[k] === 'string' && strings[k].trim(), `${l}: ${k}`);
            if (l !== 'en') { assert.notEqual(strings[k], en[k], `${l}: ${k} is translated`); }
        }
    }
    const fr = read('fr');
    assert.equal(fr['cfg.emailLogo'], 'Logo des e-mails');
    assert.equal(fr['cfg.logoEmbedded'], 'Inclus dans le message (recommandé)');
    assert.equal(fr['cfg.logoHosted'], 'Chargé depuis le serveur');
    assert.equal(fr['cfg.logoNone'], 'Aucun logo');
    assert.equal(fr['cfg.logoEmbeddedHint'], 'Le logo est joint au message : il s’affiche sans clic et sans rien demander à votre serveur (environ 30 Ko de plus par e-mail).');
    assert.equal(fr['cfg.logoHostedHint'], 'Le logo est chargé depuis votre adresse publique à l’ouverture : e-mail plus léger, mais certains lecteurs bloquent l’image et votre serveur voit l’ouverture.');
    assert.equal(fr['cfg.logoNoneHint'], 'Aucun logo dans les e-mails.');
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

// ---- Live TV checkbox of the invitation form ----
const INV = { session: { 'mailconcierge.cfgTab': 'invitations' } };
const INV_URL = 'http://jf.test/web/#/configurationpage?name=MailConcierge';
const tvRow = (d) => d.querySelector('#ucInvLiveTvRow');
const tvBox = (d) => d.querySelector('#ucInvLiveTv');
const invPosts = (w) => w.__calls.filter((c) => c.method === 'POST' && c.url === 'MailConcierge/Invitations');
const sendInvitation = async (w, d) => {
    d.querySelector('#ucInvUsername').value = 'alice';
    d.querySelector('#ucInvForm').dispatchEvent(new w.Event('submit', { cancelable: true, bubbles: true }));
    await wait();
};
const toggle = (w, box, on) => { box.checked = on; box.dispatchEvent(new w.Event('change')); };

test('Live TV: the row is shown, unticked and outside the library list, when Live TV is available', async () => {
    const { w, d, errors } = await open(INV_URL, { ...INV, liveTv: { available: true } });
    assert.notEqual(tvRow(d).style.display, 'none');
    assert.equal(tvBox(d).checked, false);
    assert.ok(tvRow(d).contains(tvBox(d)));
    assert.equal(d.querySelector('#ucInvLibraries').contains(tvRow(d)), false);
    assert.equal(d.querySelectorAll('#ucInvLibraries input').length, 2);
    assert.equal(tvRow(d).previousElementSibling, d.querySelector('#ucInvLibraries'));
    assert.ok(w.__calls.some((c) => c.method === 'GET' && c.url === 'MailConcierge/LiveTv'));
    assert.deepEqual(errors, []);
});

test('Live TV: the row stays hidden when Live TV is not available', async () => {
    const { d } = await open(INV_URL, { ...INV, liveTv: { available: false } });
    assert.equal(tvRow(d).style.display, 'none');
});

test('Live TV: an error from the availability call hides the row and the form still works', async () => {
    const { w, d } = await open(INV_URL, { ...INV, liveTvError: { status: 500 } });
    assert.equal(tvRow(d).style.display, 'none');
    assert.equal(d.querySelectorAll('#ucInvLibraries input').length, 2);
    assert.equal(d.querySelector('#ucInvError').hidden, true);
    d.querySelector('#ucInvLibraries input').checked = true;
    await sendInvitation(w, d);
    assert.equal(invPosts(w).length, 1);
    assert.equal(invPosts(w)[0].body.liveTv, false);
});

test('Live TV: "Select all" ticks the Live TV box too, and unticking one box unticks "Select all"', async () => {
    const { w, d } = await open(INV_URL, { ...INV, liveTv: { available: true } });
    const all = d.querySelector('#ucInvSelectAll');
    toggle(w, all, true);
    assert.equal(tvBox(d).checked, true);
    assert.ok([...d.querySelectorAll('#ucInvLibraries input')].every((b) => b.checked));
    toggle(w, tvBox(d), false);
    assert.equal(all.checked, false);
    toggle(w, tvBox(d), true);
    assert.equal(all.checked, true);
    toggle(w, d.querySelector('#ucInvLibraries input'), false);
    assert.equal(all.checked, false);
    toggle(w, all, true);
    toggle(w, all, false);
    assert.equal(tvBox(d).checked, false);
});

test('Live TV: with Live TV unavailable, "Select all" only depends on the libraries', async () => {
    const { w, d } = await open(INV_URL, { ...INV, liveTv: { available: false } });
    const all = d.querySelector('#ucInvSelectAll');
    toggle(w, all, true);
    assert.equal(tvBox(d).checked, false);
    for (const b of d.querySelectorAll('#ucInvLibraries input')) { toggle(w, b, true); }
    assert.equal(all.checked, true);
});

test('Live TV: the invitation request carries liveTv true when ticked, and the libraries stay free of it', async () => {
    const { w, d } = await open(INV_URL, { ...INV, liveTv: { available: true } });
    w.confirm = () => { throw new Error('must not ask'); };
    d.querySelector('#ucInvLibraries input').checked = true;
    toggle(w, tvBox(d), true);
    await sendInvitation(w, d);
    const body = invPosts(w)[0].body;
    assert.equal(body.liveTv, true);
    assert.deepEqual(body.libraryIds, ['l1']);
});

test('Live TV: the invitation request carries liveTv false when the box is not ticked', async () => {
    const { w, d } = await open(INV_URL, { ...INV, liveTv: { available: true } });
    d.querySelector('#ucInvLibraries input').checked = true;
    await sendInvitation(w, d);
    assert.equal(invPosts(w)[0].body.liveTv, false);
});

test('Live TV: a ticked but hidden box is sent as liveTv false', async () => {
    const { w, d } = await open(INV_URL, { ...INV, liveTv: { available: false } });
    tvBox(d).checked = true;
    d.querySelector('#ucInvLibraries input').checked = true;
    await sendInvitation(w, d);
    assert.equal(invPosts(w)[0].body.liveTv, false);
});

test('Live TV: ticking only Live TV still asks the "no library" confirmation', async () => {
    const { w, d } = await open(INV_URL, { ...INV, liveTv: { available: true } });
    let asked = 0;
    w.confirm = () => { asked++; return true; };
    toggle(w, tvBox(d), true);
    await sendInvitation(w, d);
    assert.equal(asked, 1);
    const body = invPosts(w)[0].body;
    assert.deepEqual(body.libraryIds, []);
    assert.equal(body.liveTv, true);
});

test('Live TV: the label comes from the inv.liveTv text', async () => {
    const { d } = await open(INV_URL, { ...INV, liveTv: { available: true }, strings: { 'inv.liveTv': 'TV EN DIRECT' } });
    assert.equal(tvRow(d).textContent.trim(), 'TV EN DIRECT');
    const fallback = await open(INV_URL, { ...INV, liveTv: { available: true } });
    assert.equal(tvRow(fallback.d).textContent.trim(), 'Live TV');
});

// --- SMTP provider list (presets), help area and suggested public URL ---
const SMTP_TAB = { session: { 'mailconcierge.cfgTab': 'smtp' } };
const openSmtp = (extra = {}) => open('http://jf.test/web/#/configurationpage?name=MailConcierge', { ...SMTP_TAB, strings: REAL_EN, ...extra });
const providerOptions = (d) => [...d.querySelectorAll('#ucSmtpProvider option')].map((o) => [o.value, o.textContent]);
const choose = (w, d, value) => {
    const select = d.querySelector('#ucSmtpProvider');
    select.value = value;
    select.dispatchEvent(new w.Event('change', { bubbles: true }));
};
const field = (d, name) => d.querySelector(`[data-f="${name}"]`);
// The (i) of a field and what it says: the paragraphs of its bubble (text only), its links, and its accessible name.
const iconOf = (d, name) => field(d, name).closest('.inputContainer, .selectContainer, .checkboxContainer').querySelector('.ucHelp');
const paras = (icon) => [...icon.querySelectorAll('.ucTipPara')].map((p) => p.textContent);
const providerIcon = (d) => d.querySelector('#ucSmtpProvider').closest('.selectContainer').querySelector('.ucHelp');
const userIcon = (d) => iconOf(d, 'smtpUsername');
const passwordIcon = (d) => iconOf(d, 'smtpPassword');

test('SMTP provider list: Custom first, then the five brand names (not translated), above the host field', async () => {
    const { d, errors } = await openSmtp();
    assert.deepEqual(providerOptions(d), [['', 'Custom'], ['gmail', 'Gmail'], ['brevo', 'Brevo'], ['smtp2go', 'SMTP2GO'], ['mailgun', 'Mailgun'], ['mailjet', 'Mailjet']]);
    const select = d.querySelector('#ucSmtpProvider');
    assert.ok(select.compareDocumentPosition(field(d, 'smtpHost')) & 4, 'the list comes before the host field');
    assert.equal(select.hasAttribute('data-f'), false, 'the list is never part of the saved settings');
    assert.equal(d.querySelector('[data-t="smtpProvider"]').textContent, 'Provider');
    assert.deepEqual(errors, []);
});

test('SMTP provider list: choosing a preset fills host, port and security, sends nothing and keeps the user name and the typed password', async () => {
    const { w, d } = await openSmtp({ config: { smtpPasswordSet: false } });
    field(d, 'smtpUsername').value = 'someone@example.org';
    field(d, 'smtpPassword').value = 'not-a-real-password';
    const before = w.__calls.length;

    choose(w, d, 'mailjet');
    await wait();

    assert.equal(field(d, 'smtpHost').value, 'in-v3.mailjet.com');
    assert.equal(field(d, 'smtpPort').value, '465');
    assert.equal(field(d, 'smtpSecurity').value, 'SslOnConnect');
    assert.equal(field(d, 'smtpUsername').value, 'someone@example.org');
    assert.equal(field(d, 'smtpPassword').value, 'not-a-real-password');
    assert.equal(w.__calls.length, before, 'no request at all');
    assert.equal(posts(w).length, 0);

    choose(w, d, 'gmail');
    assert.equal(field(d, 'smtpHost').value, 'smtp.gmail.com');
    assert.equal(field(d, 'smtpPort').value, '587');
    assert.equal(field(d, 'smtpSecurity').value, 'StartTls');
    assert.equal(d.querySelector('#ucWarnSmtp').hidden, true);
});

const typeInto = (w, el, value, type = 'input') => {
    el.value = value;
    el.dispatchEvent(new w.Event(type, { bubbles: true }));
};

test('SMTP provider list: after a preset, editing the host, port or security by hand selects Custom and empties the provider hints; typing the preset values back selects it again', async () => {
    const { w, d } = await openSmtp({ config: { smtpPasswordSet: false } });
    const help = () => userIcon(d);
    choose(w, d, 'brevo');
    assert.equal(d.querySelector('#ucSmtpProvider').value, 'brevo');
    assert.equal(help().hidden, false);
    const before = w.__calls.length;

    typeInto(w, field(d, 'smtpHost'), 'mail.example.org');
    assert.equal(d.querySelector('#ucSmtpProvider').value, '');
    assert.equal(help().hidden, true);
    assert.equal(help().textContent, '');
    assert.equal(providerIcon(d).querySelector('a'), null, 'no provider link for Custom');
    assert.equal(providerIcon(d).getAttribute('role'), 'img');

    typeInto(w, field(d, 'smtpHost'), 'SMTP-Relay.Brevo.com');
    assert.equal(d.querySelector('#ucSmtpProvider').value, 'brevo', 'the host is compared without regard to case');
    assert.equal(help().hidden, false);
    assert.deepEqual(paras(help()), [REAL_EN['smtpPreset.brevo.user']]);

    typeInto(w, field(d, 'smtpPort'), '2525');
    assert.equal(d.querySelector('#ucSmtpProvider').value, '');
    assert.equal(help().hidden, true);
    typeInto(w, field(d, 'smtpPort'), '587');
    assert.equal(d.querySelector('#ucSmtpProvider').value, 'brevo');

    typeInto(w, field(d, 'smtpSecurity'), 'None', 'change');
    assert.equal(d.querySelector('#ucSmtpProvider').value, '');
    assert.equal(help().hidden, true);
    typeInto(w, field(d, 'smtpSecurity'), 'StartTls', 'change');
    assert.equal(d.querySelector('#ucSmtpProvider').value, 'brevo');
    assert.equal(help().hidden, false);

    // Typing a host that belongs to another preset selects that preset.
    typeInto(w, field(d, 'smtpHost'), 'smtp.gmail.com');
    assert.equal(d.querySelector('#ucSmtpProvider').value, 'gmail');

    assert.equal(w.__calls.length, before, 'editing a field sends nothing');
    assert.equal(posts(w).length, 0);
});

test('SMTP provider list: a hand edit leaves the user name, the password and the retype reminder alone', async () => {
    const { w, d } = await openSmtp({ config: { smtpPasswordSet: true, smtpHost: 'smtp.example' } });
    field(d, 'smtpUsername').value = 'someone@example.org';
    choose(w, d, 'brevo');
    const line = d.querySelector('#ucSmtpRetypePassword');
    assert.equal(line.hidden, false);

    typeInto(w, field(d, 'smtpHost'), 'mail.example.org');

    assert.equal(field(d, 'smtpUsername').value, 'someone@example.org');
    assert.equal(field(d, 'smtpPassword').value, '');
    assert.equal(line.hidden, false);
});

test('SMTP provider list: Custom changes no field', async () => {
    const { w, d } = await openSmtp();
    choose(w, d, 'brevo');
    field(d, 'smtpHost').value = 'mail.example.org';
    field(d, 'smtpPort').value = '2525';
    field(d, 'smtpSecurity').value = 'None';
    field(d, 'smtpUsername').value = 'someone@example.org';
    const before = w.__calls.length;

    choose(w, d, '');

    assert.equal(field(d, 'smtpHost').value, 'mail.example.org');
    assert.equal(field(d, 'smtpPort').value, '2525');
    assert.equal(field(d, 'smtpSecurity').value, 'None');
    assert.equal(field(d, 'smtpUsername').value, 'someone@example.org');
    assert.equal(w.__calls.length, before);
    assert.equal(userIcon(d).hidden, true);
    assert.equal(providerIcon(d).querySelector('a'), null);
});

for (const [name, config, expected] of [
    ['host, port and security of a preset', { smtpHost: 'smtp-relay.brevo.com', smtpPort: 587, smtpSecurity: 'StartTls' }, 'brevo'],
    ['the same, whatever the case of the host', { smtpHost: 'SMTP.Gmail.com', smtpPort: 587, smtpSecurity: 'StartTls' }, 'gmail'],
    ['a preset in SSL', { smtpHost: 'in-v3.mailjet.com', smtpPort: 465, smtpSecurity: 'SslOnConnect' }, 'mailjet'],
    ['another port', { smtpHost: 'smtp-relay.brevo.com', smtpPort: 2525, smtpSecurity: 'StartTls' }, ''],
    ['another security', { smtpHost: 'smtp-relay.brevo.com', smtpPort: 587, smtpSecurity: 'SslOnConnect' }, ''],
    ['an unknown host', { smtpHost: 'smtp.example', smtpPort: 587, smtpSecurity: 'StartTls' }, ''],
    ['an empty host', { smtpHost: '', smtpPort: 587, smtpSecurity: 'StartTls' }, ''],
]) {
    test(`SMTP provider list: at opening it shows ${expected || 'Custom'} for ${name}`, async () => {
        const { d } = await openSmtp({ config });
        assert.equal(d.querySelector('#ucSmtpProvider').value, expected);
        assert.equal(userIcon(d).hidden, expected === '');
        assert.equal(d.querySelector('#ucSmtpPresetHelp'), null, 'no loose help block under the fields');
    });
}

test('SMTP provider help: the link goes in the (i) of the provider, the user name and password hints in the (i) of their fields', async () => {
    const { w, d, errors } = await openSmtp({ config: { smtpPasswordSet: false } });
    choose(w, d, 'brevo');
    assert.deepEqual(paras(userIcon(d)), [REAL_EN['smtpPreset.brevo.user']]);
    assert.deepEqual(paras(passwordIcon(d)), [REAL_EN.secretHint, REAL_EN['smtpPreset.brevo.password']]);
    const provider = providerIcon(d);
    assert.deepEqual(paras(provider), [REAL_EN['help.smtpProvider'], REAL_EN.smtpDomainNote, 'Official instructions']);
    const links = provider.querySelectorAll('a');
    assert.equal(links.length, 1);
    assert.equal(links[0].getAttribute('href'), 'https://help.example.org/brevo');
    assert.equal(links[0].getAttribute('target'), '_blank');
    assert.ok(links[0].getAttribute('rel').split(' ').includes('noopener'));
    assert.ok(links[0].getAttribute('rel').split(' ').includes('noreferrer'));
    assert.equal(links[0].textContent, 'Official instructions');
    assert.ok(provider.querySelector('.ucTip').contains(links[0]), 'the link is inside the bubble');
    assert.equal(d.querySelectorAll('#ucTab-smtp .ucHelp a').length, 1, 'the only link of the SMTP tab is the provider one');
    // Nothing of the provider is left visible under the fields.
    assert.equal([...d.querySelectorAll('#ucTab-smtp .fieldDescription:not([hidden])')].length, 0);

    // The accessible name of an icon says what the bubble says.
    assert.ok(userIcon(d).getAttribute('aria-label').includes(REAL_EN['smtpPreset.brevo.user']));
    assert.ok(provider.getAttribute('aria-label').includes(REAL_EN.smtpDomainNote));

    // Another preset replaces the texts and the link; Gmail has no deliverability note.
    choose(w, d, 'gmail');
    assert.deepEqual(paras(userIcon(d)), [REAL_EN['smtpPreset.gmail.user']]);
    assert.deepEqual(paras(passwordIcon(d)), [REAL_EN.secretHint, REAL_EN['smtpPreset.gmail.password']]);
    assert.deepEqual(paras(provider), [REAL_EN['help.smtpProvider'], 'Official instructions']);
    assert.equal(provider.querySelector('a').getAttribute('href'), 'https://support.example.org/gmail');
    assert.equal(provider.getAttribute('role'), 'group', 'the link stays reachable by screen readers');

    // Custom: the provider icon keeps its general sentence, the user name icon goes away, the password one keeps the write-only note.
    choose(w, d, '');
    assert.deepEqual(paras(provider), [REAL_EN['help.smtpProvider']]);
    assert.equal(userIcon(d).hidden, true);
    assert.deepEqual(paras(passwordIcon(d)), [REAL_EN.secretHint]);
    assert.deepEqual(errors, []);
});

test('SMTP provider help: specific notes (SMTP2GO port, Mailgun US region) are in the (i) of the provider', async () => {
    const { w, d } = await openSmtp({ config: { smtpPasswordSet: false } });
    choose(w, d, 'smtp2go');
    assert.ok(paras(providerIcon(d)).some((t) => t.includes('2525')));
    choose(w, d, 'mailgun');
    assert.ok(paras(providerIcon(d)).some((t) => t.includes('smtp.mailgun.org')));
    assert.ok(paras(providerIcon(d)).includes(REAL_EN.smtpDomainNote));
});

test('SMTP tab: the note about saving before the test is in the (i) of the test address, and the write-only note in the one of the password', async () => {
    const { d } = await openSmtp();
    const test = d.querySelector('#ucTestTo').closest('.inputContainer');
    assert.deepEqual(paras(test.querySelector('.ucHelp')), [REAL_EN.testHint]);
    assert.equal(test.querySelector('.fieldDescription'), null);
    assert.deepEqual(paras(passwordIcon(d)).slice(0, 1), [REAL_EN.secretHint]);
    assert.equal(passwordIcon(d).closest('.inputContainer').querySelector('.fieldDescription:not(.ucWarn)'), null);
});

test('SMTP provider help: a preset whose link is not https is left out, and nothing is read as HTML', async () => {
    const presets = [
        { ...PRESETS[0], id: 'evil', name: '<img src=x onerror=alert(1)>', docUrl: 'javascript:alert(1)' },
        { ...PRESETS[1], id: 'plain', name: 'Plain', docUrl: 'http://help.example.org/plain' },
        { ...PRESETS[2], name: '<b>Bold</b>' },
    ];
    const { d, errors } = await openSmtp({ presets });
    assert.deepEqual(providerOptions(d), [['', 'Custom'], ['smtp2go', '<b>Bold</b>']]);
    assert.equal(d.querySelector('#ucSmtpProvider b'), null);
    assert.equal(d.querySelector('#ucSmtpProvider img'), null);
    assert.deepEqual(errors, []);
});

test('SMTP provider list: changing to another host while a password is stored focuses the password field and asks to type it again', async () => {
    const { w, d } = await openSmtp({ config: { smtpPasswordSet: true, smtpHost: 'smtp.example' } });
    const line = d.querySelector('#ucSmtpRetypePassword');
    assert.equal(line.hidden, true);

    choose(w, d, 'brevo');

    assert.equal(d.activeElement, field(d, 'smtpPassword'));
    assert.equal(line.hidden, false);
    assert.equal(line.textContent, REAL_EN.smtpRetypePassword);
    assert.equal(posts(w).length, 0);

    choose(w, d, '');
    assert.equal(line.hidden, true);
});

test('SMTP provider list: no reminder when the host does not change, when no password is stored, or when one was typed', async () => {
    let { w, d } = await openSmtp({ config: { smtpPasswordSet: true, smtpHost: 'smtp-relay.brevo.com', smtpPort: 2525 } });
    choose(w, d, 'brevo');
    assert.equal(d.querySelector('#ucSmtpRetypePassword').hidden, true);

    ({ w, d } = await openSmtp({ config: { smtpPasswordSet: false } }));
    choose(w, d, 'brevo');
    assert.equal(d.querySelector('#ucSmtpRetypePassword').hidden, true);
    assert.notEqual(d.activeElement, field(d, 'smtpPassword'));

    ({ w, d } = await openSmtp({ config: { smtpPasswordSet: true } }));
    field(d, 'smtpPassword').value = 'not-a-real-password';
    choose(w, d, 'brevo');
    assert.equal(d.querySelector('#ucSmtpRetypePassword').hidden, true);
});

for (const [name, options] of [
    ['a 404', { presetsError: { status: 404 } }],
    ['a 500 with a JSON body', { presetsError: { status: 500, json: () => Promise.resolve({ code: 'x' }) } }],
    ['a network error', { presetsError: new TypeError('Failed to fetch') }],
    ['an answer that is not a list', { presets: { nope: true } }],
]) {
    test(`SMTP provider list: ${name} while loading the presets leaves Custom only and the page keeps working`, async () => {
        const { w, d, errors } = await openSmtp(options);
        assert.deepEqual(providerOptions(d), [['', 'Custom']]);
        assert.equal(d.querySelector('#ucSmtpProvider').value, '');
        assert.equal(field(d, 'smtpHost').value, 'smtp.example');
        assert.equal(d.querySelector('#ucBanner').hidden, true);
        assert.equal(d.querySelector('#ucError-smtp').hidden, true);
        submit(w, d, 'smtp');
        await wait();
        assert.equal(posts(w).length, 1);
        assert.equal(posts(w)[0].body.smtpHost, 'smtp.example');
        assert.deepEqual(errors, []);
    });
}

test('SMTP provider list: the list never reaches the saved settings', async () => {
    const { w, d } = await openSmtp({ config: { smtpHost: 'smtp-relay.brevo.com' } });
    submit(w, d, 'smtp');
    await wait();
    assert.deepEqual(Object.keys(posts(w)[0].body).sort(), ['fromAddress', 'fromName', 'smtpHost', 'smtpPort', 'smtpSecurity', 'smtpUsername']);
});

const suggestion = (d) => d.querySelector('#ucPublicUrlSuggested');

test('public URL: an empty stored URL is prefilled with the suggestion, with its help line and no request', async () => {
    const { w, d } = await openSmtp({ config: { publicUrl: '', suggestedPublicUrl: 'https://jellyfin.example.org' } });
    assert.equal(field(d, 'publicUrl').value, 'https://jellyfin.example.org');
    assert.equal(suggestion(d).hidden, false);
    assert.equal(suggestion(d).textContent, REAL_EN.publicUrlSuggested);
    assert.equal(posts(w).length, 0);
    assert.equal(w.__calls.filter((c) => c.method !== 'GET').length, 0);
});

test('public URL: a stored URL is kept and no suggestion appears', async () => {
    const { d } = await openSmtp({ config: { publicUrl: 'https://jf.example', suggestedPublicUrl: 'https://jellyfin.example.org' } });
    assert.equal(field(d, 'publicUrl').value, 'https://jf.example');
    assert.equal(suggestion(d).hidden, true);
});

for (const suggestedPublicUrl of [null, undefined, '']) {
    test(`public URL: no suggestion (${JSON.stringify(suggestedPublicUrl) ?? 'absent'}) leaves the field empty`, async () => {
        const { d } = await openSmtp({ config: { publicUrl: '', suggestedPublicUrl } });
        assert.equal(field(d, 'publicUrl').value, '');
        assert.equal(suggestion(d).hidden, true);
    });
}

test('public URL: the help line goes away once the administrator edits the prefilled value, and saving sends what is in the field', async () => {
    const { w, d } = await openSmtp({ config: { publicUrl: '', suggestedPublicUrl: 'https://jellyfin.example.org' } });
    field(d, 'publicUrl').value = 'https://other.example.org';
    field(d, 'publicUrl').dispatchEvent(new w.Event('input', { bubbles: true }));
    assert.equal(suggestion(d).hidden, true);

    submit(w, d, 'general');
    await wait();
    assert.equal(posts(w)[0].body.publicUrl, 'https://other.example.org');
});

for (const code of ['invalid_smtp_host', 'smtp_incomplete']) {
    test(`saving the SMTP tab: the ${code} refusal shows its text`, async () => {
        const { w, d } = await openSmtp({ saveError: { status: 400, json: () => Promise.resolve({ code }) } });
        submit(w, d, 'smtp');
        await wait();
        const error = d.querySelector('#ucError-smtp');
        assert.equal(error.hidden, false);
        assert.equal(error.textContent, REAL_EN[`err.${code}`]);
        assert.notEqual(error.textContent, REAL_EN.errGeneric);
    });
}

test('the provider texts exist in the four languages, brand names excepted', () => {
    const read = (l) => JSON.parse(readFileSync(here(`../../Jellyfin.Plugin.MailConcierge/Resources/Strings/${l}.json`), 'utf8'));
    const keys = ['smtpProvider', 'smtpProviderCustom', 'help.smtpProvider', 'smtpPresetDoc', 'smtpDomainNote', 'smtpRetypePassword', 'publicUrlSuggested'];
    for (const id of ['gmail', 'brevo', 'smtp2go', 'mailgun', 'mailjet']) { keys.push(`smtpPreset.${id}.user`, `smtpPreset.${id}.password`); }
    keys.push('smtpPreset.smtp2go.note', 'smtpPreset.mailgun.note');
    const en = read('en');
    for (const l of ['en', 'fr', 'es', 'de']) {
        for (const k of keys) { assert.ok(typeof read(l)['cfg.' + k] === 'string' && read(l)['cfg.' + k].trim(), `${l} cfg.${k}`); }
        if (l !== 'en') { for (const k of keys) { assert.notEqual(read(l)['cfg.' + k], en['cfg.' + k], `${l} cfg.${k} is translated`); } }
    }
    assert.equal(en['cfg.smtpProvider'], 'Provider');
    assert.equal(en['cfg.smtpProviderCustom'], 'Custom');
    assert.equal(en['cfg.smtpPresetDoc'], 'Official instructions');
});

// ---- Invitation history: table (wide screens) and cards (phones), filters, search ----
const REAL_INV_EN = (() => {
    const all = JSON.parse(readFileSync(here('../../Jellyfin.Plugin.MailConcierge/Resources/Strings/en.json'), 'utf8'));
    return Object.fromEntries(Object.entries(all).filter(([k]) => k.startsWith('inv.')));
})();
const OTTER_ID = '3f2a9c1e-0000-4000-8000-00000000a001';
const COLORS = { Pending: '#aa5cc3', Accepted: '#00a4dc', Expired: '#9e9e9e', Revoked: '#f44336' };
const ICONS = {
    Accepted: 'M9 16.17 4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z',
    Pending: 'M6 2v6h.01L6 8.01 10 12l-4 4 .01.01H6V22h12v-5.99h-.01L18 16l-4-4 4-3.99-.01-.01H18V2H6zm10 14.5V20H8v-3.5l4-4 4 4zm-4-5-4-4V4h8v3.5l-4 4z',
    Revoked: 'M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z',
    Expired: 'M11.99 2C6.47 2 2 6.48 2 12s4.47 10 9.99 10C17.52 22 22 17.52 22 12S17.52 2 11.99 2zM12 20c-4.42 0-8-3.58-8-8s3.58-8 8-8 8 3.58 8 8-3.58 8-8 8zm.5-13H11v6l5.25 3.15.75-1.23-4.5-2.67z',
};
const ZOO = [
    { id: 'z1', proposedUsername: 'otter', email: 'otter@example.org', state: 'Accepted', libraries: ['Films', 'Series'], liveTv: true, createdAt: '2026-11-01T08:00:00Z', expiresAt: '2026-11-10T08:00:00Z', acceptedAt: '2026-11-03T10:00:00Z', userId: OTTER_ID },
    { id: 'z2', proposedUsername: 'badger', email: null, state: 'Pending', libraries: [], liveTv: false, createdAt: '2026-11-10T08:00:00Z', expiresAt: '2026-11-25T08:00:00Z', acceptedAt: null, userId: null },
    { id: 'z3', proposedUsername: 'heron', email: 'heron@example.org', state: 'Expired', libraries: ['Films'], liveTv: null, createdAt: '2026-10-01T08:00:00Z', expiresAt: '2026-10-10T08:00:00Z', acceptedAt: null, userId: null },
    { id: 'z4', proposedUsername: 'mole', email: 'mole@example.org', state: 'Revoked', libraries: ['Series'], liveTv: false, createdAt: '2026-11-12T08:00:00Z', expiresAt: '2026-11-20T08:00:00Z', acceptedAt: null, userId: null },
    { id: 'z5', proposedUsername: 'fox', email: 'fox@example.org', state: 'Pending', libraries: ['Films'], liveTv: true, createdAt: '2026-11-14T08:00:00Z', expiresAt: '2026-11-28T08:00:00Z', acceptedAt: null, userId: null },
];
const openZoo = (invitations = ZOO, extra = {}) => open(INV_URL, { ...INV, invitations, strings: REAL_INV_EN, ...extra });
const tableRows = (d) => [...d.querySelectorAll('#ucInvTable tbody tr')];
const tableIds = (d) => tableRows(d).map((r) => r.getAttribute('data-id'));
const cardIds = (d) => [...d.querySelectorAll('#ucInvCards > article')].map((c) => c.getAttribute('data-id'));
const dateOf = (v) => new Date(v).toLocaleDateString('en');
const typeSearch = (w, d, text) => { const i = d.querySelector('#ucInvSearch'); i.value = text; i.dispatchEvent(new w.Event('input', { bubbles: true })); };
const filterButton = (d, i) => d.querySelectorAll('#ucInvFilters button')[i];

test('history table: the five column headers, one row per invitation, the cards beside it', async () => {
    const { d, errors } = await openZoo();
    assert.deepEqual([...d.querySelectorAll('#ucInvTable thead th')].map((th) => th.textContent), ['User', 'Status', 'Access', 'Date', 'Actions']);
    assert.equal(tableRows(d).length, 5);
    assert.equal(d.querySelectorAll('#ucInvCards > article').length, 5);
    const otter = d.querySelector('#ucInvTable tbody tr[data-id="z1"]');
    assert.equal(otter.querySelectorAll('td').length, 5);
    assert.match(otter.querySelector('td').textContent, /otter/);
    assert.match(otter.querySelector('td').textContent, /otter@example\.org/);
    assert.equal(d.querySelector('#ucInvEmpty').hidden, true);
    assert.deepEqual(errors, []);
});

test('history: an empty list shows the empty sentence and hides the table, the cards and the filters', async () => {
    const { d } = await openZoo([]);
    assert.equal(d.querySelector('#ucInvEmpty').hidden, false);
    assert.equal(d.querySelector('#ucInvHistory').hidden, true);
    assert.equal(tableRows(d).length, 0);
});

test('history: the table shows above 720 px and the cards below, with no horizontal scroll of the page', async () => {
    const { d } = await openZoo();
    const css = d.querySelector('#MailConciergeConfigPage > style').textContent;
    assert.match(css, /@media \(max-width: 720px\)/);
    const narrow = css.slice(css.indexOf('@media (max-width: 720px)'));
    assert.match(narrow, /\.ucInvWide\s*\{\s*display:\s*none/);
    assert.match(narrow, /\.ucInvNarrow\s*\{\s*display:\s*block/);
    assert.match(css, /\.ucInvNarrow\s*\{\s*display:\s*none/);
    assert.ok(d.querySelector('#ucInvTable').closest('.ucInvWide'));
    assert.ok(d.querySelector('#ucInvCards').classList.contains('ucInvNarrow'));
});

for (const state of STATES) {
    test(`history: the ${state} status label is the icon, then the word, in its colour (table and card)`, async () => {
        const { d } = await openZoo();
        const id = ZOO.find((i) => i.state === state).id;
        for (const root of [d.querySelector(`#ucInvTable tbody tr[data-id="${id}"]`), d.querySelector(`#ucInvCards > article[data-id="${id}"]`)]) {
            const label = root.querySelector('.ucInvStatus');
            assert.ok(label, state);
            assert.equal(label.firstElementChild.namespaceURI, 'http://www.w3.org/2000/svg');
            assert.equal(label.firstElementChild.localName, 'svg');
            assert.equal(label.firstElementChild.getAttribute('aria-hidden'), 'true');
            assert.equal(label.firstElementChild.getAttribute('viewBox'), '0 0 24 24');
            assert.equal(label.firstElementChild.getAttribute('fill'), 'currentColor');
            assert.equal(label.firstElementChild.querySelector('path').getAttribute('d'), ICONS[state]);
            assert.equal(label.lastElementChild.localName, 'span');
            assert.equal(label.lastElementChild.textContent, REAL_INV_EN[`inv.state.${state}`]);
            assert.equal(label.textContent, REAL_INV_EN[`inv.state.${state}`]);
            assert.equal(label.style.borderColor, hexToRgb(COLORS[state]));
            assert.equal(label.style.color, '');   // the word keeps the theme colour
            assert.equal(label.firstElementChild.style.color, hexToRgb(COLORS[state]));
        }
    });
}
function hexToRgb(h) { return `rgb(${parseInt(h.slice(1, 3), 16)}, ${parseInt(h.slice(3, 5), 16)}, ${parseInt(h.slice(5, 7), 16)})`; }

test('history: the name is a link to the profile only when the user id is known, with the encoded id', async () => {
    const odd = { ...ZOO[0], id: 'z9', proposedUsername: 'lynx', userId: 'a b/c?d' };
    const { d } = await openZoo([...ZOO, odd]);
    for (const root of ['#ucInvTable tbody tr', '#ucInvCards > article']) {
        const links = (id) => d.querySelectorAll(`${root}[data-id="${id}"] a`);
        assert.equal(links('z1').length, 1);
        assert.equal(links('z1')[0].getAttribute('href'), `#/dashboard/users/${OTTER_ID}/profile`);
        assert.equal(links('z1')[0].textContent, 'otter');
        assert.equal(links('z1')[0].title, 'Open the profile of otter');
        assert.equal(links('z9')[0].getAttribute('href'), '#/dashboard/users/a%20b%2Fc%3Fd/profile');
        for (const id of ['z2', 'z3', 'z4', 'z5']) { assert.equal(links(id).length, 0, id); }
    }
    // Without a link the name is still on the page, as plain text.
    assert.match(d.querySelector('#ucInvTable tbody tr[data-id="z2"] td').textContent, /badger/);
});

test('history: one pill per library, the Live TV pill only when liveTv is true, "None" when there is nothing', async () => {
    const { d } = await openZoo();
    const pills = (id) => [...d.querySelectorAll(`#ucInvTable tbody tr[data-id="${id}"] .ucInvPill`)].map((p) => p.textContent);
    assert.deepEqual(pills('z1'), ['Films', 'Series', 'Live TV']);
    assert.deepEqual(pills('z2'), []);
    assert.equal(d.querySelector('#ucInvTable tbody tr[data-id="z2"] .ucInvNone').textContent, 'None');
    assert.deepEqual(pills('z3'), ['Films']);   // liveTv null: unknown, no pill
    assert.deepEqual(pills('z4'), ['Series']);  // liveTv false
    assert.deepEqual(pills('z5'), ['Films', 'Live TV']);
    assert.equal(d.querySelector('#ucInvTable tbody tr[data-id="z1"] .ucInvPillTv').style.borderColor, hexToRgb('#00a4dc'));
    assert.equal(d.querySelector('#ucInvTable tbody tr[data-id="z1"] .ucInvPillTv').style.color, '');
    assert.equal(d.querySelector('#ucInvTable tbody tr[data-id="z3"] .ucInvNone'), null);
    const cardPills = [...d.querySelectorAll('#ucInvCards > article[data-id="z1"] .ucInvPill')].map((p) => p.textContent);
    assert.deepEqual(cardPills, ['Films', 'Series', 'Live TV']);
    // Only Live TV granted: no "None".
    const tvOnly = await openZoo([{ ...ZOO[1], id: 'z8', liveTv: true }]);
    assert.deepEqual([...tvOnly.d.querySelectorAll('#ucInvTable tbody tr .ucInvPill')].map((p) => p.textContent), ['Live TV']);
    assert.equal(tvOnly.d.querySelector('#ucInvTable tbody tr .ucInvNone'), null);
});

test('history: the date depends on the state, and an address-less invitation says so', async () => {
    const { d } = await openZoo();
    const date = (id) => d.querySelector(`#ucInvTable tbody tr[data-id="${id}"] .ucInvDate`).textContent;
    assert.equal(date('z1'), `Accepted on ${dateOf('2026-11-03T10:00:00Z')}`);
    assert.equal(date('z2'), `Expires on ${dateOf('2026-11-25T08:00:00Z')}`);
    assert.equal(date('z3'), `Expired on ${dateOf('2026-10-10T08:00:00Z')}`);
    assert.equal(date('z4'), 'Revoked');
    assert.equal(d.querySelector('#ucInvCards > article[data-id="z1"] .ucInvDate').textContent, date('z1'));
    assert.match(d.querySelector('#ucInvTable tbody tr[data-id="z2"] td').textContent, /Link to copy, no address/);
    assert.doesNotMatch(d.querySelector('#ucInvTable tbody tr[data-id="z1"] td').textContent, /Link to copy/);
});

test('history: an accepted invitation without acceptedAt, and a pending or expired one without expiresAt, show an em dash', async () => {
    const odd = [
        { ...ZOO[0], id: 'd1', acceptedAt: null },
        { ...ZOO[1], id: 'd2', expiresAt: null },
        { ...ZOO[2], id: 'd3', expiresAt: undefined },
    ];
    const { d } = await openZoo(odd);
    for (const id of ['d1', 'd2', 'd3']) {
        assert.equal(d.querySelector(`#ucInvTable tbody tr[data-id="${id}"] .ucInvDate`).textContent, '—', id);
        assert.equal(d.querySelector(`#ucInvCards > article[data-id="${id}"] .ucInvDate`).textContent, '—', id);
    }
});

test('history access: "None" only when liveTv is false, an em dash when it is unknown, the Live TV pill when it is true', async () => {
    const base = { ...ZOO[1], libraries: [] };
    const { d } = await openZoo([{ ...base, id: 'a1', liveTv: false }, { ...base, id: 'a2', liveTv: null }, { ...base, id: 'a3', liveTv: true }, { ...base, id: 'a4', liveTv: undefined }]);
    const part = (root, id) => d.querySelector(`${root}[data-id="${id}"] .ucInvAccess`);
    for (const root of ['#ucInvTable tbody tr', '#ucInvCards > article']) {
        assert.equal(part(root, 'a1').textContent, 'None');
        assert.equal(part(root, 'a2').textContent, '—');
        assert.equal(part(root, 'a4').textContent, '—');
        assert.equal(part(root, 'a2').querySelector('.ucInvPill'), null);
        assert.deepEqual([...part(root, 'a3').querySelectorAll('.ucInvPill')].map((p) => p.textContent), ['Live TV']);
        assert.equal(part(root, 'a3').querySelector('.ucInvNone'), null);
    }
});

test('history: a successful creation shows the new invitation whatever the filter and the search were', async () => {
    const { w, d } = await openZoo(ZOO, { created: { link: 'L', emailed: false, emailStatus: 'none' } });
    filterButton(d, 4).click();
    typeSearch(w, d, 'mole');
    assert.deepEqual(tableIds(d), ['z4']);
    d.querySelector('#ucInvLibraries input').checked = true;
    await sendInvitation(w, d);
    assert.equal(invPosts(w).length, 1);
    assert.equal(d.querySelector('#ucInvSearch').value, '');
    assert.deepEqual([...d.querySelectorAll('#ucInvFilters button')].map((b) => b.getAttribute('aria-pressed')), ['true', 'false', 'false', 'false', 'false']);
    assert.equal(tableIds(d).length, 5);
    assert.equal(cardIds(d).length, 5);
});

test('history: a failed creation leaves the filter and the search as they were', async () => {
    const { w, d } = await openZoo(ZOO, { createError: { status: 500, json: () => Promise.resolve({}) } });
    filterButton(d, 4).click();
    typeSearch(w, d, 'mole');
    d.querySelector('#ucInvLibraries input').checked = true;
    await sendInvitation(w, d);
    assert.equal(invPosts(w).length, 1);
    assert.equal(d.querySelector('#ucInvSearch').value, 'mole');
    assert.deepEqual(tableIds(d), ['z4']);
    assert.equal(filterButton(d, 4).getAttribute('aria-pressed'), 'true');
});

test('history: the filter group is named after the status column', async () => {
    const { d } = await openZoo(ZOO, { strings: { ...REAL_INV_EN, 'inv.col.status': 'STATUT' } });
    assert.equal(d.querySelector('#ucInvFilters').getAttribute('role'), 'group');
    assert.equal(d.querySelector('#ucInvFilters').getAttribute('aria-label'), 'STATUT');
    const plain = await openZoo();
    assert.equal(plain.d.querySelector('#ucInvFilters').getAttribute('aria-label'), 'Status');
});

test('history: an access pill wraps a long library name instead of overflowing a card', async () => {
    const { d } = await openZoo();
    const css = d.querySelector('#MailConciergeConfigPage > style').textContent;
    const rule = css.match(/\.ucInvPill\s*\{([^}]*)\}/)[1];
    assert.match(rule, /overflow-wrap:\s*anywhere/);
    assert.match(rule, /max-width:\s*100%/);
});

test('history: pending first, then the most recently created first', async () => {
    const { d } = await openZoo();
    assert.deepEqual(tableIds(d), ['z5', 'z2', 'z4', 'z1', 'z3']);
    assert.deepEqual(cardIds(d), ['z5', 'z2', 'z4', 'z1', 'z3']);
});

test('history filters: counters, pressed state, and filtering of both renderings', async () => {
    const { d } = await openZoo();
    const buttons = [...d.querySelectorAll('#ucInvFilters button')];
    assert.deepEqual(buttons.map((b) => b.textContent), ['All (5)', 'Pending (2)', 'Accepted (1)', 'Expired (1)', 'Revoked (1)']);
    assert.deepEqual(buttons.map((b) => b.getAttribute('aria-pressed')), ['true', 'false', 'false', 'false', 'false']);
    assert.ok(buttons.every((b) => b.type === 'button'));

    filterButton(d, 1).click();
    assert.deepEqual(tableIds(d), ['z5', 'z2']);
    assert.deepEqual(cardIds(d), ['z5', 'z2']);
    assert.deepEqual([...d.querySelectorAll('#ucInvFilters button')].map((b) => b.getAttribute('aria-pressed')), ['false', 'true', 'false', 'false', 'false']);

    filterButton(d, 4).click();
    assert.deepEqual(tableIds(d), ['z4']);
    filterButton(d, 0).click();
    assert.equal(tableIds(d).length, 5);
});

test('history filters: the focused filter button survives its own click (rebuilt in place)', async () => {
    const { w, d } = await openZoo();
    const b = filterButton(d, 2);
    b.focus();
    b.click();
    assert.equal(d.activeElement, b);
    assert.equal(w.document.querySelectorAll('#ucInvFilters button').length, 5);
});

test('history search: case-insensitive on the name and on the address, combined with the filter, and a message when nothing matches', async () => {
    const { w, d } = await openZoo();
    typeSearch(w, d, 'OTT');
    assert.deepEqual(tableIds(d), ['z1']);
    assert.deepEqual(cardIds(d), ['z1']);
    typeSearch(w, d, 'HERON@EXAMPLE');
    assert.deepEqual(tableIds(d), ['z3']);
    typeSearch(w, d, 'example.org');
    assert.equal(tableIds(d).length, 4);   // badger has no address
    filterButton(d, 1).click();
    assert.deepEqual(tableIds(d), ['z5']);
    assert.equal(d.querySelector('#ucInvNoMatch').hidden, true);
    typeSearch(w, d, 'zzz');
    assert.equal(tableIds(d).length, 0);
    assert.equal(d.querySelector('#ucInvNoMatch').hidden, false);
    assert.equal(d.querySelector('#ucInvNoMatch').textContent, 'No invitation matches.');
    typeSearch(w, d, '');
    assert.equal(d.querySelector('#ucInvNoMatch').hidden, true);
});

test('history: names and library names are shown as text, never as markup', async () => {
    const evil = { ...ZOO[0], id: 'z7', proposedUsername: '<b>x</b>', email: '<i>y</i>@example.org', libraries: ['"><img src=x onerror=1>'], userId: null };
    const { d } = await openZoo([evil]);
    const page = d.querySelector('#ucInvHistory');
    assert.equal(page.querySelector('b'), null);
    assert.equal(page.querySelector('i'), null);
    assert.equal(page.querySelector('img'), null);
    assert.match(d.querySelector('#ucInvTable tbody tr td').textContent, /<b>x<\/b>/);
    assert.ok([...d.querySelectorAll('#ucInvTable .ucInvPill')].some((p) => p.textContent === '"><img src=x onerror=1>'));
    assert.ok([...d.querySelectorAll('#ucInvCards .ucInvPill')].some((p) => p.textContent === '"><img src=x onerror=1>'));
});

test('history cards: same information and same buttons as the table rows', async () => {
    const { d } = await openZoo();
    for (const inv of ZOO) {
        const row = d.querySelector(`#ucInvTable tbody tr[data-id="${inv.id}"]`);
        const card = d.querySelector(`#ucInvCards > article[data-id="${inv.id}"]`);
        const words = (n) => n.textContent.replace(/\s+/g, ' ').trim();
        assert.equal(words(card), words(row), inv.id);
        assert.deepEqual([...card.querySelectorAll('button')].map((b) => b.getAttribute('aria-label') || b.textContent), [...row.querySelectorAll('button')].map((b) => b.getAttribute('aria-label') || b.textContent), inv.id);
    }
});

test('history buttons: a card button sends the same requests as the table button', async () => {
    const { w, d } = await openZoo(ZOO, { regenerated: { link: 'L', emailed: false, emailStatus: 'none' } });
    const asked = [];
    w.confirm = (q) => { asked.push(q); return true; };
    const card = (id) => d.querySelector(`#ucInvCards > article[data-id="${id}"]`);
    const named = (root, text) => [...root.querySelectorAll('button')].find((b) => b.textContent === text);

    named(card('z2'), 'New link').click();
    await wait();
    assert.deepEqual(w.__calls.filter((c) => c.method === 'POST' && c.url.endsWith('/Regenerate')).map((c) => c.url), ['MailConcierge/Invitations/z2/Regenerate']);

    named(card('z2'), 'Revoke').click();
    await wait();
    assert.deepEqual(asked, ['Revoke this invitation?']);
    assert.deepEqual(invDeletes(w).map((c) => c.url), ['MailConcierge/Invitations/z2']);

    card('z1').querySelector('button[is="paper-icon-button-light"]').click();
    await wait();
    assert.deepEqual(invDeletes(w).map((c) => c.url), ['MailConcierge/Invitations/z2', 'MailConcierge/Invitations/z1?erase=true']);
    assert.deepEqual(asked, ['Revoke this invitation?', 'Erase this line from the history? This cannot be undone.']);   // an accepted invitation is erased after its own confirmation
});

test('history buttons: only pending and expired invitations get New link and Revoke; the table keeps its confirmation', async () => {
    const { w, d } = await openZoo();
    const labels = (id) => [...d.querySelectorAll(`#ucInvTable tbody tr[data-id="${id}"] button:not([is="paper-icon-button-light"])`)].map((b) => b.textContent);
    assert.deepEqual(labels('z5'), ['New link', 'Revoke']);
    assert.deepEqual(labels('z3'), ['New link', 'Revoke']);
    assert.deepEqual(labels('z1'), []);
    assert.deepEqual(labels('z4'), []);
    let asked = 0;
    w.confirm = () => { asked++; return false; };
    [...d.querySelectorAll('#ucInvTable tbody tr[data-id="z5"] button')].find((b) => b.textContent === 'Revoke').click();
    await wait();
    assert.equal(asked, 1);
    assert.equal(invDeletes(w).length, 0);
});

test('history: a state the page does not know, and an answer without the new fields, still render', async () => {
    const old = [
        { id: 'o1', proposedUsername: 'newt', email: 'newt@example.org', state: 'Pending', expiresAt: '2026-11-20T00:00:00Z' },
        { id: 'o2', proposedUsername: 'vole', email: null, state: 'Archived', expiresAt: '2026-11-20T00:00:00Z', createdAt: '2026-11-01T00:00:00Z', libraries: null },
        { id: 'o3', proposedUsername: 'wren', email: null, state: 'Accepted', expiresAt: null },
    ];
    const { d, errors } = await openZoo(old);
    assert.deepEqual(tableIds(d).sort(), ['o1', 'o2', 'o3']);
    assert.equal(d.querySelector('#ucInvTable tbody tr[data-id="o2"] .ucInvStatus').textContent, 'Archived');
    assert.equal(d.querySelector('#ucInvTable tbody tr[data-id="o2"] a'), null);
    assert.equal(d.querySelector('#ucInvTable tbody tr[data-id="o1"] .ucInvNone').textContent, '\u2014');   // access unknown: older invitation
    assert.equal(d.querySelector('#ucInvTable tbody tr[data-id="o3"] .ucInvDate').textContent, '\u2014');   // accepted, no acceptedAt
    assert.equal(d.querySelector('#ucInvTable tbody tr[data-id="o1"] .ucInvDate').textContent, `Expires on ${dateOf('2026-11-20T00:00:00Z')}`);
    assert.equal(d.querySelector('#ucInvTable tbody tr[data-id="o2"] .ucInvDate').textContent, '');           // unknown state
    assert.deepEqual([...d.querySelectorAll('#ucInvFilters button')].map((b) => b.textContent), ['All (3)', 'Pending (1)', 'Accepted (1)', 'Expired (0)', 'Revoked (0)']);
    assert.deepEqual(errors, []);
});

test('history texts: every new key exists in the four languages, and the English ones are the fixed copy', () => {
    const read = (l) => JSON.parse(readFileSync(here(`../../Jellyfin.Plugin.MailConcierge/Resources/Strings/${l}.json`), 'utf8'));
    const fixed = {
        'inv.col.user': 'User', 'inv.col.status': 'Status', 'inv.col.access': 'Access', 'inv.col.date': 'Date', 'inv.col.actions': 'Actions',
        'inv.filter.all': 'All', 'inv.search': 'Search a name or address', 'inv.noAccess': 'None', 'inv.noEmail': 'Link to copy, no address',
        'inv.date.accepted': 'Accepted on {0}', 'inv.date.pending': 'Expires on {0}', 'inv.date.expired': 'Expired on {0}', 'inv.date.revoked': 'Revoked',
        'inv.openProfile': 'Open the profile of {0}', 'inv.noMatch': 'No invitation matches.',
    };
    assert.deepEqual(Object.fromEntries(Object.keys(fixed).map((k) => [k, read('en')[k]])), fixed);
    for (const l of ['fr', 'es', 'de']) {
        for (const k of Object.keys(fixed)) { assert.ok(read(l)[k], `${l} ${k}`); }
        for (const k of ['inv.date.accepted', 'inv.date.pending', 'inv.date.expired', 'inv.openProfile']) { assert.ok(read(l)[k].includes('{0}'), `${l} ${k}`); }
    }
});

test('history texts: fr, es and de are translated, not copies of the English text', () => {
    const read = (l) => JSON.parse(readFileSync(here(`../../Jellyfin.Plugin.MailConcierge/Resources/Strings/${l}.json`), 'utf8'));
    const en = read('en');
    const keys = Object.keys(en).filter((k) => k.startsWith('inv.col.') || k.startsWith('inv.date.') || ['inv.filter.all', 'inv.search', 'inv.noAccess', 'inv.noEmail', 'inv.openProfile', 'inv.noMatch'].includes(k));
    assert.ok(keys.length >= 15);
    // The same word in the other language is legitimate for these (a word that is spelled alike), and only these.
    const alike = { fr: ['inv.col.date', 'inv.col.actions'], es: [], de: ['inv.col.status'] };
    for (const l of ['fr', 'es', 'de']) {
        const texts = read(l);
        for (const k of keys) {
            if (alike[l].includes(k)) { assert.equal(texts[k], en[k], `${l} ${k} is expected to be spelled as in English`); } else { assert.notEqual(texts[k], en[k], `${l} ${k} is still the English text`); }
        }
    }
});

test('history texts: the key inv.until is gone from the four languages and from the page', () => {
    for (const l of ['en', 'fr', 'es', 'de']) {
        assert.equal('inv.until' in JSON.parse(readFileSync(here(`../../Jellyfin.Plugin.MailConcierge/Resources/Strings/${l}.json`), 'utf8')), false, l);
    }
    assert.doesNotMatch(html, /inv\.until/);
});

test('history: without any server text the page still shows the English fallback labels', async () => {
    const { d } = await open(INV_URL, { ...INV, invitations: ZOO });
    assert.deepEqual([...d.querySelectorAll('#ucInvTable thead th')].map((th) => th.textContent), ['User', 'Status', 'Access', 'Date', 'Actions']);
    assert.equal(d.querySelector('#ucInvFilters button').textContent, 'All (5)');
});

test('history: the page never writes markup (no innerHTML on the history)', () => {
    const script = html.slice(html.indexOf('function loadInvList') - 6000, html.indexOf('function loadInvitations'));
    assert.doesNotMatch(script, /innerHTML|insertAdjacentHTML|outerHTML/);
});

// ---- Daily e-mail limits (General tab) and e-mails of each account (Accounts tab).

const PAGE = 'http://jf.test/web/#/configurationpage?name=MailConcierge';
const STAT_STRINGS = {
    quotaLine: 'QUOTA {0} OF {1} PER {2}', quotaBlocked: 'BLOCKED {0}', quotaReached: 'REACHED {0}',
    statsSince: 'SINCE {0}', statsInvitations: 'INVITATIONS {0} ok {1} ko', statsReceived: 'SENT {0} LAST {1}', statsFailed: 'FAILED {0}',
    statsNone: 'NONE YET', statsUnavailable: 'UNAVAILABLE',
};
const QUOTA = { used: 42, limit: 200, perRecipientLimit: 10, windowHours: 24, limitReachedAt: null, recipientsBlocked: 0 };
const statsOf = (users, invitations = { sent: 0, failed: 0 }) => ({ since: '2026-11-20T08:00:00+00:00', invitations, users });

test('quota: the General tab shows the two daily limits and the address to warn, with the stored values', async () => {
    const { d, errors } = await open(PAGE, { strings: STAT_STRINGS });
    assert.equal(d.querySelector('[data-f="maxEmailsPerRecipientPerDay"]').value, '10');
    assert.equal(d.querySelector('[data-f="maxEmailsPerDay"]').value, '200');
    assert.equal(d.querySelector('[data-f="adminNotificationAddress"]').value, 'admin@example.org');
    assert.equal(d.querySelector('[data-f="adminNotificationAddress"]').type, 'email');
    for (const key of ['maxEmailsPerRecipientPerDay', 'maxEmailsPerDay', 'adminNotificationAddress']) {
        assert.ok(d.querySelector('#ucTab-general').contains(d.querySelector(`[data-f="${key}"]`)), key);
        assert.ok(d.querySelector(`[data-f="${key}"]`).closest('.inputContainer').querySelector('.ucHelpRow [data-help]'), `${key} has its (i)`);
    }
    for (const gone of ['maxRecoveryEmailsPerHour', 'maxInvitationEmailsPerHour', 'maxAccountEmailsPerHour', 'maxRecoveryEmailsPerAccountPerHour']) {
        assert.equal(d.querySelector(`[data-f="${gone}"]`), null, gone);
    }
    assert.deepEqual(errors, []);
});

test('quota: saving the General tab sends the two limits and the address, and nothing of the old hourly settings', async () => {
    const { w, d } = await open(PAGE, { strings: STAT_STRINGS });
    d.querySelector('[data-f="maxEmailsPerRecipientPerDay"]').value = '12';
    d.querySelector('[data-f="maxEmailsPerDay"]').value = '300';
    d.querySelector('[data-f="adminNotificationAddress"]').value = '';
    submit(w, d, 'general');
    await wait();
    const body = posts(w).at(-1).body;
    assert.equal(body.maxEmailsPerRecipientPerDay, 12);
    assert.equal(body.maxEmailsPerDay, 300);
    assert.equal(body.adminNotificationAddress, '');
    assert.ok(!Object.keys(body).some((k) => /PerHour/.test(k)), Object.keys(body).join());
});

test('quota: a server refusal of the address is told in words', async () => {
    const { w, d } = await open(PAGE, { strings: { 'err.invalid_admin_address': 'BAD ADDRESS' }, saveError: { json: () => Promise.resolve({ code: 'invalid_admin_address' }) } });
    submit(w, d, 'general');
    await wait();
    assert.equal(d.querySelector('#ucError-general').textContent, 'BAD ADDRESS');
});

test('quota: the line "used of limit" and the limit of one recipient, after the limit fields, written as text', async () => {
    const { d, errors } = await open(PAGE, { strings: STAT_STRINGS, status: { emailQuota: QUOTA } });
    const section = d.querySelector('#ucQuota');
    assert.equal(section.hidden, false);
    assert.ok(d.querySelector('#ucTab-general').contains(section));
    assert.ok(d.querySelector('[data-f="maxPinFailures"]').compareDocumentPosition(section) & 4, 'after the limit fields');
    assert.equal(d.querySelector('#ucQuotaLine').textContent, 'QUOTA 42 OF 200 PER 10');
    assert.equal(d.querySelector('#ucQuotaBlocked').hidden, true);
    assert.equal(d.querySelector('#ucQuotaBanner').hidden, true);
    assert.deepEqual(errors, []);
});

test('quota: a limit that stopped an e-mail shows the banner, with the date, and the number of recipients at their limit', async () => {
    const at = '2026-11-21T09:30:00+00:00';
    const { d } = await open(PAGE, { strings: STAT_STRINGS, status: { emailQuota: { ...QUOTA, used: 200, limitReachedAt: at, recipientsBlocked: 3 } } });
    const banner = d.querySelector('#ucQuotaBanner');
    assert.equal(banner.hidden, false);
    assert.ok(banner.classList.contains('ucBoxWarn'));
    assert.equal(banner.getAttribute('role'), 'status');
    assert.equal(banner.textContent, 'REACHED ' + new Date(at).toLocaleString());
    assert.ok(d.querySelector('#ucTab-general').contains(banner));
    assert.equal(d.querySelector('#ucQuotaBlocked').hidden, false);
    assert.equal(d.querySelector('#ucQuotaBlocked').textContent, 'BLOCKED 3');
});

test('quota: shown again as text when the data is written with markup characters, never as markup', async () => {
    const { d } = await open(PAGE, { strings: STAT_STRINGS, status: { emailQuota: { ...QUOTA, limitReachedAt: '<img src=x onerror=alert(1)>' } } });
    assert.equal(d.querySelector('#ucQuotaBanner').hidden, true);
    assert.equal(d.querySelector('#ucQuota img'), null);
});

test('quota: rebuilt at each Status refresh (after a save), and hidden when the server sends none or malformed data', async () => {
    let status = { emailQuota: QUOTA };
    const { w, d, errors } = await open(PAGE, { strings: STAT_STRINGS, status: () => status });
    assert.equal(d.querySelector('#ucQuotaLine').textContent, 'QUOTA 42 OF 200 PER 10');
    status = { emailQuota: { ...QUOTA, used: 43 } };
    submit(w, d, 'general');
    await wait();
    assert.equal(d.querySelector('#ucQuotaLine').textContent, 'QUOTA 43 OF 200 PER 10');
    for (const bad of [{}, { emailQuota: null }, { emailQuota: { used: '5', limit: 10, perRecipientLimit: 3 } }, { emailQuota: { used: 1, limit: null, perRecipientLimit: 3 } }, { emailQuota: { used: Infinity, limit: 5, perRecipientLimit: 3 } }]) {
        status = bad;
        await reopenGeneral(d);
        assert.equal(d.querySelector('#ucQuota').hidden, true, JSON.stringify(bad));
        assert.equal(d.querySelector('#ucQuotaBanner').hidden, true);
    }
    assert.deepEqual(errors, []);
});

test('quota: read again each time the General tab opens, and a tab opened during a request joins it', async () => {
    let used = 0;
    const { w, d } = await open(PAGE, { strings: STAT_STRINGS, status: () => ({ emailQuota: { ...QUOTA, used: ++used } }) });
    const statusCalls = () => w.__calls.filter((c) => c.url === 'MailConcierge/Admin/Status').length;
    assert.equal(statusCalls(), 1, 'one request at page load, not two');
    assert.equal(d.querySelector('#ucQuotaLine').textContent, 'QUOTA 1 OF 200 PER 10');
    await reopenGeneral(d);
    assert.equal(statusCalls(), 2);
    assert.equal(d.querySelector('#ucQuotaLine').textContent, 'QUOTA 2 OF 200 PER 10');
    // Opening the tab again while the request is still running does not send another one.
    d.querySelector('#ucTabBtn-smtp').click();
    d.querySelector('#ucTabBtn-general').click();
    d.querySelector('#ucTabBtn-smtp').click();
    d.querySelector('#ucTabBtn-general').click();
    await wait();
    assert.equal(statusCalls(), 3);
    // Other tabs do not read it.
    d.querySelector('#ucTabBtn-accounts').click();
    d.querySelector('#ucTabBtn-smtp').click();
    await wait();
    assert.equal(statusCalls(), 3);
});

test('stats: nothing is requested at page load; the Accounts tab loads them once each time it opens', async () => {
    const { w, d } = await open(PAGE, { strings: STAT_STRINGS });
    const statCalls = () => w.__calls.filter((c) => c.url === 'MailConcierge/Admin/EmailStats').length;
    assert.equal(statCalls(), 0);
    d.querySelector('#ucTabBtn-accounts').click();
    await wait();
    assert.equal(statCalls(), 1);
    d.querySelector('#ucTabBtn-general').click();
    assert.equal(statCalls(), 1);
    d.querySelector('#ucTabBtn-accounts').click();
    await wait();
    assert.equal(statCalls(), 2);
});

test('stats: a known account shows its name, count and last date in the browser locale; failed only when above 0; invitation totals when there are some', async () => {
    const last = '2026-11-21T09:30:00+00:00';
    const { w, d, errors } = await open(PAGE, { strings: STAT_STRINGS, session: { 'mailconcierge.cfgTab': 'accounts' }, emailStats: statsOf([
        { userId: '11111111-1111-1111-1111-111111111111', sent: 4, failed: 0, lastSentAt: last },
        { userId: '22222222-2222-2222-2222-222222222222', sent: 1, failed: 2, lastSentAt: last },
    ], { sent: 3, failed: 1 }) });
    assert.equal(d.querySelector('#ucStatsInvitations').hidden, false);
    assert.equal(d.querySelector('#ucStatsInvitations').textContent, 'INVITATIONS 3 ok 1 ko');
    const rows = [...d.querySelectorAll('#ucStatsList li')];
    assert.equal(rows.length, 2);
    const date = new w.Date(last).toLocaleString();
    assert.equal(rows[0].querySelector('.ucStatName').textContent, 'alice');
    assert.equal(rows[0].querySelector('.ucStatSent').textContent, `SENT 4 LAST ${date}`);
    assert.equal(rows[0].querySelector('.ucStatFailed'), null);
    assert.equal(rows[1].querySelector('.ucStatFailed').textContent, 'FAILED 2');
    assert.equal(d.querySelector('#ucStatsSince').textContent, `SINCE ${new w.Date('2026-11-20T08:00:00+00:00').toLocaleString()}`);
    assert.equal(d.querySelector('#ucStatsNone').hidden, true);
    assert.equal(d.querySelector('#ucStatsUnavailable').hidden, true);
    assert.deepEqual(errors, []);
});

test('stats: an id without an account is skipped and never shown; names are written as text', async () => {
    const { d } = await open(PAGE, { strings: STAT_STRINGS, session: { 'mailconcierge.cfgTab': 'accounts' }, users: [{ Id: '11111111111111111111111111111111', Name: '<b>x</b>' }], emailStats: statsOf([
        { userId: '99999999-9999-9999-9999-999999999999', sent: 7, failed: 0, lastSentAt: '2026-11-21T09:30:00+00:00' },
        { userId: '11111111-1111-1111-1111-111111111111', sent: 1, failed: 0, lastSentAt: '2026-11-21T09:30:00+00:00' },
    ]) });
    const rows = [...d.querySelectorAll('#ucStatsList li')];
    assert.equal(rows.length, 1);
    assert.equal(rows[0].querySelector('.ucStatName').textContent, '<b>x</b>');
    assert.equal(rows[0].querySelector('b'), null);
    assert.ok(!d.querySelector('#ucStatsList').textContent.includes('9999'));
});

test('stats: empty shows "none yet" and no totals line, also when every id is skipped', async () => {
    const empty = await open(PAGE, { strings: STAT_STRINGS, session: { 'mailconcierge.cfgTab': 'accounts' }, emailStats: statsOf([]) });
    assert.equal(empty.d.querySelector('#ucStatsInvitations').hidden, true, 'no totals line without invitation e-mails');
    assert.equal(empty.d.querySelector('#ucStatsNone').hidden, false);
    assert.equal(empty.d.querySelector('#ucStatsNone').textContent, 'NONE YET');
    assert.equal(empty.d.querySelectorAll('#ucStatsList li').length, 0);
    const skipped = await open(PAGE, { strings: STAT_STRINGS, session: { 'mailconcierge.cfgTab': 'accounts' }, emailStats: statsOf([{ userId: '99999999-9999-9999-9999-999999999999', sent: 1, failed: 0, lastSentAt: null }]) });
    assert.equal(skipped.d.querySelector('#ucStatsNone').hidden, false);
});

test('stats: a failed request shows "unavailable" and nothing else, without breaking the tab', async () => {
    const { d, errors } = await open(PAGE, { strings: STAT_STRINGS, session: { 'mailconcierge.cfgTab': 'accounts' }, emailStatsError: { status: 500 } });
    assert.equal(d.querySelector('#ucStatsUnavailable').hidden, false);
    assert.equal(d.querySelector('#ucStatsUnavailable').textContent, 'UNAVAILABLE');
    assert.equal(d.querySelector('#ucStatsNone').hidden, true);
    assert.equal(d.querySelector('#ucStatsList').hidden, true);
    assert.equal(d.querySelector('#ucStatsSince').hidden, true);
    assert.equal(d.querySelector('#ucAssign').disabled, false);
    assert.deepEqual(errors, []);
});

test('stats: a failed user list also shows "unavailable"; a later success clears it', async () => {
    const { w, d } = await open(PAGE, { strings: STAT_STRINGS, session: { 'mailconcierge.cfgTab': 'accounts' }, emailStats: statsOf([]) });
    const original = w.ApiClient.ajax;
    w.ApiClient.ajax = (req) => (req.url === 'Users' ? Promise.reject({ status: 500 }) : original(req));
    d.querySelector('#ucTabBtn-general').click();
    d.querySelector('#ucTabBtn-accounts').click();
    await wait();
    assert.equal(d.querySelector('#ucStatsUnavailable').hidden, false);
    w.ApiClient.ajax = original;
    d.querySelector('#ucTabBtn-general').click();
    d.querySelector('#ucTabBtn-accounts').click();
    await wait();
    assert.equal(d.querySelector('#ucStatsUnavailable').hidden, true);
    assert.equal(d.querySelector('#ucStatsNone').hidden, false);
});

test('stats: an answer of the wrong shape shows "unavailable", never an empty list', async () => {
    const good = statsOf([{ userId: '11111111-1111-1111-1111-111111111111', sent: 1, failed: 0, lastSentAt: '2026-11-21T09:30:00+00:00' }]);
    let answer = {};
    const { w, d, errors } = await open(PAGE, { strings: STAT_STRINGS, session: { 'mailconcierge.cfgTab': 'accounts' }, emailStats: good });
    const original = w.ApiClient.ajax;
    w.ApiClient.ajax = (req) => (req.url === 'MailConcierge/Admin/EmailStats' ? Promise.resolve(answer) : original(req));
    const reopen = async () => { d.querySelector('#ucTabBtn-general').click(); d.querySelector('#ucTabBtn-accounts').click(); await wait(); };
    const shapes = {
        'empty object': {},
        'null': null,
        'text': 'oops',
        'users is not a list': { ...good, users: {} },
        'users missing': { since: good.since, invitations: good.invitations },
        'invitations missing': { since: good.since, users: [] },
        'invitations is a list': { ...good, invitations: [] },
        'sent is text': { ...good, invitations: { sent: '3', failed: 0 } },
        'failed is missing': { ...good, invitations: { sent: 3 } },
        'since missing': { invitations: good.invitations, users: [] },
        'since is not a date': { ...good, since: 'yesterday' },
        'since is a number': { ...good, since: 5 },
    };
    for (const [name, shape] of Object.entries(shapes)) {
        answer = shape;
        await reopen();
        assert.equal(d.querySelector('#ucStatsUnavailable').hidden, false, name);
        assert.equal(d.querySelector('#ucStatsNone').hidden, true, name);
        assert.equal(d.querySelector('#ucStatsSince').hidden, true, name);
        assert.equal(d.querySelector('#ucStatsList').hidden, true, name);
    }
    answer = good;
    await reopen();
    assert.equal(d.querySelector('#ucStatsUnavailable').hidden, true);
    assert.equal(d.querySelectorAll('#ucStatsList li').length, 1);
    assert.deepEqual(errors, []);
});

test('stats: a late answer to an older request is dropped, whether it succeeds or fails', async () => {
    const { w, d } = await open(PAGE, { strings: STAT_STRINGS, session: { 'mailconcierge.cfgTab': 'accounts' }, emailStats: statsOf([]) });
    const original = w.ApiClient.ajax;
    const held = [];
    let hold = true;
    w.ApiClient.ajax = (req) => {
        if (hold && req.url === 'MailConcierge/Admin/EmailStats') { return new Promise((resolve, reject) => held.push({ resolve, reject })); }
        return original(req);
    };
    const reopen = () => { d.querySelector('#ucTabBtn-general').click(); d.querySelector('#ucTabBtn-accounts').click(); };
    reopen();
    await wait();
    hold = false;
    reopen();
    await wait();
    assert.equal(held.length, 1);
    assert.equal(d.querySelector('#ucStatsNone').hidden, false);
    // The first request answers after the second one: its data must not replace the newer view.
    held[0].resolve(statsOf([{ userId: '11111111-1111-1111-1111-111111111111', sent: 9, failed: 0, lastSentAt: null }]));
    await wait();
    assert.equal(d.querySelectorAll('#ucStatsList li').length, 0);
    assert.equal(d.querySelector('#ucStatsNone').hidden, false);
    // Same for a late failure.
    hold = true;
    reopen();
    await wait();
    hold = false;
    reopen();
    await wait();
    held[1].reject({ status: 500 });
    await wait();
    assert.equal(d.querySelector('#ucStatsUnavailable').hidden, true);
    assert.equal(d.querySelector('#ucStatsNone').hidden, false);
});

test('stats and quota: the page never writes data as markup', () => {
    const script = html.slice(html.indexOf('<script type="text/javascript">'));
    assert.doesNotMatch(script, /innerHTML|insertAdjacentHTML|outerHTML/);
});

test('stats and quota: the shipped English texts exist and use the page placeholders', async () => {
    for (const key of ['quotaTitle', 'quotaLine', 'quotaBlocked', 'quotaReached', 'maxEmailsPerRecipient', 'maxEmailsPerDay', 'adminNotificationAddress', 'help.maxEmailsPerRecipient', 'help.maxEmailsPerDay', 'help.adminNotificationAddress', 'err.invalid_admin_address', 'statsTitle', 'statsSince', 'statsInvitations', 'statsReceived', 'statsFailed', 'statsNone', 'statsUnavailable']) {
        assert.equal(typeof REAL_EN[key], 'string', key);
    }
    assert.match(REAL_EN.statsSince, /\{0\}/);
    assert.match(REAL_EN.statsInvitations, /\{0\}.*\{1\}/);
    assert.match(REAL_EN.statsReceived, /\{0\}.*\{1\}/);
    assert.match(REAL_EN.statsFailed, /\{0\}/);
});

test('stats and quota: every shipped language keeps the same {0}/{1} placeholders in the cfg texts of this lot', () => {
    const keys = ['quotaTitle', 'quotaLine', 'quotaBlocked', 'quotaReached', 'maxEmailsPerRecipient', 'maxEmailsPerDay', 'adminNotificationAddress', 'help.maxEmailsPerRecipient', 'help.maxEmailsPerDay', 'help.adminNotificationAddress', 'err.invalid_admin_address', 'statsTitle', 'statsSince', 'statsInvitations', 'statsReceived', 'statsFailed', 'statsNone', 'statsUnavailable'];
    const read = (l) => JSON.parse(readFileSync(here(`../../Jellyfin.Plugin.MailConcierge/Resources/Strings/${l}.json`), 'utf8'));
    const placeholders = (text) => [...new Set(text.match(/\{\d\}/g) || [])].sort().join(',');
    const en = read('en');
    for (const l of ['fr', 'de', 'es']) {
        const other = read(l);
        for (const k of keys) {
            assert.equal(typeof other['cfg.' + k], 'string', `${l} cfg.${k}`);
            assert.equal(placeholders(other['cfg.' + k]), placeholders(en['cfg.' + k]), `${l} cfg.${k}`);
        }
    }
    assert.equal(placeholders(en['cfg.statsReceived']), '{0},{1}');
    assert.equal(placeholders(en['cfg.statsFailed']), '{0}');
    assert.equal(placeholders(en['cfg.quotaLine']), '{0},{1},{2}');
    assert.equal(placeholders(en['cfg.quotaReached']), '{0}');
});

// ---- Final fix wave: first-install banner, theme-safe colours, confirmations, texts.

test('banner: a plugin that is turned off (and not because of the proxy guard) shows the "turned off" banner, with its text', async () => {
    const { d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { status: { serviceOpen: false }, strings: { disabledBanner: 'TURNED OFF' } });
    const banner = d.querySelector('#ucDisabledBanner');
    assert.equal(banner.hidden, false);
    assert.equal(banner.textContent, 'TURNED OFF');
    assert.equal(banner.getAttribute('role'), 'status');
    assert.equal(d.querySelector('#ucBanner').hidden, true);
});

for (const [name, status] of [['open', { serviceOpen: true }], ['not reported', {}], ['closed by the proxy guard', { serviceOpen: false, proxyGuardTripped: true, reason: 'unknown_proxy' }]]) {
    test(`banner: the "turned off" banner stays hidden when the service is ${name}`, async () => {
        const { d } = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { status, strings: { disabledBanner: 'TURNED OFF' } });
        assert.equal(d.querySelector('#ucDisabledBanner').hidden, true);
    });
}

test('colours: no fixed pastel text colour is left on the page (it must read on light and dark themes)', () => {
    assert.doesNotMatch(html, /#ffb74d|#ffd180|#ff8a80/i);
    for (const id of ['ucBanner', 'ucDisabledBanner', 'ucMailBanner']) {
        assert.match(html, new RegExp(`id="${id}" class="ucBox ucBox(Err|Warn)"`));
    }
    assert.match(html, /\.ucBox \{[^}]*color: inherit/);
    assert.match(html, /\.ucBoxWarn \{[^}]*rgba\(/);
});

test('Live TV: the explanation is in the (i) next to the box, which shows with the box only', async () => {
    const on = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { session: { 'mailconcierge.cfgTab': 'invitations' }, liveTv: { available: true }, strings: { 'inv.liveTvHint': 'TV HINT' } });
    const row = on.d.querySelector('#ucInvLiveTvRow');
    assert.notEqual(row.style.display, 'none');
    assert.ok(row.contains(on.d.querySelector('#ucInvLiveTvHint')));
    assert.equal(paras(on.d.querySelector('#ucInvLiveTvHint')).join(), 'TV HINT');
    assert.equal(on.d.querySelector('#ucInvLiveTvHint').getAttribute('aria-label'), 'TV HINT');
    assert.equal(on.d.querySelector('#ucInvLiveTvHint').closest('label'), null, 'the icon is outside the label of the box');
    const off = await open('http://jf.test/web/#/configurationpage?name=MailConcierge', { session: { 'mailconcierge.cfgTab': 'invitations' }, liveTv: { available: false }, strings: { 'inv.liveTvHint': 'TV HINT' } });
    assert.equal(off.d.querySelector('#ucInvLiveTvRow').style.display, 'none');
});

test('texts: the fix-wave keys exist in the four languages, the template hint names real placeholders, the refusal says what to do', () => {
    const read = (l) => JSON.parse(readFileSync(here(`../../Jellyfin.Plugin.MailConcierge/Resources/Strings/${l}.json`), 'utf8'));
    const en = read('en');
    for (const l of ['en', 'fr', 'de', 'es']) {
        const strings = read(l);
        for (const k of ['cfg.disabledBanner', 'cfg.enabledHint', 'inv.liveTvHint', 'inv.confirmErase', 'inv.historyHint', 'page.accept.usernameRules']) {
            assert.ok(typeof strings[k] === 'string' && strings[k].trim(), `${l} ${k}`);
            if (l !== 'en') { assert.notEqual(strings[k], en[k], `${l} ${k} is translated`); }
        }
        assert.match(strings['cfg.templatesHint'], /\{\{username\}\}/, l);
        assert.match(strings['cfg.templatesHint'], /\{\{link\}\}/, l);
        assert.doesNotMatch(strings['cfg.templatesHint'], /\{\{(name|nom)\}\}/, l);
    }
    assert.match(en['inv.errServiceClosed'], /Enable Mail Concierge/);
    assert.match(en['cfg.err.smtp_incomplete'], /SMTP tab/);
    assert.doesNotMatch(en['cfg.err.smtp_incomplete'], /turn the plugin off/);
});

test('the "turned off" banner and the erase confirmation are in the English fallback of the page', () => {
    assert.match(html, /disabledBanner: 'Mail Concierge is turned off/);
    assert.match(html, /'inv\.confirmErase': 'Erase this line/);
});

// ---- Help in (i) icons, and the links of the page ----

test('help icons: every visible (i) is focusable, has an accessible name and a bubble with the same text', async () => {
    for (const tab of ['general', 'smtp', 'templates', 'seerr', 'accounts', 'invitations']) {
        const { d, errors } = await open(PAGE, { session: { 'mailconcierge.cfgTab': tab }, strings: { ...REAL_EN, ...REAL_INV_EN }, liveTv: { available: true } });
        const icons = [...d.querySelectorAll(`#ucTab-${tab} .ucHelp`)].filter((i) => !i.hidden);
        assert.ok(icons.length > 0, tab);
        for (const icon of icons) {
            assert.equal(icon.getAttribute('tabindex'), '0');
            assert.equal(icon.getAttribute('role'), icon.querySelector('a') ? 'group' : 'img', 'an icon holding a link must not hide it as an image');
            assert.ok(icon.getAttribute('aria-label').trim(), `${tab}: ${icon.getAttribute('data-help')}`);
            assert.equal(icon.querySelectorAll('.ucTipPara').length > 0, true);
            assert.equal(paras(icon).join(' '), icon.getAttribute('aria-label'));
            assert.equal(icon.getAttribute('title'), null, 'no native tooltip: it would show a second bubble');
        }
        assert.deepEqual(errors, []);
    }
});

test('help icons: an icon with nothing to say is hidden, not an empty bubble', async () => {
    const { d } = await openSmtp({ config: { smtpHost: 'mail.example.org' } });
    assert.equal(userIcon(d).hidden, true);
    assert.equal(userIcon(d).querySelector('.ucTip'), null);
    assert.equal(userIcon(d).getAttribute('aria-label'), null);
});

test('help icons: the former paragraphs of every tab are in the icon of their field, in full', async () => {
    const { d } = await open(PAGE, { strings: REAL_EN });
    const text = (name) => paras(iconOf(d, name));
    assert.deepEqual(text('enabled'), [REAL_EN.enabledHint]);
    assert.deepEqual(text('publicUrl'), [REAL_EN['help.publicUrl'], REAL_EN.publicUrlHint]);
    assert.deepEqual(text('allowAdministrators'), [REAL_EN['help.allowAdministrators'], REAL_EN.allowAdminsHint]);
    assert.deepEqual(text('nativeForgotPassword'), [REAL_EN.nativeHint]);
    assert.deepEqual(text('defaultLanguage'), [REAL_EN['help.defaultLanguage'], REAL_EN.defaultLanguageHint]);
    assert.deepEqual(text('seerrApiKey'), [REAL_EN['help.seerrApiKey'], REAL_EN.secretHint]);
    assert.deepEqual(paras(d.querySelector('#ucTab-templates h3 .ucHelp')), [REAL_EN['help.templates'], REAL_EN.templatesHint]);
    assert.deepEqual(paras(d.querySelector('#ucTab-seerr h3:last-of-type .ucHelp')), [REAL_EN.seerrToolsHint]);
    assert.deepEqual(paras(d.querySelector('#ucTab-accounts h3 .ucHelp')), [REAL_EN['help.assignProvider'], REAL_EN.assignHint]);
    // What stays visible under a field is a status: it has an id and is shown by the page when needed.
    for (const el of d.querySelectorAll('.fieldDescription')) { assert.ok(el.id, el.outerHTML); }
});

test('help icons: the invitation hints are in the icons of the e-mail address and of the history', async () => {
    const { d } = await open(PAGE, { session: { 'mailconcierge.cfgTab': 'invitations' }, strings: { 'inv.emailHint': 'EMAIL HINT', 'inv.historyHint': 'HISTORY HINT' } });
    assert.deepEqual(paras(d.querySelector('#ucInvEmail').closest('.inputContainer').querySelector('.ucHelp')), ['EMAIL HINT']);
    assert.deepEqual(paras(d.querySelector('#ucTab-invitations h3 .ucHelp')), ['HISTORY HINT']);
    assert.equal(d.querySelector('#ucInvEmail').closest('.inputContainer').querySelector('.fieldDescription'), null);
});

test('help icons: texts are written as text, never as markup, and a link only exists for an https address', async () => {
    const evil = '<img src=x onerror=alert(1)><a href="javascript:alert(1)">x</a>';
    const { d, errors } = await open(PAGE, { strings: { ...REAL_EN, 'help.publicUrl': evil, 'smtpPreset.brevo.user': evil, smtpPresetDoc: evil }, session: { 'mailconcierge.cfgTab': 'smtp' }, config: { smtpHost: 'smtp-relay.brevo.com', smtpPort: 587, smtpSecurity: 'StartTls' } });
    assert.equal(d.querySelector('#MailConciergeConfigPage img'), null);
    assert.equal(paras(userIcon(d))[0], evil);
    const anchors = [...d.querySelectorAll('#MailConciergeConfigPage .ucHelp a')];
    assert.equal(anchors.length, 1);
    assert.match(anchors[0].getAttribute('href'), /^https:\/\//);
    assert.equal(anchors[0].textContent, evil);
    assert.deepEqual(errors, []);
    const script = html.slice(html.indexOf('function helpParts'), html.indexOf('function show('));
    assert.doesNotMatch(script, /innerHTML|insertAdjacentHTML|outerHTML/);
});

test('help icons: the bubble opens with the icon (hover, focus) and stays open while its link has the focus; Escape closes it', async () => {
    assert.match(html, /\.ucHelp:hover > \.ucTip[^{]*\.ucHelp:focus > \.ucTip[^{]*\.ucHelp:focus-within > \.ucTip \{ display: block; \}/);
    const { w, d } = await openSmtp({ config: { smtpHost: 'smtp-relay.brevo.com', smtpPort: 587, smtpSecurity: 'StartTls' } });
    const link = providerIcon(d).querySelector('a');
    link.focus();
    assert.equal(d.activeElement, link);
    assert.ok(providerIcon(d).matches(':focus-within'));
    link.dispatchEvent(new w.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    assert.notEqual(d.activeElement, link);
});

test('links: the page styles its own links only, in the colour of the text and underlined, Jellyfin blue when pointed at or focused', () => {
    const style = html.slice(html.indexOf('<style>') + 7, html.indexOf('</style>'));
    const rules = [...style.matchAll(/([^{}]+)\{([^{}]*)\}/g)].map((m) => ({ selectors: m[1].replace(/\/\*[\s\S]*?\*\//g, '').split(',').map((x) => x.trim()).filter(Boolean), body: m[2] }));
    // Nothing leaks to the rest of Jellyfin: every selector is anchored on the page or on one of its own elements.
    for (const rule of rules) { for (const sel of rule.selectors) { assert.match(sel, /^(#MailConciergeConfigPage|#uc[A-Za-z]+)/, sel); } }
    const rule = (sel) => rules.find((r) => r.selectors.includes(sel));
    assert.match(rule('#MailConciergeConfigPage a').body, /color: inherit/);
    assert.match(rule('#MailConciergeConfigPage a').body, /text-decoration: underline/);
    assert.match(rule('#MailConciergeConfigPage a:hover').body, /color: #00a4dc/);
    assert.ok(rule('#MailConciergeConfigPage a:focus'));
    // The bubble is dark in every theme: its links are light, blue when pointed at.
    assert.match(rule('#MailConciergeConfigPage .ucTipBox a').body, /color: #fff/);
    assert.match(rule('#MailConciergeConfigPage .ucTipBox a:hover').body, /color: #00a4dc/);
    assert.match(rule('#MailConciergeConfigPage .ucTipBox').body, /background: #202020/);
});

test('links: the profile link of an accepted invitation is an ordinary link of the page (so it follows the link style)', async () => {
    const invitations = [{ id: 'i1', state: 'Accepted', proposedUsername: 'alice', userId: '11111111-1111-1111-1111-111111111111', email: null, libraries: [], liveTv: true, createdAt: '2026-11-01T10:00:00Z', expiresAt: '2026-11-08T10:00:00Z', acceptedAt: '2026-11-02T10:00:00Z' }];
    const { d } = await open(PAGE, { session: { 'mailconcierge.cfgTab': 'invitations' }, invitations });
    const link = d.querySelector('#ucInvTable tbody a');
    assert.ok(link);
    assert.equal(link.closest('#MailConciergeConfigPage') !== null, true);
    assert.equal(link.hasAttribute('style'), false, 'no inline colour that would beat the page rule');
    assert.equal(link.className, '');
});
