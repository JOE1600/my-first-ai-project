# Boxwood enquiry API

This is the local backend for the Boxwood x Islington x JackJumpers page:

- ASP.NET Core Minimal API
- SQLite database stored at `backend/app_data/boxwood.db`
- `GET /api/games` for upcoming JackJumpers home fixtures, read from the official schedule and cached for 15 minutes (the last good list is kept for 24 hours if the official site is down)
- `POST /api/enquiries` for website enquiries
- `GET /api/enquiries` for authorised manager access
- `GET /api/health` for a health check

## Run locally

```bash
cd backend
export BOXWOOD_ADMIN_API_KEY="$(openssl rand -base64 32)"
echo "$BOXWOOD_ADMIN_API_KEY"   # paste this into manager.html; it is not stored anywhere else
export BOXWOOD_DATA_KEY="..."   # optional in Development; reuse the same key every run or old enquiries cannot be read
dotnet run --urls http://localhost:5050
```

Serve the parent folder with a static server (for example `python3 -m http.server 8080` from the project root) and open `http://localhost:8080`. The frontend sends requests to `http://localhost:5050` by default.
The live game picker reads `GET /api/games`; the API needs outbound HTTPS access to `www.jackjumpers.com.au` for the latest fixture list.

The page is served with a Content-Security-Policy, so inline scripts are blocked. To use another API host locally, change `window.BOXWOOD_API_BASE` in `config.js` **and** the `connect-src` entry in the CSP `<meta>` tag of `index.html` (and `manager.html`). On GitHub Pages the deploy workflow does both from the `BOXWOOD_API_BASE` repository variable (leave it empty to show "Online enquiries open soon").

## Security

Secrets are never stored in source code or `appsettings.json`:

- The manager key comes only from the `BOXWOOD_ADMIN_API_KEY` environment variable. Outside Development the API refuses to start if it is missing or shorter than 24 characters.
- Keys are compared in constant time. Five wrong keys from one address lock that address out for 15 minutes.

The application firewall (`Security.cs`) runs before every endpoint:

| Layer | What it stops |
| --- | --- |
| Blocklist (`Security:BlockedIps`) | Known bad addresses, rejected with 403 |
| Temporary bans | Scanners probing paths such as `/.env` or `/wp-admin` (3 strikes) and manager-key guessing (5 strikes) are banned for 15 minutes |
| Method allowlist | Anything other than GET, HEAD, POST and OPTIONS |
| Body limits | Request bodies over 8 KB, oversized headers, and slow header uploads |
| Content-type check | Non-JSON POSTs |
| Rate limit | More than 60 requests per minute per address, plus a 15-second cooldown between enquiries |
| Honeypot field | Bots that fill in the hidden `website` field get a fake success and nothing is stored |
| Input checks | Control characters, and game choices that are not in the official fixture list |

It also enforces these rules:

- **CORS** only allows the origins in `Security:AllowedOrigins`.
- **Host headers** must match `AllowedHosts`.
- **Security headers** (`nosniff`, `DENY` framing, `no-store`, a strict CSP) are added to every response, and the `Server` header is removed.
- **Outside Development**, the API redirects to HTTPS and sends HSTS.

`X-Forwarded-For` is ignored unless the request comes from an address listed in `Security:TrustedProxies`. This stops a client from choosing its own IP address to dodge limits or bans.

### Before going live

1. Add the real site origin to `Security:AllowedOrigins` and the API hostname to `AllowedHosts`.
2. If the API sits behind a reverse proxy or CDN, add the proxy's address to `Security:TrustedProxies`.
3. Put a network firewall or WAF in front of the API (for example Cloudflare, Azure Front Door or AWS WAF). Only ports 80 and 443 should be reachable. The in-app firewall is per-server and in-memory, so it resets on restart and is not shared between instances.
4. Upgrade from .NET 6 to .NET 8 LTS (see below).
5. Set `BOXWOOD_DATA_KEY` (`openssl rand -base64 32`) and keep a copy somewhere safe. Without it, encrypted enquiries and backups cannot be read.

## Guest data protection

- Guest names, emails and notes are encrypted before they are written to SQLite (AES-256-CBC with an HMAC-SHA256 integrity tag, keys derived from `BOXWOOD_DATA_KEY`). A copied database file or backup is unreadable without the key, and an edited value is rejected.
- Client IP addresses are stored only as a keyed hash.
- `app_data/` and the database files are restricted to the owner (700/600).
- A background job backs up the database every `Data:BackupIntervalHours`, keeps `Data:BackupsToKeep` copies, and deletes enquiries older than `Data:RetentionDays`.
- Outside Development the API refuses to start without `BOXWOOD_DATA_KEY`. In Development it logs a warning and stores plaintext.

## .NET version

The API currently targets **.NET 6** (`backend.csproj`) because the development Mac only has the .NET 6 SDK (6.0.202). The code was checked to build with 0 warnings on .NET 6 and avoids .NET 7/8-only APIs.

**This must move to .NET 8 LTS before launch.** .NET 6 reached end of support on 12 November 2024, so it no longer receives security fixes. Any vulnerability found in the runtime, ASP.NET Core or Kestrel after that date stays open in this API. To upgrade:

1. Install the .NET 8 SDK.
2. In `backend.csproj`, set `<TargetFramework>net8.0</TargetFramework>` and `Microsoft.Data.Sqlite` to the latest `8.0.x`.
3. Optionally, replace the `chmod` P/Invoke in `GuestData.cs` with `File.SetUnixFileMode`.
4. Run `dotnet build`, then re-run the checks in `AGILE-CYCLE-2.md`.

Note: .NET 8 support itself ends on 10 November 2026. .NET 10 LTS (supported until November 2028) is the better target, and the steps are the same with `net10.0`.

## View saved enquiries

Open `http://localhost:8080/manager.html`, enter the API address and the value of `BOXWOOD_ADMIN_API_KEY`, then select **Load enquiries**. Opening the file directly from disk (`file://`) is blocked by CORS on purpose.

The manager page is not published to GitHub Pages. Never put the manager key in frontend source code or commit it to Git.
