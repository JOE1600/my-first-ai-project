# Boxwood enquiry API

This is the simplest local backend for the Boxwood x Islington x JackJumpers page:

- ASP.NET Core Minimal API
- SQLite database stored at `backend/app_data/boxwood.db`
- `POST /api/enquiries` for website enquiries
- `GET /api/health` for a health check
- Basic in-memory per-client cooldown to reduce duplicate submissions

## Run locally

```bash
cd backend
dotnet run --urls http://localhost:5050
```

Then open the parent `index.html` in a browser or serve the parent folder with a local static server. The frontend sends enquiries to `http://localhost:5050` by default.

To use another API host, define `window.BOXWOOD_API_BASE` before `script.js` loads.

This is intentionally a first backend milestone. Before production use, restrict CORS to the real site origin, move rate limiting to a distributed store, add authentication/authorisation for staff access, and configure backups and secret management.
