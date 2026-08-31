#!/usr/bin/env node
/*
 * Builds a shareable copy of the demo. Two shapes, because the two places it gets
 * shared from have opposite constraints:
 *
 *   node demo/build.js                 -> demo/dist/fixlosophy-demo.html
 *       One file, photos baked in as data URIs. For anywhere the page has to travel
 *       on its own — a published Artifact, an email attachment, a USB stick.
 *
 *   node demo/build.js --pages _site   -> _site/index.html + _site/assets/…
 *       A normal static site: the photos stay files, so the browser caches them and
 *       the HTML stays small. For GitHub Pages (see .github/workflows/demo-pages.yml).
 *
 * `demo/index.html` needs neither: opened straight from disk it reads the photos from
 * the public Supabase bucket the real site uses. The builds exist to cut that last
 * dependency — an Artifact's sandbox blocks off-origin images outright, and a hosted
 * site shouldn't lean on a bucket it doesn't own.
 *
 * Either way the mapping is by the path the page asks for, so `assets/shop/x.webp`
 * answers `img('shop/x.jpg')`: the page keeps naming the bucket's files, and the build
 * swaps in whatever it has locally.
 */

'use strict';

const fs = require('fs');
const path = require('path');

const root = __dirname;
const SOURCE = path.join(root, 'index.html');
const ASSETS = path.join(root, 'assets');

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

// Every image under demo/assets, as { requested path -> { file, mime } }.
function collect(dir, prefix) {
    const found = {};
    for (const entry of fs.readdirSync(dir, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) {
            Object.assign(found, collect(full, prefix + entry.name + '/'));
            continue;
        }
        const mime = MIME[path.extname(entry.name).toLowerCase()];
        if (!mime) continue;
        found[prefix + requestedName(entry.name)] = { file: full, rel: prefix + entry.name, mime };
    }
    return found;
}

const args = process.argv.slice(2);
const pagesIndex = args.indexOf('--pages');
const pages = pagesIndex !== -1;
const outDir = pages
    ? path.resolve(args[pagesIndex + 1] || '_site')
    : path.join(root, 'dist');
const outFile = path.join(outDir, pages ? 'index.html' : 'fixlosophy-demo.html');

const images = collect(ASSETS, '');
const names = Object.keys(images);
if (names.length === 0) {
    console.error('No images under ' + ASSETS + ' — nothing to bundle.');
    process.exit(1);
}

const html = fs.readFileSync(SOURCE, 'utf8');

// Anchor on the demo harness's own markup, which sits immediately above the script
// that reads window.__FIXLOSOPHY_PHOTOS. Failing loudly beats emitting a build whose
// photos silently never got wired up.
const anchor = '<div id="demo-toast" hidden></div>\n';
if (!html.includes(anchor)) {
    console.error('Could not find the injection point in demo/index.html.');
    process.exit(1);
}

const photos = {};
for (const name of names) {
    const image = images[name];
    photos[name] = pages
        ? 'assets/' + image.rel
        : 'data:' + image.mime + ';base64,' + fs.readFileSync(image.file).toString('base64');
}

const block =
    '<script>\n' +
    '/* Shop photography, wired up by demo/build.js so the page needs no third-party host. */\n' +
    'window.__FIXLOSOPHY_PHOTOS = ' + JSON.stringify(photos) + ';\n' +
    '</script>\n';

fs.rmSync(outDir, { recursive: true, force: true });
fs.mkdirSync(outDir, { recursive: true });
fs.writeFileSync(outFile, html.replace(anchor, anchor + block));

if (pages) {
    fs.cpSync(ASSETS, path.join(outDir, 'assets'), { recursive: true });
    // Pages runs Jekyll over the artifact otherwise, which ignores files it doesn't
    // recognise; nothing here needs processing.
    fs.writeFileSync(path.join(outDir, '.nojekyll'), '');
}

const kb = (n) => (n / 1024).toFixed(0) + ' KB';
const assetBytes = names.reduce((sum, n) => sum + fs.statSync(images[n].file).size, 0);
const rel = (p) => path.relative(process.cwd(), p);

console.log((pages ? 'Site build' : 'Single-file build') + ' — ' + names.length + ' images');
if (pages) {
    console.log('  ' + rel(outFile) + ' (' + kb(fs.statSync(outFile).size) + ')');
    console.log('  ' + rel(path.join(outDir, 'assets')) + ' (' + kb(assetBytes) + ' over ' + names.length + ' files)');
} else {
    console.log('  ' + rel(outFile) + ' (' + kb(fs.statSync(outFile).size) + ', photos inlined)');
}
