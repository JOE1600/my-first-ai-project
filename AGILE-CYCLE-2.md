# Agile Cycle 2: Security Hardening and Live Fixtures

**Project:** Boxwood x Islington Hotel x JackJumpers, Game Night Escape website
**Cycle:** 2 (follows the Cycle 1 summary in `BOXWOOD-AGILE-CYCLE-SUMMARY.pdf`)
**Date:** 27 September 2026
**Status:** Local MVP hardened. Not yet production-ready (see [Action required: .NET 8](#action-required-upgrade-to-net-8)).

---

## Summary

Cycle 1 produced a working website and a basic enquiry API. Its summary listed several open risks: an open CORS policy, no staff authentication, a simple in-memory rate limiter, guest data stored in plain text, and generic imagery.

In Cycle 2 the team worked through most of those risks:

- **The API is locked down.** A new application firewall, strict CORS, security headers, request limits, a honeypot for bots and a protected manager key were added.
- **Guest data is encrypted in the database.** Owner-only file permissions, daily backups and a 180-day retention policy were also added.
- **Real fixtures replace free-text game choices.** Upcoming JackJumpers home games are read from the official schedule, and the API only accepts games that are on that list.
- **The website no longer loads code from third parties.** Fonts and the scrolling library are now served from the site itself, behind a strict Content-Security-Policy.
- **The public deployment only publishes public files.** The backend source and the staff manager page are no longer published to GitHub Pages.
- **The API stays on .NET 6 for now,** because the development Mac only has the .NET 6 SDK. This is a known risk that must be fixed before launch.

---

## What changed, how, and what it does

### 1. Backend security (`backend/Security.cs`, `backend/Program.cs`, `backend/appsettings.json`)

| Change | How it was done | What it does |
| --- | --- | --- |
| **Application firewall** | New middleware (`UseApplicationFirewall`) that runs before every endpoint | Checks each request against the blocklist and bans, the method allowlist, content type and rate limits before the request reaches any API code |
| **Blocklist and temporary bans** | `Security:BlockedIps`, plus an in-memory `ClientGuard` that counts strikes per address | Blocks known bad addresses (403). Scanners probing paths such as `/.env` or `/wp-admin` are banned for 15 minutes after 3 attempts, and anyone guessing the manager key is locked out after 5 wrong keys |
| **Rate limiting** | `ClientGuard.TryConsumeRequest` and `TryAcceptEnquiry` replace the old `SubmissionLimiter` | Allows at most 60 requests per minute per address and one enquiry every 15 seconds. Excess requests get a clear 429 message |
| **Request size limits** | Kestrel limits in `Program.cs` | Rejects bodies over 8 KB (413), oversized headers, and slow header uploads (15-second timeout, which protects against slow-loris attacks) |
| **Method and content-type allowlist** | Firewall middleware | Only GET, HEAD, POST and OPTIONS are allowed (405 otherwise). POSTs must be JSON (415 otherwise) |
| **Strict CORS** | `WithOrigins(Security:AllowedOrigins)` replaces `AllowAnyOrigin()` | Only the real website can call the API from a browser. Other sites are refused |
| **Host header check** | `AllowedHosts` set to `localhost;127.0.0.1` instead of `*` | Rejects requests with a spoofed Host header (400) |
| **Security headers** | `UseApiSecurityHeaders` middleware | Adds `nosniff`, `X-Frame-Options: DENY`, a `default-src 'none'` CSP and `Cache-Control: no-store`, and removes the `Server` header |
| **HTTPS and HSTS** | `UseHttpsRedirection` and `UseHsts` outside Development | Forces encrypted connections in production |
| **Trusted proxies only** | `ForwardedHeaders` limited to `Security:TrustedProxies` and `TrustedNetworks` | A client can no longer fake its IP address with `X-Forwarded-For` to dodge limits or bans |
| **Manager key hardening** | The key is read only from the `BOXWOOD_ADMIN_API_KEY` environment variable, must be at least 24 characters, and is compared in constant time | The key is never in source code. Outside Development the API refuses to start without a strong key, and timing attacks cannot reveal it |
| **Honeypot field** | Hidden `website` input on the form. The API accepts the request but stores nothing if the field is filled | Bots get a fake success and learn nothing. Real guests never see the field |
| **Stricter input validation** | Control-character checks on every field, and the game choice must match an official fixture | Stops injection-style junk and tampered requests that invent a fixture |
| **Access logging** | Firewall and manager events are logged | Staff can see bans, lockouts, honeypot hits and who read the enquiry list |

### 2. Guest data protection (`backend/GuestData.cs`)

| Change | How it was done | What it does |
| --- | --- | --- |
| **Encryption at rest** | Names, emails and notes are encrypted with **AES-256-CBC plus an HMAC-SHA256 integrity tag** (encrypt-then-MAC). Separate keys are derived with HKDF from `BOXWOOD_DATA_KEY` | A copied database file or backup cannot be read without the key, and a value that has been edited is detected and rejected |
| **Automatic migration** | At startup, any plain-text rows are encrypted in place | Enquiries saved before encryption was switched on are protected too |
| **IP address privacy** | Only a keyed hash of each client address is stored | The team can still spot repeat senders, but raw IP addresses are no longer kept |
| **File permissions** | `app_data/` is set to 700 and the database files to 600 (owner only) | Other users on the server cannot read the database |
| **Backups** | Background job (`GuestDataMaintenance`) using SQLite's online backup | Takes a backup every 24 hours and keeps the last 14 copies in `app_data/backups/` (Git-ignored) |
| **Retention** | The same job deletes enquiries older than 180 days | Guest data is not kept longer than the business needs it |

**Why CBC and HMAC instead of AES-GCM:** the first version used AES-GCM. During this cycle the team found that .NET 6 on macOS does not support AES-GCM, so saving an encrypted enquiry crashed with `PlatformNotSupportedException`. The team switched to AES-CBC with HMAC-SHA256, which gives the same confidentiality and tamper protection and works on every platform with both .NET 6 and .NET 8. Values in the older GCM format are still readable wherever GCM is available.

### 3. Live fixtures (`backend/ScheduleService.cs`, `backend/JackJumpersSchedule.cs`)

- **How:** the schedule fetch moved into a `ScheduleService`, which caches the fixture list for 15 minutes and keeps the last good list for 24 hours if the official site is down.
- **Parser hardening:** every regular expression has a 2-second timeout, which protects against ReDoS. Fixture slugs must match a strict pattern, duplicates are removed, at most 20 games are returned, and official links are rebuilt from the checked slug rather than copied from the page.
- **What it does:** the game picker shows real upcoming home games with opponent, date and tip-off time. The API rejects any game that is not on the official list.

### 4. Website (`index.html`, `script.js`, `styles.css`, `config.js`, `assets/`)

| Change | What it does |
| --- | --- |
| **Content-Security-Policy and referrer policy** | Blocks injected or inline scripts. The page can only talk to its own origin and the configured API |
| **Fonts and Lenis served locally** (`assets/fonts/`, `assets/vendor/`) | Removes the Google Fonts and unpkg dependencies, so no third-party script runs on the page and guest visits are not shared with those services |
| **`config.js`** | One place to set the API address. When it is empty, the form shows "Online enquiries open soon" instead of failing |
| **Responsive images** (`-480` and `-800` WebP versions) | Phones download smaller photos, so pages load faster |
| **Smoother motion** | Pointer effects are batched with `requestAnimationFrame`, so they no longer cause layout thrashing |
| **Friendlier errors** | The form shows the API's field-level message, a clear "please wait" on 429, and no longer tells guests to "start the backend" |
| **Favicon** | Adds a site icon (`assets/favicon.svg`) |

### 5. Staff manager page (`manager.html`, `manager.css`, `manager.js`)

- **How:** the inline CSS and JavaScript moved into separate files.
- **What it does:** the page runs under a strict CSP (`default-src 'none'`), sends no referrer, and tells search engines not to index it.

### 6. Deployment (`.github/workflows/deploy-pages.yml`, `.gitignore`)

- **Public files only:** GitHub Pages now publishes only the public site. Previously it published the whole repository, including the backend source and `manager.html`.
- **API address from a repository variable:** the workflow writes `config.js` from the `BOXWOOD_API_BASE` repository variable, which must be an `https://` address, and updates the page's CSP to match. If the variable is empty, the live site shows "Online enquiries open soon".
- **Secrets stay out of Git:** `.gitignore` now excludes `.env` files, certificates (`*.pem`, `*.pfx`), database backups and build output.

---

## Verification (run on 27 September 2026, .NET 6.0.202, macOS)

| Check | Expected | Result |
| --- | --- | --- |
| `dotnet build -c Release` | Builds cleanly | ✅ 0 errors, 0 warnings |
| JavaScript syntax (`script.js`, `manager.js`, `config.js`) | Valid | ✅ |
| `GET /api/health` | 200 `ok` | ✅ |
| `GET /api/games` | Live fixtures | ✅ 16 upcoming home games (first: Melbourne United, 1 Oct 2026) |
| Security headers | Present | ✅ nosniff, DENY, CSP, no-store |
| CORS from `localhost:8080` | Allowed | ✅ |
| CORS from another site | Refused | ✅ no allow-origin header |
| Spoofed Host header | 400 | ✅ |
| DELETE request | 405 | ✅ |
| Non-JSON POST | 415 | ✅ |
| 20 KB body | 413 | ✅ |
| Made-up game choice | 400 | ✅ |
| Honeypot filled | Fake 201, nothing stored | ✅ |
| Valid enquiry | 201 | ✅ |
| Repeat enquiry within 15 s | 429 | ✅ |
| Manager with correct key | 200 | ✅ |
| Manager with wrong key | 401 | ✅ |
| Probing `/.env` three times | Address banned (403) | ✅ |
| Encryption on | Database holds only `enc:v2:` values; manager sees plain text | ✅ |
| Plain-text rows when a key is added | Encrypted at startup | ✅ "Encrypted 1 enquiries…" |
| Tampered encrypted row | Rejected (integrity check) | ✅ |
| Database permissions | `app_data` 700, `boxwood.db` 600 | ✅ |
| Website at `localhost:8080` | Loads | ✅ 200 |

---

## What is working now

- The full website, with the Islington photo tour, the live JackJumpers fixture picker and the enquiry form.
- Enquiries are saved to SQLite and encrypted when `BOXWOOD_DATA_KEY` is set.
- The staff manager page lists enquiries using the manager key.
- The firewall, rate limits, bans, honeypot, CORS, header checks and security headers.
- Daily backups and 180-day retention.
- GitHub Pages deployment of the public site only.

## Known limitations

- **.NET 6 is out of support** (see the next section).
- **GitHub Pages is not enabled on the repository yet,** so the deploy workflow stops at "Configure Pages" (Not Found). This has happened on every deploy so far, not just this cycle. To fix it, open **Settings → Pages → Build and deployment → Source** and choose **GitHub Actions**, then re-run the workflow.
- The firewall, bans and rate limits are held in memory on one server. They reset when the API restarts and are not shared between servers. A network WAF (Cloudflare, Azure Front Door or AWS WAF) is still needed in production.
- SQLite is fine for a single server. Managed production storage is still recommended.
- A single tampered row makes the whole manager list fail (500) rather than skipping that row. This is safe, but not friendly.
- Still not built: email notifications, payments, ticket inventory, privacy notice and booking terms.
- `BOXWOOD_DATA_KEY` must be backed up somewhere safe. If it is lost, encrypted enquiries and backups cannot be recovered.

---

## Action required: upgrade to .NET 8

> **Note:** The API was upgraded to .NET 8 earlier in this cycle, then **moved back to .NET 6** because the development Mac only has the .NET 6 SDK (6.0.202) and cannot build .NET 8. All the security work above was kept and adjusted to build on .NET 6. **The project must move to .NET 8 LTS before it goes live.**

### What happens if it stays on .NET 6

- **No more security patches.** .NET 6 reached end of support on **12 November 2024**. Microsoft no longer fixes vulnerabilities in the .NET 6 runtime, ASP.NET Core 6 or the Kestrel web server. Any new flaw, such as a request-smuggling, denial-of-service or TLS bug, stays open in this API for good.
- **The firewall cannot fully protect it.** The in-app firewall filters requests, but it runs on top of Kestrel. A bug in Kestrel itself, or in the runtime, can be exploited before the firewall ever sees the request.
- **Hosting and compliance problems.** Some hosting platforms (for example Azure App Service) are removing .NET 6 images. Security reviews, cyber-insurance questionnaires and payment-provider checks usually flag unsupported runtimes, which could block launch or future payment integration.
- **Packages will stop updating.** New versions of `Microsoft.Data.Sqlite` and other libraries are dropping .NET 6 support, so future fixes will not install.
- **Workarounds stay in the code.** The code uses a manual `chmod` call and avoids newer APIs only because of .NET 6.

### What .NET 8 gives, and an important date

- Current security patches, native AES-GCM on macOS, `File.SetUnixFileMode`, the built-in ASP.NET Core rate-limiting middleware, and better performance.
- ⚠️ **.NET 8 support itself ends on 10 November 2026**, about six weeks after this cycle. Upgrading to .NET 8 fixes the immediate risk, but the project will be unsupported again soon after.
- **Recommendation:** go straight to **.NET 10 LTS**, which is supported until November 2028. The steps below are the same; use `net10.0` and the matching `10.0.x` package instead. If the team moves to .NET 8 first, plan the .NET 10 move before 10 November 2026.

### How to upgrade (about 30 minutes)

1. Install the .NET 8 SDK on the Mac: download it from https://dotnet.microsoft.com/download/dotnet/8.0, or run `brew install --cask dotnet-sdk@8`.
2. In `backend/backend.csproj`, change `net6.0` to `net8.0` and set `Microsoft.Data.Sqlite` to the latest `8.0.x`.
3. Optionally, in `backend/GuestData.cs`, replace the `chmod` P/Invoke with `File.SetUnixFileMode`.
4. Run `dotnet build`, then run the verification checks above again.

Existing encrypted data needs no migration, because the `enc:v2` format is identical on .NET 6 and .NET 8.

---

## Recommended next cycle (Cycle 3)

1. Upgrade the API off .NET 6, preferably straight to .NET 10 LTS (.NET 8 support ends 10 November 2026).
2. Choose production hosting for the API, put a WAF in front of it, and set `BOXWOOD_API_BASE`, `AllowedOrigins` and `AllowedHosts` to the real domains.
3. Store `BOXWOOD_ADMIN_API_KEY` and `BOXWOOD_DATA_KEY` in a secrets manager and back up the data key.
4. Add email notifications for new enquiries.
5. Publish the privacy notice, retention statement and booking terms.
6. Add enquiry statuses and follow-up notes to the manager page.
7. Complete a final accessibility and performance review on a staging environment.
