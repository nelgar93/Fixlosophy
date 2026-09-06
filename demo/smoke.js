#!/usr/bin/env node
/*
 * Smoke test for the built demo.
 *
 *   node demo/build.js && node demo/smoke.js
 *   node demo/smoke.js --url http://localhost:8080/     # against a served site build
 *
 * Drives demo/dist/fixlosophy-demo.html in headless Chromium: every route, the
 * booking wizard end to end, each persona, and the things that only break once the
 * page is a single shared file — an image that never loaded, a console error, a
 * page wider than the phone it is being read on.
 *
 * Needs Playwright. This container has it globally:
 *   NODE_PATH=/opt/node22/lib/node_modules node demo/smoke.js
 */

'use strict';

const path = require('path');
const fs = require('fs');
const { chromium } = require('playwright');
const { linkedCss } = require('./stylesheets');

// Either shape of build: the single file by default, or a served site build (the one
// GitHub Pages publishes) when a URL is passed.
const urlArg = process.argv.indexOf('--url');
const FILE = path.join(__dirname, 'dist', 'fixlosophy-demo.html');
const SERVED = urlArg !== -1 ? process.argv[urlArg + 1].replace(/\/$/, '') + '/' : null;
const URL_BASE = SERVED || 'file://' + FILE;

// #/contact is retired — the real app 301s it to /about#contact, and the demo's
// router renders About there. Still swept, so the old address never dead-ends.
const ROUTES = ['#/', '#/services', '#/about', '#/gallery', '#/contact', '#/book',
                '#/privacy', '#/terms', '#/account/login', '#/account/register',
                '#/account/forgot', '#/admin/login', '#/nowhere'];

// The demo drifted by a whole admin tab once: Admin.razor grew Availability and
// nothing here noticed, because the tab list was a hand-written array. So it isn't one
// any more — the tabs come out of the Razor, and a tab the demo hasn't got fails.
// Skipped, not failed, when the demo has been copied away from the repo it mirrors.
const ADMIN_RAZOR = path.join(__dirname, '..', 'Components', 'Pages', 'Admin.razor');
const dashboardTabs = fs.existsSync(ADMIN_RAZOR)
    ? [...fs.readFileSync(ADMIN_RAZOR, 'utf8')
         .matchAll(/private const string Tab\w+\s*=\s*"([a-z]+)"/g)].map((m) => m[1])
    : null;

const failures = [];
const fail = (msg) => { failures.push(msg); console.log('  FAIL  ' + msg); };
const pass = (msg) => console.log('  ok    ' + msg);
const check = (cond, msg) => (cond ? pass(msg) : fail(msg));

(async () => {
    if (!SERVED && !fs.existsSync(FILE)) {
        console.error('Missing ' + FILE + ' — run `node demo/build.js` first.');
        process.exit(1);
    }
    console.log('Testing ' + URL_BASE);

    const browser = await chromium.launch();
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await context.newPage();

    const consoleErrors = [];
    page.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(m.text()); });
    page.on('pageerror', (e) => consoleErrors.push('pageerror: ' + e.message));

    const go = async (hash) => {
        await page.goto(URL_BASE + hash);
        await page.waitForTimeout(120);
    };

    console.log('\nRoutes');
    for (const route of ROUTES) {
        await go(route);
        const text = await page.locator('#app').innerText();
        check(text.trim().length > 120, route + ' renders (' + text.trim().length + ' chars)');
    }

    console.log('\nPhotography (' + (SERVED ? 'served alongside the page' : 'baked-in data URIs') + ')');
    await go('#/gallery');
    await page.waitForTimeout(400);
    const images = await page.evaluate(() => [...document.querySelectorAll('#app img')]
        .map((i) => ({ src: i.currentSrc.slice(0, 120), w: i.naturalWidth, alt: i.alt })));
    check(images.length >= 8, 'gallery renders ' + images.length + ' photos');
    const broken = images.filter((i) => i.w === 0);
    check(broken.length === 0, 'every gallery photo decoded' +
        (broken.length ? ' — broken: ' + broken.map((b) => b.alt).join(', ') : ''));
    check(images.every((i) => i.src.startsWith(SERVED ? URL_BASE + 'assets/' : 'data:image')),
        SERVED ? 'photos come from the site\'s own assets' : 'photos come from the baked-in data URIs');

    console.log('\nBooking wizard, end to end');
    await go('#/book');
    await page.locator('.service-pick-card').first().click();
    await page.locator('[data-act="book-step2"]').click();
    await page.waitForSelector('.cal-day--available');
    await page.locator('.cal-day--available').first().click();
    await page.waitForSelector('.timeslot');
    const slot = await page.locator('.timeslot').first().innerText();
    await page.locator('.timeslot').first().click();
    await page.locator('[data-act="book-step3"]').click();
    await page.waitForSelector('#booking-name');
    await page.fill('#booking-name', 'Demo Visitor');
    await page.fill('#booking-email', 'demo.visitor@example.com');
    await page.fill('#booking-phone', '07700 900123');
    await page.fill('#booking-bike', 'Trek FX 3, 2020');
    await page.locator('[data-act="book-step4"]').click();
    await page.waitForSelector('[data-act="book-confirm"]');
    await page.locator('[data-act="book-confirm"]').click();
    await page.waitForSelector('.confirmed-ref', { timeout: 5000 });
    const reference = (await page.locator('.confirmed-ref strong').innerText()).trim();
    check(/^FIX-\d{6}-\d{3}$/.test(reference), 'booking confirmed with reference ' + reference + ' at ' + slot);

    console.log('\nThe new booking reaches the dashboard');
    await page.evaluate(() => applyPersona('admin'));
    await page.waitForTimeout(200);
    await page.evaluate(() => { state.admin.tab = 'bookings'; state.admin.search = 'demo.visitor@example.com'; render(); });
    await page.waitForTimeout(200);
    const adminText = await page.locator('#app').innerText();
    check(adminText.includes(reference), 'admin bookings tab finds ' + reference);
    check(adminText.includes('Demo Visitor'), 'admin sees the customer name');

    console.log('\nPersonas');
    const personas = [
        ['guest', 'Sign in'],
        ['customer', 'Dashboard'],
        ['admin', 'Dashboard'],
        ['worker', 'Dashboard'],
        ['worker-restricted', 'Dashboard']
    ];
    for (const [key, expect] of personas) {
        await page.evaluate((k) => applyPersona(k), key);
        await page.waitForTimeout(200);
        const body = await page.locator('body').innerText();
        check(body.includes(expect), 'persona ' + key + ' lands on a page showing "' + expect + '"');
    }

    console.log('\nAdmin tabs (as the admin persona)');
    await page.evaluate(() => applyPersona('admin'));
    const tabsToSweep = dashboardTabs ||
        ['calendar', 'bookings', 'customers', 'enquiries', 'availability', 'pricing', 'staff'];
    for (const tab of tabsToSweep) {
        const ok = await page.evaluate((t) => {
            try { state.admin.tab = t; render(); return document.querySelector('#app').innerText.length > 200; }
            catch (e) { return 'threw: ' + e.message; }
        }, tab);
        check(ok === true, 'admin tab "' + tab + '" renders');
    }

    console.log('\nCustomer account (Aisha)');
    await page.evaluate(() => applyPersona('customer'));
    await page.waitForTimeout(200);
    const account = await page.locator('#app').innerText();
    check(/Upcoming/i.test(account), 'account page lists upcoming bookings');
    check(/Specialized Allez/.test(account), 'saved bikes show');
    check(account.indexOf('Your Bikes') < account.indexOf('Your Details'),
        'Your Bikes sits above Your Details');

    // The shared completion note belongs to the job it came out of, not to a list of
    // its own — and a job finished on its appointment day must not hide in Upcoming.
    const report = await page.evaluate(() => {
        const note = document.querySelector('.account-booking-row__note');
        const upcoming = [...document.querySelectorAll('.booking-card')]
            .find((c) => /Upcoming/.test(c.querySelector('.booking-card__title')?.textContent || ''));
        return {
            onARow: !!note?.closest('.account-booking-row'),
            headed: /Mechanic's report/.test(
                document.querySelector('.account-booking-row__note-head')?.textContent || ''),
            completedStuckInUpcoming: !!upcoming?.querySelector('.status-badge--completed')
        };
    });
    check(report.onARow, "the mechanic's report renders inside its booking row");
    check(report.headed, 'it is headed as the report on that job');
    check(!report.completedStuckInUpcoming, 'a completed booking is out of Upcoming');

    console.log('\nAdmin customer panel');
    await page.evaluate(() => {
        applyPersona('admin');
        state.admin.tab = 'customers';
        state.admin.selectedCustomerId = 'cust-aisha';
        render();
    });
    await page.waitForTimeout(200);
    const panel = await page.evaluate(() => {
        const headings = [...document.querySelectorAll('.admin-subheading')].map((h) => h.textContent.trim());
        return {
            nested: document.querySelectorAll('.customer-booking .customer-booking__notes .customer-note').length,
            bookingsFirst: headings.findIndex((h) => h.startsWith('Bookings'))
                         < headings.findIndex((h) => h.startsWith('General')),
            keepsShareBox: !!document.querySelector('.customer-detail__add-note .complete-note__share')
        };
    });
    check(panel.nested > 0, 'job notes render under the booking they came from');
    check(panel.bookingsFirst, 'bookings come before the general notes');
    check(panel.keepsShareBox, 'the share-with-customer box is still there');

    console.log('\nData export');
    await page.evaluate(() => applyPersona('customer'));
    await page.waitForTimeout(200);
    check(/See my data/.test(await page.locator('#app').innerText()),
        'the account page offers the export');
    // Nothing may try to start a download: a page that offers one can't be shared by
    // link, which is what this build exists for.
    const attempted = await page.evaluate(() => {
        let tried = false;
        const create = document.createElement.bind(document);
        document.createElement = (tag) => {
            const el = create(tag);
            if (tag.toLowerCase() === 'a') {
                Object.defineProperty(el, 'download', { set() { tried = true; }, get: () => '' });
            }
            return el;
        };
        accountExport();
        document.createElement = create;
        return tried;
    });
    check(attempted === false, 'the export starts no download');
    await page.locator('.demo-export__json').waitFor({ timeout: 5000 });
    const exportText = await page.locator('.demo-export__json').innerText();
    check(exportText.includes('"reference": "FIX-'), 'the export panel shows the bookings');
    check(exportText.includes('aisha.bello@example.com'), 'the export panel shows the account');
    check(!/Rear hub has a touch of play/.test(exportText), 'internal staff notes stay out of the export');
    await page.locator('[data-act="export-copy"]').click();
    await page.waitForTimeout(200);
    await page.locator('.demo-export__close').click();
    await page.waitForTimeout(200);
    check((await page.locator('.demo-export').count()) === 0, 'the export panel closes');

    // Closures are the half of availability a customer sees, and the stranded list is
    // the half only the shop sees. Both were missing entirely once, so both are swept.
    console.log('\nClosures and availability');

    await page.evaluate(() => { applyPersona('guest'); bookReset(); });
    await go('#/book');
    await page.locator('.service-pick-card').first().click();
    await page.locator('[data-act="book-step2"]').click();
    await page.waitForSelector('.cal-day');
    const closedDays = await page.evaluate(() => [...document.querySelectorAll('.cal-day--closed')]
        .map((el) => ({ title: el.title, aria: el.getAttribute('aria-label'), disabled: el.disabled })));
    check(closedDays.length > 0, 'the booking calendar has a closed day (' + closedDays.length + ')');
    check(closedDays.every((d) => /^Closed/.test(d.title)),
        'each one says it is closed rather than just going grey');
    check(closedDays.some((d) => /Closed — .+/.test(d.title)),
        'and at least one gives the reason: ' + (closedDays.find((d) => /—/.test(d.title)) || {}).title);
    check(closedDays.every((d) => d.disabled && /—/.test(d.aria)),
        'the reason reaches a screen reader, and the day cannot be picked');
    check((await page.locator('.cal-legend__closed').count()) === 1, 'the legend explains the marker');

    await page.evaluate(() => { applyPersona('admin'); state.admin.tab = 'availability'; render(); });
    await page.waitForTimeout(200);
    const stranded = await page.evaluate(() => findStrandedBookings().length);
    check(stranded > 0, 'the shop is closed over ' + stranded + ' live booking(s)');
    check((await page.locator('.availability-affected__row').count()) === stranded,
        'the Availability tab lists every one of them');
    check((await page.locator('.availability-panel').count()) === 2,
        'closures and staff absences are separate lists');

    // A closure with no reason is refused: the reason is what the customer sees.
    await page.locator('[data-act="avail-add-closure"]').click();
    await page.waitForTimeout(150);
    check(/reason/i.test(await page.locator('.admin-inline-error').innerText().catch(() => '')),
        'a closure with no reason is refused');

    // Moving somebody takes them off the list and keeps their reference.
    const move = await page.evaluate(() => {
        const b = findStrandedBookings()[0];
        for (let i = 1; i <= 60; i++) {
            const date = addDays(today(), i);
            const slot = availableSlots(date)[0];
            if (slot) {
                state.admin.availability.moves[b.id] = { date: isoDate(date), slot };
                render();
                return { id: b.id, reference: b.reference };
            }
        }
        return null;
    });
    check(move !== null, 'there is somewhere to move the first one to');
    await page.locator('[data-act="avail-move"][data-id="' + move.id + '"]').click();
    await page.waitForTimeout(200);
    const afterMove = await page.evaluate(() => findStrandedBookings().length);
    check(afterMove === stranded - 1, 'moving one takes it off the list (' + afterMove + ' left)');
    check((await page.evaluate((id) => bookingById(id).reference, move.id)) === move.reference,
        'and keeps the reference the customer was emailed');

    // A worker must not reach the tab at all, the same as Pricing and Staff.
    const workerLandsOn = await page.evaluate(() => {
        applyPersona('worker');
        state.admin.tab = 'availability';
        render();
        return state.admin.tab;
    });
    check(workerLandsOn !== 'availability', 'a worker is bounced off the tab to ' + workerLandsOn);
    await page.evaluate(() => applyPersona('admin'));

    console.log('\nResponsive');
    for (const width of [320, 390, 768, 1440]) {
        await page.setViewportSize({ width, height: 900 });
        for (const route of ['#/', '#/services', '#/gallery', '#/book', '#/admin']) {
            await go(route);
            await page.waitForTimeout(150);
            const overflow = await page.evaluate(() =>
                document.documentElement.scrollWidth - document.documentElement.clientWidth);
            if (overflow > 1) fail('horizontal overflow on ' + route + ' at ' + width + 'px (' + overflow + 'px)');
        }
        pass('no horizontal overflow at ' + width + 'px');
    }
    await page.setViewportSize({ width: 1280, height: 900 });

    console.log('\nPersistence');
    await go('#/');
    const seeded = await page.evaluate(() => DB.bookings.length);
    await page.evaluate(() => { state.book = state.book; persist(); });
    await page.reload();
    await page.waitForTimeout(200);
    const afterReload = await page.evaluate(() => DB.bookings.length);
    check(afterReload === seeded, 'the store survives a reload (' + afterReload + ' bookings)');
    await page.evaluate(() => resetDemo());
    await page.waitForTimeout(200);
    const afterReset = await page.evaluate(() => DB.bookings.length);
    check(afterReset > 0 && afterReset === seeded - 1, 'reset clears the demo booking (' + afterReset + ' bookings)');

    console.log('\nConsole');
    check(consoleErrors.length === 0, 'no console errors' +
        (consoleErrors.length ? ':\n        ' + consoleErrors.slice(0, 6).join('\n        ') : ''));

    // The site's CSS is linked rather than copied, so it can no longer drift — but the
    // markup in this file's render functions can, and silently. Renaming a modifier in
    // the Razor without renaming it here leaves a button matching only the base
    // .action-btn: no background, no colour, so the browser falls back to its own
    // grey-on-black default button chrome. That is exactly what shipped once.
    //
    // Scanned from the source rather than the DOM on purpose: only one route and one
    // persona are rendered at a time, so a DOM sweep silently misses every screen it
    // isn't currently looking at — which is most of them.
    console.log('\nClass names');
    const source = fs.readFileSync(path.join(__dirname, 'index.html'), 'utf8');
    const styleEnd = source.indexOf('</style>');
    // Both stylesheets: the site's, read from where index.html links it, and the
    // harness's own block. Reading only the latter would report every class the real
    // site defines as an orphan.
    const css = linkedCss(source, __dirname) + source.slice(0, styleEnd);
    const markup = source.slice(styleEnd);

    const defined = new Set([...css.matchAll(/\.([a-zA-Z][a-zA-Z0-9_-]*--[a-zA-Z0-9_-]+)/g)].map((m) => m[1]));
    const used = new Set([...markup.matchAll(/(?<![-\w])([a-z][a-z0-9]*(?:-[a-z0-9]+)*--[a-z0-9-]+)/g)].map((m) => m[1]));
    const orphanClasses = [...used].filter((c) => !defined.has(c));

    check(orphanClasses.length === 0,
        'every BEM modifier in the markup has a CSS rule' +
        (orphanClasses.length ? ': ' + orphanClasses.join(', ') : ''));

    // The same class of drift one level up: a tab the real dashboard has and this file
    // doesn't. Rendering the tabs proves they work; this proves the list is complete.
    if (dashboardTabs) {
        check(dashboardTabs.length > 0, 'read ' + dashboardTabs.length + ' tabs out of Admin.razor');
        const absent = dashboardTabs.filter((t) => !markup.includes("button('" + t + "'"));
        check(absent.length === 0,
            'the demo has every tab the dashboard does' +
            (absent.length ? ' — missing: ' + absent.join(', ') : ''));
    } else {
        pass('Admin.razor is not alongside — skipping the tab cross-check');
    }

    await browser.close();

    console.log('\n' + (failures.length === 0
        ? 'All checks passed.'
        : failures.length + ' check(s) failed.'));
    process.exit(failures.length === 0 ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
