const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');

(async () => {
    const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || '/usr/bin/chromium', headless: true });
    try {
        const page = await browser.newPage();
        await page.goto('about:blank');
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        const appRoot = path.resolve(__dirname, '../../NuctiusNow/NuctiusNow');
        const catalogs = JSON.parse(fs.readFileSync(path.join(appRoot, 'Localization/common.json'), 'utf8'));
        await page.setContent(`<html><head><title>News Details - NuctiusNow</title></head><body>
            <a class="nav-logo">NuctiusNow</a><nav><span id="home">Home</span><span id="settings">Settings</span></nav>
            <label>Email<input id="email" value="reader@example.test" placeholder="Email Address"></label>
            <select id="language"><option value="es" translate="no">Español</option></select>
            <h1 class="detail-title" data-news-text>Source headline</h1>
            <div class="summary-section"><p data-news-text>Source summary</p></div>
            <div class="full-article-body" data-news-text>First article paragraph.\n\nSecond factual paragraph.</div>
            <div class="chat-user">Keep my typed message</div>
        </body></html>`);
        await page.addScriptTag({ path: path.join(appRoot, 'wwwroot/js/localization.js') });
        await page.evaluate(() => {
            window.translationCalls = 0;
            window.failTranslation = false;
            window.translationDelay = 0;
            window.mockReference = { async invokeMethodAsync(method, texts, code) {
                window.translationCalls++;
                await new Promise(resolve => setTimeout(resolve, window.translationDelay));
                if (window.failTranslation) throw new Error('Model unavailable');
                return texts.map(item => `${code}:${item.text}`);
            }};
        });
        const languages = { en:'en-US', es:'es-AR', pt:'pt-BR', ru:'ru-RU', zh:'zh-CN', ja:'ja-JP', hi:'hi-IN', ar:'ar', fr:'fr-FR', de:'de-DE', it:'it-IT' };
        await page.evaluate(({catalog}) => window.nuctiusLocalization.start(window.mockReference,
            { code:'es', culture:'es-AR', rightToLeft:false }, catalog), { catalog:catalogs.es });
        for (const [code, culture] of Object.entries(languages)) {
            await page.evaluate(({code,culture,catalog}) => window.nuctiusLocalization.setLanguage(
                {code,culture,rightToLeft:code==='ar'}, catalog), {code,culture,catalog:catalogs[code] || {}});
            await page.waitForFunction(({code}) => document.querySelector('.full-article-body').textContent.startsWith(code+':'), {code});
            assert.equal(await page.locator('#home').textContent(), code === 'en' ? 'Home' : catalogs[code].Home);
            assert.equal(await page.locator('html').getAttribute('dir'), code === 'ar' ? 'rtl' : 'ltr');
            assert.equal(await page.locator('html').getAttribute('lang'), code === 'zh' ? 'zh-CN' : code);
            assert.equal(await page.locator('#email').inputValue(), 'reader@example.test');
            assert.equal(await page.locator('.nav-logo').textContent(), 'NuctiusNow');
            assert.equal(await page.locator('#language').inputValue(), 'es');
            assert.equal(await page.locator('#language option').textContent(), 'Español');
            assert.equal(await page.locator('.chat-user').textContent(), 'Keep my typed message');
        }
        // Mutations from navigation/live news must use the original new text, not old translations.
        await page.evaluate(() => {
            document.querySelector('.detail-title').textContent = 'New headline after navigation';
            const dynamic = document.createElement('p');
            dynamic.id = 'dynamic'; dynamic.textContent = 'Dynamic validation message'; document.body.append(dynamic);
        });
        await page.waitForFunction(() => document.querySelector('#dynamic').textContent === 'it:Dynamic validation message');
        assert.equal(await page.locator('.detail-title').textContent(), 'it:New headline after navigation');
        const calls = await page.evaluate(() => window.translationCalls);
        await page.waitForTimeout(400);
        assert.equal(await page.evaluate(() => window.translationCalls), calls, 'observer must settle instead of looping');
        // A response from the old language must never overwrite a newer selection.
        await page.evaluate(({catalog}) => {
            window.translationDelay = 250;
            document.querySelector('.detail-title').textContent = 'Race headline';
            window.nuctiusLocalization.setLanguage({code:'ru',culture:'ru-RU'}, catalog);
        }, {catalog:catalogs.ru});
        await page.waitForTimeout(160);
        await page.evaluate(({catalog}) => window.nuctiusLocalization.setLanguage({code:'ar',culture:'ar',rightToLeft:true}, catalog), {catalog:catalogs.ar});
        await page.waitForFunction(() => document.querySelector('.detail-title').textContent === 'ar:Race headline');
        await page.evaluate(() => { window.failTranslation = true; document.querySelector('#dynamic').textContent = 'New failing text'; });
        await page.waitForFunction(() => !document.querySelector('.localization-status button').hidden);
        assert.equal(await page.locator('.localization-status span').textContent(), catalogs.ar['Translation unavailable. Try again.']);
        await page.evaluate(() => { window.failTranslation = false; window.translationDelay = 0; });
        await page.locator('.localization-status button').click();
        await page.waitForFunction(() => document.querySelector('#dynamic').textContent === 'ar:New failing text');
        const spoken = await page.evaluate(() => window.nuctiusLocalization.articleText());
        assert.ok(spoken.includes('ar:Race headline'));
        assert.ok(spoken.includes('ar:Source summary'));
        assert.ok(spoken.includes('ar:First article paragraph.'));
        await page.addScriptTag({ path: path.join(appRoot, 'wwwroot/js/nuctiusTts.js') });
        await page.evaluate(() => {
            window.SpeechSynthesisUtterance = function(text) { this.text = text; };
            Object.defineProperty(window, 'speechSynthesis', { configurable:true, value: {
                cancel() {}, speak(utterance) { window.spokenUtterance = utterance; }
            }});
            window.nuctiusTts.speak('Translated article', 1.25, 'ar');
        });
        assert.equal(await page.evaluate(() => window.spokenUtterance.lang), 'ar');
        assert.equal(await page.evaluate(() => window.spokenUtterance.rate), 1.25);
        assert.deepEqual(errors, []);
        console.log('PASS: 11 languages, news text, input preservation, live updates, stale responses, failure/retry and read-aloud text');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
