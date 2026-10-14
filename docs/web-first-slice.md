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


## Read-only Garmin session integration (2026-10-14)
The local web backend now reads recent real Garmin EGYM activities through the same `WindowsGarminTokenCache` implementation as the CLI. Start it from the **same Windows user account** with `GARMIN_EMAIL` and `GARMIN_PASSWORD` available to the API process, as well as the EGYM environment variables. Garmin tokens remain DPAPI encrypted and server-side; no tokens are returned to React. The API is explicitly local-only.

The EGYM listing returns UTC-normalized completion times and training methods; the Garmin listing returns UTC start times and Garmin positive/negative names. The UI proposes a match only if exactly one Garmin EGYM activity is within the existing 0–120-minute window and has the matching training variant. When ambiguous or unsupported, no match is auto-selected; the user may choose any Garmin training in the dropdown. This first implementation computes the candidate in the UI using the same conservative time/method criteria; the authoritative session matcher still exists in the Matching library.

Live exercise sets are **not yet displayed**: switching to real sessions deliberately hides the demonstration comparison rather than showing mock weights as true. All Garmin mutations remain disabled. The next slice is real two-sided set comparison, including source-order-safe per-set pairing and an offline-only save preview.
