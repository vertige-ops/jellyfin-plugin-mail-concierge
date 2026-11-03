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
    assert.equal(w.location.href, 'http://jf.test/MailConcierge/Cancel?t=abc');
});
