# EgymGarminSync web UI — first vertical slice

This first iteration introduces an ASP.NET Core 10 local-only API (`src/EgymGarminSync.Web`) and a React/TypeScript/Vite UI (`web`). The frontend defaults to an explicitly labeled **demo** with EGYM sessions on the left, a proposed/manual Garmin session on the right, and a per-set comparison below. Every weight or repetition value can be staged independently using the center arrows. 'Alles overnemen' and 'Ongedaan maken' modify frontend draft state only. Saving to Garmin is deliberately **disabled**.

The local API is bound to `127.0.0.1:5187` and exposes `GET /api/health`, `GET /api/egym/sessions?days=180`, and disabled Garmin/sync endpoints. It reads EGYM credentials from environment variables **on the server only**. The Garmin OAuth Windows DPAPI flow still lives in the CLI and is *not* hooked up to the web API. In live EGYM mode, the comparison remains unavailable rather than showing demo Garmin data as real data.

Run in separate PowerShell terminals:

```powershell
dotnet run --project src/EgymGarminSync.Web/EgymGarminSync.Web.csproj
```

```powershell
cd web
npm install
npm run dev
```

Open `http://127.0.0.1:5173`. The API needs `EGYM_EMAIL`, `EGYM_PASSWORD` and optionally `EGYM_BASE_URL` for live EGYM readback. No accounts, local data or secrets are persisted by the web slice.

Next slices: securely reuse Garmin auth inside a local-only API, expose real matched session choices, verify per-set pairing and edited repetition semantics, connect real read-only comparison, and only then enable the existing guarded backup/PUT/readback workflow. The historical CLI remains unchanged.
