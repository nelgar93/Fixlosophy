#!/usr/bin/env node
/*
 * The one place that knows how demo/index.html gets the real site's CSS.
 *
 * The demo used to carry wwwroot/app.css and wwwroot/booking.css copied in by hand,
 * byte for byte, with a pair of `sed | diff` commands in the README to check the copy
 * was still exact. It wasn't: a stylesheet change that never got re-copied left the
 * demo 182 lines behind, silently, because nothing runs a README.
 *
 * So the copy is gone. `index.html` <link>s the real files — which works opened from
 * disk, the path being relative to the demo folder — and demo/build.js inlines them
 * into the built page, which has to stand alone. Nothing to keep in sync: the demo
 * reads the same bytes the site serves, or the build fails.
 *
 * Both the builder and the smoke test need this, and they need to agree, hence a
 * module rather than the same regex written out twice.
 */

'use strict';

const fs = require('fs');
const path = require('path');

/*
 * A whole-line <link> to a stylesheet. Anchored to the line so replacing one takes its
 * newline with it, and deliberately narrow: this matches the head of index.html, not
 * arbitrary markup, and anything it can't parse should fail the build rather than be
 * quietly skipped.
 */
const LINK = /^[ \t]*<link[^>]*\brel="stylesheet"[^>]*\bhref="([^"]+)"[^>]*>[ \t]*\r?\n/gm;

/**
 * Every stylesheet `html` links, in document order, as { href, file, css }.
 * `root` is the directory the hrefs are relative to — demo/.
 */
function linkedStylesheets(html, root) {
    const found = [];
    for (const match of html.matchAll(LINK)) {
        const href = match[1];
        const file = path.resolve(root, href);
        if (!fs.existsSync(file)) {
            throw new Error('demo/index.html links ' + href + ', which is not at ' + file);
        }
        found.push({ href, file, css: fs.readFileSync(file, 'utf8') });
    }
    return found;
}

/**
 * `html` with every linked stylesheet replaced, in place, by its contents in a
 * <style> block — so the built page carries no external CSS request. Throws if there
 * are none, because a build with no site CSS in it renders as unstyled markup and
 * looks, at a glance, like a page that simply failed to load.
 */
function inlineStylesheets(html, root) {
    const sheets = linkedStylesheets(html, root);
    if (sheets.length === 0) {
        throw new Error('No stylesheet links in demo/index.html — nothing to inline.');
    }

    let index = 0;
    const inlined = html.replace(LINK, () => {
        const sheet = sheets[index++];
        return '<style>\n/* ' + sheet.href + ', inlined by demo/build.js */\n' +
               sheet.css.replace(/\s*$/, '') + '\n</style>\n';
    });

    return { html: inlined, sheets };
}

/** Just the CSS, concatenated. What the smoke test checks class names against. */
function linkedCss(html, root) {
    return linkedStylesheets(html, root).map((s) => s.css).join('\n');
}

module.exports = { LINK, linkedStylesheets, inlineStylesheets, linkedCss };
