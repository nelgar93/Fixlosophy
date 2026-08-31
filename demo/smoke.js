#!/usr/bin/env node
/*
 * Smoke test for the built demo.
 *
 *   node demo/build.js && node demo/smoke.js
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

const FILE = path.join(__dirname, 'dist', 'fixlosophy-demo.html');
const URL_BASE = 'file://' + FILE;

const ROUTES = ['#/', '#/services', '#/about', '#/gallery', '#/contact', '#/book',
                '#/privacy', '#/terms', '#/account/login', '#/account/register',
                '#/account/forgot', '#/admin/login', '#/nowhere'];

const failures = [];
const fail = (msg) => { failures.push(msg); console.log('  FAIL  ' + msg); };
const pass = (msg) => console.log('  ok    ' + msg);
const check = (cond, msg) => (cond ? pass(msg) : fail(msg));

(async () => {
    if (!fs.existsSync(FILE)) {
        console.error('Missing ' + FILE + ' — run `node demo/build.js` first.');
        process.exit(1);
    }

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

    console.log('\nPhotography (baked-in data URIs)');
    await go('#/gallery');
    await page.waitForTimeout(400);
    const images = await page.evaluate(() => [...document.querySelectorAll('#app img')]
        .map((i) => ({ src: i.currentSrc.slice(0, 24), w: i.naturalWidth, alt: i.alt })));
    check(images.length >= 8, 'gallery renders ' + images.length + ' photos');
    const broken = images.filter((i) => i.w === 0);
    check(broken.length === 0, 'every gallery photo decoded' +
        (broken.length ? ' — broken: ' + broken.map((b) => b.alt).join(', ') : ''));
    check(images.every((i) => i.src.startsWith('data:image')), 'photos come from the baked-in data URIs');

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
        ['guest', 'Book a Repair'],
        ['customer', 'My Account'],
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
    for (const tab of ['calendar', 'bookings', 'customers', 'enquiries', 'pricing', 'staff']) {
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

    await browser.close();

    console.log('\n' + (failures.length === 0
        ? 'All checks passed.'
        : failures.length + ' check(s) failed.'));
    process.exit(failures.length === 0 ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
