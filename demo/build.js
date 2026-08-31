#!/usr/bin/env node
/*
 * Builds the standalone, shareable copy of the demo.
 *
 *   node demo/build.js            -> demo/dist/fixlosophy-demo.html
 *
 * `demo/index.html` is already self-contained apart from the shop photography,
 * which it pulls from the public Supabase bucket the real site uses. A published
 * Artifact runs under a sandbox that blocks off-origin images, so this step bakes
 * the photos in `demo/assets/` into the page as data URIs and the demo renders the
 * same everywhere — opened from disk, served over HTTP, or shared as a link.
 *
 * The mapping is by the path the page asks for, so `assets/shop/street-view.webp`
 * answers `img('shop/street-view.jpg')`: the page keeps naming the bucket's files,
 * and the build swaps in whatever it has locally.
 */

'use strict';

const fs = require('fs');
const path = require('path');

const root = __dirname;
const SOURCE = path.join(root, 'index.html');
const ASSETS = path.join(root, 'assets');
const OUT_DIR = path.join(root, 'dist');
const OUT = path.join(OUT_DIR, 'fixlosophy-demo.html');

const MIME = {
    '.webp': 'image/webp',
    '.avif': 'image/avif',
    '.jpg': 'image/jpeg',
    '.jpeg': 'image/jpeg',
    '.png': 'image/png',
    '.svg': 'image/svg+xml'
};

// The page names every photo as a .jpg in the bucket; locally they are .webp.
const requestedName = (file) => (path.extname(file) === '.webp' ? file.replace(/\.webp$/, '.jpg') : file);

function collect(dir, prefix) {
    const photos = {};
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) {
            Object.assign(photos, collect(full, prefix + entry.name + '/'));
            continue;
        }
        const mime = MIME[path.extname(entry.name).toLowerCase()];
        if (!mime) continue;
        const data = fs.readFileSync(full).toString('base64');
        photos[prefix + requestedName(entry.name)] = 'data:' + mime + ';base64,' + data;
    }
    return photos;
}

const photos = collect(ASSETS, '');
const names = Object.keys(photos).sort();
if (names.length === 0) {
    console.error('No images under ' + ASSETS + ' — nothing to bake in.');
    process.exit(1);
}

const html = fs.readFileSync(SOURCE, 'utf8');

// Anchor on the demo harness's own markup, which sits immediately above the script
// that reads window.__FIXLOSOPHY_PHOTOS. Failing loudly beats emitting a build whose
// photos silently never got baked in.
const anchor = '<div id="demo-toast" hidden></div>\n';
if (!html.includes(anchor)) {
    console.error('Could not find the injection point in demo/index.html.');
    process.exit(1);
}

const block =
    '<script>\n' +
    '/* Shop photography, baked in by demo/build.js so the page needs no network. */\n' +
    'window.__FIXLOSOPHY_PHOTOS = ' + JSON.stringify(photos) + ';\n' +
    '</script>\n';

fs.mkdirSync(OUT_DIR, { recursive: true });
fs.writeFileSync(OUT, html.replace(anchor, anchor + block));

const kb = (n) => (n / 1024).toFixed(0) + ' KB';
console.log('Baked in ' + names.length + ' images:');
names.forEach((n) => console.log('  ' + n.padEnd(32) + kb(photos[n].length)));
console.log('\nWrote ' + path.relative(process.cwd(), OUT) + ' (' + kb(fs.statSync(OUT).size) + ')');
