using System.Text.Json;
using Microsoft.Playwright;

// A line-oriented browser driver for agents.
//
// Reads commands from stdin, one per line, and writes one result line per command
// to stdout. Built for driving a running web app from a shell: no Node needed
// (Playwright's .NET package bundles its own driver) and no browser download —
// it uses the Edge already installed on the machine via Channel = "msedge".
//
// Commands:
//   goto <url>                  navigate and wait for network idle
//   size <w>x<h>                set the viewport (device pixel ratio stays 1)
//   shot <path>                 full-page screenshot to <path>
//   shotview <path>             viewport-only screenshot to <path>
//   eval <js>                   evaluate JS, print the JSON result
//   click <selector>            click the first match
//   text <selector>             print textContent of the first match
//   count <selector>            print the number of matches
//   overflow                    report horizontal overflow + the elements causing it
//   touch <selector>            report bounding boxes smaller than 44x44
//   wait <ms>                   sleep
//   quit                        exit
//
// Every result line is prefixed OK: or ERR: so a shell caller can branch on it.

var exitCode = 0;
using var playwright = await Playwright.CreateAsync();

// ── Signed-in mode ───────────────────────────────────────────────────────────
// Set PWDRIVER_PROFILE to a directory and the browser keeps its cookies there
// between runs, instead of starting from a blank context every time.
//
// This is what makes the admin dashboard reachable at all. /admin needs a staff
// login, the driver never types passwords, and a fresh headless context is signed
// out by definition — so those screens simply couldn't be driven. With a profile the
// person running this signs in once, by hand, in a visible window (PWDRIVER_HEADED=1),
// and every later invocation reuses that session.
//
// Default behaviour is unchanged: no profile, headless, blank context.
var profileDir = Environment.GetEnvironmentVariable("PWDRIVER_PROFILE");
var headed = Environment.GetEnvironmentVariable("PWDRIVER_HEADED") == "1";

IBrowser? browser = null;
IBrowserContext context;

if (string.IsNullOrWhiteSpace(profileDir))
{
    browser = await playwright.Chromium.LaunchAsync(new()
    {
        Channel = "msedge",
        Headless = !headed,
    });

    context = await browser.NewContextAsync(new()
    {
        ViewportSize = new() { Width = 1280, Height = 900 },
        DeviceScaleFactor = 1,
        // Reports as a touch device so `@media (hover: hover)` and `(pointer: fine)`
        // evaluate the way they would on a phone. Overridden per-size below.
        HasTouch = false,
    });
}
else
{
    Directory.CreateDirectory(profileDir);
    context = await playwright.Chromium.LaunchPersistentContextAsync(profileDir, new()
    {
        Channel = "msedge",
        Headless = !headed,
        ViewportSize = new() { Width = 1280, Height = 900 },
        DeviceScaleFactor = 1,
        HasTouch = false,
    });
}

// A persistent context opens with a page already; a fresh one doesn't.
var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();

// Surface page errors rather than swallowing them — a JS exception on the page is
// usually the reason an interaction "did nothing".
page.PageError += (_, e) => Console.Error.WriteLine($"[pageerror] {e}");
page.Console += (_, e) =>
{
    if (e.Type is "error" or "warning") Console.Error.WriteLine($"[console.{e.Type}] {e.Text}");
};

Console.WriteLine("OK: ready");

string? line;
while ((line = Console.ReadLine()) is not null)
{
    line = line.Trim();
    if (line.Length == 0) continue;

    var space = line.IndexOf(' ');
    var cmd = (space < 0 ? line : line[..space]).ToLowerInvariant();
    var arg = space < 0 ? "" : line[(space + 1)..].Trim();

    try
    {
        switch (cmd)
        {
            case "goto":
                await page.GotoAsync(arg, new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30_000 });
                await SettleAsync(page);
                Console.WriteLine($"OK: at {page.Url}");
                break;

            case "size":
            {
                var parts = arg.Split('x', 2);
                var w = int.Parse(parts[0]);
                var h = parts.Length > 1 ? int.Parse(parts[1]) : 900;
                await page.SetViewportSizeAsync(w, h);
                await SettleAsync(page);
                Console.WriteLine($"OK: viewport {w}x{h}");
                break;
            }

            case "shot":
                await page.ScreenshotAsync(new() { Path = arg, FullPage = true });
                Console.WriteLine($"OK: wrote {arg}");
                break;

            case "shotview":
                await page.ScreenshotAsync(new() { Path = arg, FullPage = false });
                Console.WriteLine($"OK: wrote {arg}");
                break;

            case "eval":
            {
                var result = await page.EvaluateAsync<JsonElement?>(arg);
                Console.WriteLine("OK: " + (result is null ? "null" : result.Value.ToString()));
                break;
            }

            case "click":
                await page.ClickAsync(arg, new() { Timeout = 10_000 });
                Console.WriteLine($"OK: clicked {arg}");
                break;

            // fill <selector> <value> — the value is never echoed back, so this is
            // safe to use for passwords without leaking them into the transcript.
            case "fill":
            {
                var split = arg.IndexOf(' ');
                if (split < 0) { Console.WriteLine("ERR: fill needs '<selector> <value>'"); exitCode = 1; break; }
                var selector = arg[..split];
                var value = arg[(split + 1)..];
                await page.FillAsync(selector, value, new() { Timeout = 10_000 });
                Console.WriteLine($"OK: filled {selector} ({value.Length} chars)");
                break;
            }

            case "press":
            {
                var split = arg.IndexOf(' ');
                if (split < 0) { Console.WriteLine("ERR: press needs '<selector> <key>'"); exitCode = 1; break; }
                await page.PressAsync(arg[..split], arg[(split + 1)..], new() { Timeout = 10_000 });
                Console.WriteLine($"OK: pressed {arg[(split + 1)..]} on {arg[..split]}");
                break;
            }

            case "text":
            {
                var t = await page.Locator(arg).First.TextContentAsync();
                Console.WriteLine("OK: " + (t ?? "").Trim());
                break;
            }

            case "count":
                Console.WriteLine("OK: " + await page.Locator(arg).CountAsync());
                break;

            // Horizontal overflow is the single most common responsive defect, so it
            // gets a dedicated command rather than a remembered one-liner.
            case "overflow":
            {
                var report = await page.EvaluateAsync<JsonElement>(@"() => {
                    const de = document.documentElement;
                    const vw = window.innerWidth;
                    const offenders = [];
                    for (const el of document.querySelectorAll('*')) {
                        const r = el.getBoundingClientRect();
                        if (r.width === 0 && r.height === 0) continue;
                        if (r.right > vw + 1 || r.left < -1) {
                            offenders.push({
                                tag: el.tagName.toLowerCase(),
                                cls: (el.className && el.className.toString ? el.className.toString() : '').slice(0, 60),
                                left: Math.round(r.left),
                                right: Math.round(r.right)
                            });
                        }
                    }
                    return {
                        innerWidth: vw,
                        scrollWidth: de.scrollWidth,
                        overflowBy: de.scrollWidth - vw,
                        offenders: offenders.slice(0, 12)
                    };
                }");
                Console.WriteLine("OK: " + report.ToString());
                break;
            }

            // Reports controls below the 44x44 minimum. Takes a selector so a caller
            // can scope it to the region under test.
            case "touch":
            {
                var report = await page.EvaluateAsync<JsonElement>(@"(sel) => {
                    const small = [];
                    for (const el of document.querySelectorAll(sel)) {
                        const r = el.getBoundingClientRect();
                        if (r.width === 0 && r.height === 0) continue;
                        if (r.width < 44 || r.height < 44) {
                            small.push({
                                tag: el.tagName.toLowerCase(),
                                cls: (el.className && el.className.toString ? el.className.toString() : '').slice(0, 50),
                                w: Math.round(r.width * 10) / 10,
                                h: Math.round(r.height * 10) / 10
                            });
                        }
                    }
                    return small.slice(0, 25);
                }", arg);
                Console.WriteLine("OK: " + report.ToString());
                break;
            }

            case "wait":
                await Task.Delay(int.Parse(arg));
                Console.WriteLine("OK: waited");
                break;

            case "quit":
                Console.WriteLine("OK: bye");
                // Context first: that's what writes a persistent profile's cookies
                // out, so a signed-in session survives to the next invocation.
                await context.CloseAsync();
                if (browser is not null) await browser.CloseAsync();
                return exitCode;

            default:
                Console.WriteLine($"ERR: unknown command '{cmd}'");
                exitCode = 1;
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERR: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        exitCode = 1;
    }
}

// Closing the context is what flushes a persistent profile's cookies to disk, so it
// has to happen before the browser goes — and in profile mode there is no separate
// browser handle to close.
await context.CloseAsync();
if (browser is not null) await browser.CloseAsync();
return exitCode;

// Waits until the document's scrollWidth stops changing across consecutive animation
// frames. Without this, measuring straight after a viewport change or a navigation
// can catch a mid-relayout frame: a sweep over 12 routes x 9 widths reported two
// large phantom overflows (/about at 640px, /contact at 360px) that were not
// reproducible in isolation and did not exist in the page.
static async Task SettleAsync(IPage page)
{
    await page.EvaluateAsync(@"async () => {
        const frame = () => new Promise(r => requestAnimationFrame(() => r()));
        let last = -1;
        for (let i = 0; i < 10; i++) {
            await frame();
            const w = document.documentElement.scrollWidth;
            if (w === last) return;
            last = w;
        }
    }");
}
