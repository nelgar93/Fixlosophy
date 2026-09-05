// Small behaviours for the statically-rendered auth pages, which have no Blazor
// circuit to handle them, plus the image fallbacks used across the public pages.
// Everything is delegated from document, so it works for markup that arrives after
// load and needs no per-page wiring.
//
// This file is where any inline handler has to end up: script-src carries no
// 'unsafe-inline', so an onclick="" or onerror="" attribute is silently dead. See
// the CSP note in Program.cs.
(function () {
    'use strict';

    // ── Password visibility toggle ───────────────────────────────────────────
    // Typing a password blind on a phone keyboard is the main reason people get
    // locked out of their own account. Buttons are marked up as
    // <button data-password-toggle="<input id>">.
    document.addEventListener('click', function (e) {
        var button = e.target.closest('[data-password-toggle]');
        if (!button) return;

        var input = document.getElementById(button.getAttribute('data-password-toggle'));
        if (!input) return;

        var revealing = input.type === 'password';
        input.type = revealing ? 'text' : 'password';
        button.setAttribute('aria-pressed', revealing ? 'true' : 'false');
        button.setAttribute('aria-label', revealing ? 'Hide password' : 'Show password');

        // Keep the caret where it was rather than jumping to the start.
        var pos = input.value.length;
        input.focus();
        try { input.setSelectionRange(pos, pos); } catch (_) { /* type may not support it */ }
    });

    // ── Phone fields accept digits and nothing else ──────────────────────────
    // Marked up as <input type="tel" data-phone-input>. type="tel" only asks the
    // phone for a numeric keypad — it does not stop a desktop keyboard, a paste, or
    // an autofill from putting letters in, and the server used to accept them
    // because it just counted digits ("call me on 07700 900000" reached the admin
    // panel as a dead Call link). Punctuation people genuinely write in numbers is
    // kept: spaces, brackets, dashes, dots, and a leading +.
    //
    // Registered in the CAPTURE phase deliberately. Blazor attaches its @bind
    // handler to the element itself, so a document-level listener in the bubble
    // phase would run after Blazor had already read the unfiltered value. Capture
    // runs first, which lets this one listener cover both the Blazor-bound fields
    // and the statically-rendered POST forms, with no re-dispatch and no event loop.
    var PHONE_MAX = 20;

    var cleanPhone = function (raw) {
        var kept = '';
        for (var i = 0; i < raw.length; i++) {
            var c = raw[i];
            if (c >= '0' && c <= '9') { kept += c; continue; }
            // '+' is a country-code prefix, so it only means anything at the front.
            if (c === '+' && kept.length === 0) { kept += c; continue; }
            // Separators only separate — dropping them until there is something to
            // separate stops "call me on 07700..." leaving its spaces behind.
            if (kept.length && (c === ' ' || c === '(' || c === ')' || c === '-' || c === '.')) { kept += c; }
        }
        return kept.slice(0, PHONE_MAX);
    };

    document.addEventListener('input', function (e) {
        var input = e.target;
        if (!input || !input.hasAttribute || !input.hasAttribute('data-phone-input')) return;

        var cleaned = cleanPhone(input.value);
        if (cleaned === input.value) return;

        // Hold the caret where the typing was, minus whatever we dropped ahead of it.
        var caret = input.selectionStart;
        var removedBeforeCaret = input.value.length - cleaned.length;
        input.value = cleaned;
        if (caret !== null) {
            var pos = Math.max(0, caret - removedBeforeCaret);
            try { input.setSelectionRange(pos, pos); } catch (_) { /* not supported */ }
        }
    }, true);

    // ── Device diagnostics (/devinfo, development only) ──────────────────────
    // Safari's Web Inspector needs a Mac, so on Windows there is no console to read
    // when something looks wrong on an iPhone. This fills in what the device actually
    // reports. Each reading maps to a fix that could only be checked against
    // emulation. Bails out immediately on every other page.
    (function devInfo() {
        var card = document.querySelector('[data-devinfo-zoom-card]');
        if (!card) return;

        var set = function (key, value) {
            var el = document.querySelector('[data-devinfo="' + key + '"]');
            if (el) el.textContent = value;
        };

        var round = function (n) { return Math.round(n * 10) / 10; };

        // iOS zooms when a focused input is under 16px and never zooms back out.
        // visualViewport.scale is how you watch that happen — it goes to ~1.3 the
        // moment a too-small field takes focus.
        function reportScale() {
            var vv = window.visualViewport;
            if (!vv) {
                set('scale', 'n/a');
                set('scale-verdict', 'This browser has no visualViewport API.');
                return;
            }
            var scale = vv.scale;
            set('scale', scale.toFixed(2));
            set('scale-verdict', scale > 1.01
                ? 'ZOOMED — the page scaled to ' + scale.toFixed(2) + '×. The iOS zoom fix is NOT working here.'
                : 'Tap the field below. If this stays at 1.00, the iOS zoom fix is working.');
        }

        reportScale();
        if (window.visualViewport) {
            window.visualViewport.addEventListener('resize', reportScale);
            window.visualViewport.addEventListener('scroll', reportScale);
        }

        // The mechanism behind the above: anything under 16px triggers the zoom.
        var probe = document.getElementById('devinfo-probe');
        if (probe) {
            var px = parseFloat(getComputedStyle(probe).fontSize);
            set('input-font', px + 'px' + (px >= 16 ? '  ✓ (no zoom expected)' : '  ✗ iOS WILL zoom'));
        }

        // Must be false on a touchscreen, or hover styles latch on tap and stay.
        var hover = window.matchMedia('(hover: hover) and (pointer: fine)').matches;
        set('hover', hover
            ? 'true  ✗ (expected false on a phone — hover styles will stick)'
            : 'false  ✓ (hover styles correctly suppressed)');

        // Reads the env() values through a throwaway element, since they can't be
        // queried directly.
        var probeEl = document.createElement('div');
        probeEl.style.cssText =
            'position:fixed;top:0;left:0;visibility:hidden;' +
            'padding-top:env(safe-area-inset-top);padding-right:env(safe-area-inset-right);' +
            'padding-bottom:env(safe-area-inset-bottom);padding-left:env(safe-area-inset-left);';
        document.body.appendChild(probeEl);
        var cs = getComputedStyle(probeEl);
        var insets = [cs.paddingTop, cs.paddingRight, cs.paddingBottom, cs.paddingLeft];
        document.body.removeChild(probeEl);
        set('safe-area', 'top ' + insets[0] + ' · right ' + insets[1] +
                         ' · bottom ' + insets[2] + ' · left ' + insets[3]);

        set('viewport', window.innerWidth + ' × ' + window.innerHeight +
                        ' CSS px  ·  DPR ' + window.devicePixelRatio +
                        '  ·  screen ' + screen.width + ' × ' + screen.height);

        // The hero uses 100svh; this shows whether Safari's collapsing toolbar makes
        // that differ from innerHeight.
        var svhEl = document.createElement('div');
        svhEl.style.cssText = 'position:fixed;visibility:hidden;height:100svh;';
        document.body.appendChild(svhEl);
        var svh = round(svhEl.getBoundingClientRect().height);
        document.body.removeChild(svhEl);
        set('svh', svh + 'px vs ' + window.innerHeight + 'px' +
                   (Math.abs(svh - window.innerHeight) < 2 ? '  (same)' : '  (differ)'));

        set('reduced-motion',
            window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'reduce' : 'no-preference');

        // Measured rather than computed — this is what a thumb actually gets.
        var sizes = [];
        document.querySelectorAll('[data-devinfo-sample]').forEach(function (el) {
            var r = el.getBoundingClientRect();
            var name = el.getAttribute('data-devinfo-sample');
            var ok = r.width >= 44 && r.height >= 44 ? '✓' : '✗';
            sizes.push(name + ' ' + round(r.width) + '×' + round(r.height) + ' ' + ok);
        });
        set('touch', sizes.join('  ·  '));

        // This page is static SSR, so a connected circuit means Blazor is working
        // generally — useful for telling "layout is broken" from "nothing loaded".
        set('circuit', window.Blazor ? 'script loaded' : 'NOT loaded');

        set('ua', navigator.userAgent);

        // One tap to get everything into a message.
        var copyButton = document.querySelector('[data-devinfo-copy]');
        if (copyButton) {
            copyButton.addEventListener('click', function () {
                var lines = ['Fixlosophy /devinfo', new Date().toISOString(), ''];
                document.querySelectorAll('.devinfo__row').forEach(function (row) {
                    var dt = row.querySelector('dt'), dd = row.querySelector('dd');
                    if (dt && dd) lines.push(dt.textContent.trim() + ': ' + dd.textContent.trim());
                });
                var scaleEl = document.querySelector('[data-devinfo="scale"]');
                if (scaleEl) lines.splice(3, 0, 'Viewport scale: ' + scaleEl.textContent.trim());

                var text = lines.join('\n');
                var done = function () {
                    var flag = document.querySelector('[data-devinfo="copied"]');
                    if (flag) {
                        flag.hidden = false;
                        setTimeout(function () { flag.hidden = true; }, 2500);
                    }
                };

                // navigator.clipboard needs a secure context, and LAN testing runs over
                // plain HTTP — so fall back to selecting the text for a manual copy.
                if (navigator.clipboard && window.isSecureContext) {
                    navigator.clipboard.writeText(text).then(done, function () { fallback(text); });
                } else {
                    fallback(text);
                }

                function fallback(value) {
                    var ta = document.createElement('textarea');
                    ta.value = value;
                    ta.setAttribute('readonly', '');
                    ta.style.cssText = 'position:fixed;top:50%;left:5%;width:90%;height:40vh;z-index:9999;';
                    document.body.appendChild(ta);
                    ta.select();
                    try {
                        if (document.execCommand('copy')) { document.body.removeChild(ta); done(); return; }
                    } catch (_) { /* fall through to leaving it selected */ }
                    // Left on screen and selected so it can be copied by hand.
                }
            });
        }
    })();

    // ── Resend cooldown ──────────────────────────────────────────────────────
    // Mirrors AuthService.ResendCooldownSeconds so the button can't be mashed
    // during the window the server would ignore anyway. The duration comes from the
    // markup (data-cooldown-seconds) so the two can't silently drift apart.
    document.querySelectorAll('[data-cooldown-seconds]').forEach(function (button) {
        var remaining = parseInt(button.getAttribute('data-cooldown-seconds'), 10);
        if (!remaining || remaining < 0) return;

        var label = button.textContent;

        (function tick() {
            if (remaining <= 0) {
                button.disabled = false;
                button.textContent = label;
                return;
            }
            button.disabled = true;
            button.textContent = 'Resend available in ' + remaining + 's';
            remaining--;
            setTimeout(tick, 1000);
        })();
    });

    // ── Images that get out of the way when they fail ────────────────────────
    // Site photography lives in a Supabase bucket, so any given photo can 404 while
    // the rest of the page is fine. Rather than leave a broken-image icon in a
    // gallery grid, the image (or the figure around it) removes itself.
    //
    // This was eight inline onerror="" attributes until the CSP dropped
    // 'unsafe-inline' from script-src — inline handlers are exactly what that
    // forbids, and unlike a <script> block they can't carry a nonce. Same behaviour,
    // one place.
    //
    // Markup:
    //   data-hide-on-error            hide the image itself
    //   data-hide-on-error="figure"   hide the nearest matching ancestor instead
    //   data-reveal-next-on-error     also un-hide the next sibling (the logo's
    //                                 text fallback)
    function hideFailedImage(img) {
        if (!img || !img.hasAttribute('data-hide-on-error')) return;

        var selector = img.getAttribute('data-hide-on-error');
        var target = selector ? img.closest(selector) : img;
        if (target) target.style.display = 'none';

        if (img.hasAttribute('data-reveal-next-on-error') && img.nextElementSibling) {
            img.nextElementSibling.style.display = 'inline';
        }
    }

    // 'error' doesn't bubble, so this has to listen in the capture phase. Delegating
    // from document is what makes it work for images Blazor renders later.
    document.addEventListener('error', function (e) {
        if (e.target && e.target.tagName === 'IMG') hideFailedImage(e.target);
    }, true);

    // The listener above is attached when this file runs, at the end of <body> —
    // images higher up the page load in parallel with parsing and can fail before
    // then, and that error is gone for good. An image that has finished loading with
    // no intrinsic width is one that failed, so sweeping catches those. Cheap enough
    // to repeat once everything has settled.
    function sweepFailedImages() {
        document.querySelectorAll('img[data-hide-on-error]').forEach(function (img) {
            if (img.complete && img.naturalWidth === 0) hideFailedImage(img);
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', sweepFailedImages);
    } else {
        sweepFailedImages();
    }
    window.addEventListener('load', sweepFailedImages);
})();
