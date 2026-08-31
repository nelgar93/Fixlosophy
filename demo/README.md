# The shareable demo

A single-file, browser-only replica of the site, for showing Fixlosophy to someone
without giving them a database, an SMTP server, a Supabase bucket or a login.

Everything works: the booking wizard writes a real booking (with a reference from the
same `FIX-{yyMMdd}-{nnn}` scheme), the dashboard sees it a moment later, prices and
staff permissions can be edited, and the whole store survives a reload. Nothing leaves
the browser — there are no network calls at all in the built file.

## Files

| Path | What it is |
|---|---|
| `index.html` | The demo itself. `wwwroot/app.css` and `wwwroot/booking.css` verbatim, then a demo-harness stylesheet, then the app: an in-memory store standing in for `AppDbContext` and the service layer, and one render function per Razor page. |
| `assets/` | The shop photography, re-encoded as WebP. The same photos the real site serves from the public Supabase bucket. |
| `build.js` | Bakes `assets/` into the page as data URIs → `dist/fixlosophy-demo.html`. |
| `smoke.js` | Drives the built file in headless Chromium: every route, the wizard end to end, each persona, every admin tab, overflow at four widths. |

`dist/` is generated and gitignored.

## Build and check

```bash
node demo/build.js                  # -> demo/dist/fixlosophy-demo.html (~2 MB)
node demo/smoke.js                  # needs Playwright on NODE_PATH
```

`index.html` opens and runs on its own — the build step exists only because a
published Artifact runs in a sandbox that blocks off-origin images, so the photos have
to travel inside the file. Opened from disk or served over HTTP, the page falls back to
the bucket URLs and looks the same.

## What it stands in for

The business rules are ports of the real ones, so what the demo refuses the real site
refuses too: slot times derived from `SiteContent`'s trading hours, two bookings per
slot, three active bookings per email address, a two-hour cutoff on cancelling your own
booking, and the staff permission matrix (`CanViewAllBookings`, `CanManageBookings`,
`CanViewCustomerDetails`) gating what each dashboard shows.

What it can't stand in for: real email, file uploads reaching storage, password
hashing, rate limiting, and anything that depends on the server's clock rather than the
visitor's. One deliberate divergence: the real account page links to `/account/export`
and the browser saves the JSON, but a published page is sandboxed and can start no
download — and declaring the capability that would let it hand over a file stops the
page being shared by link at all, which is the one thing this build is for. So the
export is shown on the page in full instead, with a copy button.

## Driving it

The **Demo** button, bottom right, switches personas — visitor, customer, unverified
customer, admin, and two workers with different permission flags — jumps to any route,
and resets the seeded data.

## Keeping it honest

The two stylesheets are copied in byte for byte, so after a CSS change re-copy them and
confirm the copy is exact:

```bash
sed -n '5,1996p'    demo/index.html | diff - wwwroot/app.css
sed -n '1997,4171p' demo/index.html | diff - wwwroot/booking.css
```

(The line ranges move when the stylesheets grow — they start after `<style>` and end at
the `DEMO HARNESS` banner.) A change to a Razor page or a service means editing the
matching render function or store operation here; each carries the name of the C# it
mirrors.
