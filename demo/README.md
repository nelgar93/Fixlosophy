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
| `index.html` | The demo itself. A `<link>` to each of the real stylesheets, then a demo-harness stylesheet, then the app: an in-memory store standing in for `AppDbContext` and the service layer, and one render function per Razor page. |
| `assets/` | The shop photography, re-encoded as WebP. The same photos the real site serves from the public Supabase bucket. |
| `build.js` | Wires `assets/` and the linked stylesheets into the page — data URIs and inline CSS for a single travelling file, ordinary files and inline CSS for a hosted site. |
| `stylesheets.js` | The one place that knows how the page gets the site's CSS. Shared by the builder and the smoke test so they cannot disagree. |
| `smoke.js` | Drives a build in headless Chromium: every route, the wizard end to end, each persona, every admin tab, closures and the stranded list, overflow at four widths. |

`dist/` and `_site/` are generated and gitignored.

## Where it's published

GitHub Pages, from `.github/workflows/demo-pages.yml`, on every push to `main` that
touches `demo/` **or `wwwroot/`** — the build reads the real stylesheets, so a CSS
change alters what Pages serves without this folder being touched at all. That is the
link to send people. Pages has to be set to deploy from GitHub Actions (Settings →
Pages → Source: **GitHub Actions**); the workflow's `configure-pages` step turns that
on by itself the first time.

An Artifact copy exists too, but a published Artifact can only be shared publicly after
an automated review that this page is too large to get through — hence Pages.

## Build and check

```bash
node demo/build.js                       # -> demo/dist/fixlosophy-demo.html, photos inlined
node demo/build.js --pages _site         # -> _site/index.html + _site/assets, what Pages serves
node demo/smoke.js                       # needs Playwright on NODE_PATH
node demo/smoke.js --url http://localhost:8080/   # ...against a served site build
```

`index.html` opens and runs on its own: the stylesheet links resolve up into
`wwwroot/`, and the photos come from the public Supabase bucket the real site uses. The
builds exist to cut both of those, because neither survives the page leaving the repo.

`smoke.js` also runs in CI, from `.github/workflows/build.yml`, on every push and PR.

## What it stands in for

The business rules are ports of the real ones, so what the demo refuses the real site
refuses too: slot times derived from `SiteContent`'s trading hours, one booking per
slot, three active bookings per email address, a two-hour cutoff on cancelling your own
booking, and the staff permission matrix (`CanViewAllBookings`, `CanManageBookings`,
`CanViewCustomerDetails`) gating what each dashboard shows.

`AvailabilityService` is ported too: a day is bookable if it's a trading day, no all-day
closure covers it, and at least one mechanic is working. The seed arranges for all three
cases to be visible — a closure with a reason on the customer calendar, a part-day
closure that takes the afternoon, and an absence that does *not* shut the day because
somebody else is in. The closure is deliberately placed over a day that already has
bookings on it, so the Availability tab's stranded list has something in it the moment
you open it.

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

The stylesheets used to be copied in byte for byte, checked by a pair of `sed | diff`
commands written down in this file. They drifted 182 lines anyway, because nothing runs
a README. They are linked now, and inlined at build time, so that particular drift is
gone by construction — there is no copy to fall behind.

What can still drift is the markup: the render functions here are hand-written, and
renaming a modifier in the Razor without renaming it here leaves a button matching only
the base `.action-btn` — no background, no colour, browser-default chrome. That shipped
once. So `smoke.js` checks two things beyond driving the page:

- every BEM modifier used in this file's markup has a CSS rule behind it, scanned from
  source rather than the DOM (only one route renders at a time, so a DOM sweep misses
  most screens);
- every admin tab declared in `Components/Pages/Admin.razor` exists here. The demo fell
  a whole tab behind once — Availability — and nothing noticed, because the tab list
  was a hand-written array in the test.

A change to a Razor page or a service still means editing the matching render function
or store operation here; each carries the name of the C# it mirrors. Run
`node demo/smoke.js` before pushing, or let CI do it.
