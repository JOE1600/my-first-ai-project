# Boxwood enquiry API

This is the local backend for the Boxwood x Islington x JackJumpers page:

- ASP.NET Core Minimal API
- SQLite database stored at `backend/app_data/boxwood.db`
- `GET /api/games` for upcoming JackJumpers home fixtures, read from the official schedule and cached for 15 minutes (the last good list is kept for 24 hours if the official site is down)
- `POST /api/enquiries` for website enquiries
- `GET /api/enquiries` for authorised manager access
- `GET /api/health` for a health check

## Run locally

Locally the project runs as two separate programs on two ports:

| Port | Program | Job |
| --- | --- | --- |
| 8080 | `python3 -m http.server 8080` in the project root | Serves the website files (`index.html`, `manager.html`, CSS, JS, images). It only hands out files. |
| 5050 | This API (`dotnet run`) | Fixtures, enquiries and the SQLite database. |

The browser loads the page from 8080; the page reads `config.js` to find the API on 5050 and calls it. The API only accepts browser calls from the origins in `Security:AllowedOrigins` (by default `http://localhost:8080`), so the live parts do not work if the site is served from another port.

```bash
# Terminal 1: the website
python3 -m http.server 8080          # from the project root

# Terminal 2: the API
cd backend
export BOXWOOD_ADMIN_API_KEY="..."   # first time: openssl rand -base64 32, then keep reusing it
export BOXWOOD_DATA_KEY="..."        # reuse the same key every run or old enquiries cannot be read
dotnet run --urls http://localhost:5050
```

Open `http://localhost:8080`. Stop each program with Ctrl+C. Stop the API before `dotnet build -c Release`, because a running API holds the files in `bin/Release` open and the build fails with a file-lock error.

The live game picker reads `GET /api/games`; the API needs outbound HTTPS access to `www.jackjumpers.com.au` for the latest fixture list.

The site does not need the API to list matches. Every GitHub Pages deploy (on each push to `main`, and automatically every 6 hours) runs `dotnet run -- --export-fixtures _site/fixtures.json`, which uses the same parser to publish the upcoming home games as a static file. The page tries `GET /api/games` first and falls back to `fixtures.json`, skipping games that have already been played. If the official site is down during a deploy, that step is skipped and the page shows its "check the official schedule" fallback.

The page is served with a Content-Security-Policy, so inline scripts are blocked. To use another API host locally, change `window.BOXWOOD_API_BASE` in `config.js` **and** the `connect-src` entry in the CSP `<meta>` tag of `index.html`. On GitHub Pages the deploy workflow does both from the `BOXWOOD_API_BASE` repository variable (leave it empty to show "Online enquiries open soon").

## Hosting publicly

GitHub Pages already serves the website worldwide, but it can only host static files. The API and its database need a host that runs a container with a persistent disk. `backend/Dockerfile` works on any of them (Fly.io, Render, Railway, Azure Container Apps, or a VPS with Docker).

The image builds the API for **.NET 10 LTS**, while your Mac keeps building it for .NET 6. `backend.csproj` defaults to `net6.0`, and the Dockerfile passes `-p:BoxwoodTargetFramework=net10.0`. Both targets build with 0 warnings.

1. Create the app on your chosen host from `backend/Dockerfile` (build context: the `backend` folder). The container listens on port **8080**.
2. Attach a persistent volume at **`/data`**. Without it every redeploy starts with an empty database.
3. Set the environment variables below as secrets in the host's dashboard. Never commit them.
4. Point the host's health check at **`/api/health`**. Other paths count as scanning and get the caller banned.
5. Give the API a hostname with HTTPS (the host normally provides this, for example `https://boxwood-api.fly.dev`, or add your own domain).
6. In the GitHub repository, go to Settings → Secrets and variables → Actions → Variables and set `BOXWOOD_API_BASE` to that address. The next Pages deploy points the live site at the API.

| Variable | Example | Notes |
| --- | --- | --- |
| `BOXWOOD_ADMIN_API_KEY` | `openssl rand -base64 32` | Required. The key you type into the manager page. |
| `BOXWOOD_DATA_KEY` | `openssl rand -base64 32` | Required. Encrypts guest data. Keep a copy somewhere safe: without it the database and backups cannot be read. |
| `AllowedHosts` | `boxwood-api.fly.dev` | The API's public hostname. |
| `Security__AllowedOrigins__0` | `https://joe1600.github.io` | The website origin (scheme and host only, no path). |
| `Security__AllowedOrigins__1` | `http://localhost:8080` | Lets the manager page, served from your laptop, talk to the hosted API. |
| `Security__TrustedNetworks__0` | `10.0.0.0/8` | The host's proxy network (see its docs). **Required behind a proxy**: without it every visitor appears to come from the proxy's address, so rate limits and bans would hit everyone at once. |
| `Email__...`, `BOXWOOD_SMTP_PASSWORD` | see below | Optional manager emails. |

Build and try the image anywhere Docker is installed:

```bash
docker build -t boxwood-api backend
docker run -p 8080:8080 -v boxwood-data:/data --env-file boxwood.env boxwood-api
```

(`*.env` files are git-ignored, so `boxwood.env` stays on your machine.)

## Where the data is stored

Enquiries live in one SQLite file, `boxwood.db`, in the data folder:

- **Locally:** `backend/app_data/boxwood.db`.
- **Hosted:** `/data/boxwood.db` on the persistent volume (set by `BOXWOOD_DATA_DIR`, which the Docker image sets to `/data`).

Daily backups go to `backups/` in the same folder, and enquiries older than `Data:RetentionDays` (180) are deleted. Guest names, emails and notes are encrypted inside the file (see [Guest data protection](#guest-data-protection)). SQLite is right for one API instance. Running several instances at once would need a shared database such as PostgreSQL instead.

To move existing local enquiries to the server, copy `app_data/boxwood.db` onto the volume **and** use the same `BOXWOOD_DATA_KEY` there.

## Manager email notifications

When `Email:SmtpHost`, `Email:From` and `Email:ManagerAddress` are all set, the API emails the manager about every new enquiry. Any SMTP service works (for example Resend, SendGrid, Postmark, Amazon SES, Microsoft 365 or Gmail with an app password).

| Variable | Example |
| --- | --- |
| `Email__SmtpHost` | `smtp.resend.com` |
| `Email__SmtpPort` | `587` (STARTTLS; port 465 is not supported) |
| `Email__SmtpUsername` | `resend` |
| `BOXWOOD_SMTP_PASSWORD` | the service's SMTP password or API key (secret) |
| `Email__From` | `enquiries@your-domain.com` (must be a sender the service has verified) |
| `Email__ManagerAddress` | `manager@your-domain.com` (separate several with commas) |
| `Email__ManagerPageUrl` | optional link added to each email |

- The email contains the guest's name, email, chosen match and preferences, with the time in Hobart time. **Reply-To** is the guest, so the manager can simply hit Reply.
- Emails are sent in the background. If the mail service is down the enquiry is still saved, and sending is retried 3 times and then logged, without guest details in the log.
- Setting only some of the three required values stops the API from starting outside Development, so a typo cannot silently turn emails off.
- Email leaves the encrypted database: treat the manager inbox as confidential too.

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
4. Deploy with the Docker image, which runs on .NET 10 LTS (see below).
5. Set `BOXWOOD_DATA_KEY` (`openssl rand -base64 32`) and keep a copy somewhere safe. Without it, encrypted enquiries and backups cannot be read.

## Guest data protection

- Guest names, emails and notes are encrypted before they are written to SQLite (AES-256-CBC with an HMAC-SHA256 integrity tag, keys derived from `BOXWOOD_DATA_KEY`). A copied database file or backup is unreadable without the key, and an edited value is rejected.
- Client IP addresses are stored only as a keyed hash.
- `app_data/` and the database files are restricted to the owner (700/600).
- A background job backs up the database every `Data:BackupIntervalHours`, keeps `Data:BackupsToKeep` copies, and deletes enquiries older than `Data:RetentionDays`.
- Outside Development the API refuses to start without `BOXWOOD_DATA_KEY`. In Development it logs a warning and stores plaintext.

## .NET version

Local builds target **.NET 6** because the development Mac only has the .NET 6 SDK (6.0.202). .NET 6 reached end of support on 12 November 2024 and no longer receives security fixes, so it is only for local development. The Docker image builds the same code for **.NET 10 LTS** (supported until November 2028), and version-specific APIs are switched with `#if` so both targets build cleanly.

Both targets pin `SQLitePCLRaw.bundle_e_sqlite3` 2.1.13, because 2.1.11 and older bundle a SQLite version with a high-severity vulnerability (GHSA-2m69-gcr7-jv3q).

To move local development to .NET 10 as well, install the .NET 10 SDK and change the `BoxwoodTargetFramework` default in `backend.csproj` to `net10.0`.

## View saved enquiries

Open `http://localhost:8080/manager.html`, enter the API address (`http://localhost:5050` locally, or the hosted `https://...` address) and the value of `BOXWOOD_ADMIN_API_KEY`, then select **Load enquiries**. Opening the file directly from disk (`file://`) is blocked by CORS on purpose.

The manager page shows totals, search and a match filter. Each enquiry has **Reply** (opens your mail app addressed to the guest) and **Copy email** buttons, and **Export CSV** downloads the enquiries currently shown. The API address is remembered on that device. The key is never stored, and the page locks itself after 30 minutes without activity.

The manager page is not published to GitHub Pages. Never put the manager key in frontend source code or commit it to Git.
