# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

The DigitalBox API — a from-scratch rewrite of the old Node/Express `DigitalBoxApi`
(`C:\Users\jonat\Documents\JSProjects\DigitalBoxAPI`). DigitalBox is an internal
warehouse-fulfillment tool for a multi-marketplace reseller (Amazon / eBay / Walmart /
Shopify). Warehouse staff **upload packing-slip PDFs**, the API parses each into an order
with line items, and staff search / filter / ship / cancel from the React UI
(`DigitalBoxUI`, separate repo).

The rewrite deliberately drops the old design's Google Drive dependency, its JSON-file
"database", and its shared-Google-identity auth. Architecture mirrors the
**Henderson Software Labs** project (`C:\Users\jonat\Documents\HendersonSoftwareLabs`) —
read that repo's `CLAUDE.md` for the deployment model this one follows.

## Commands

```bash
dotnet build
dotnet run                              # Development profile, http://localhost:5180 (Swagger at /swagger)
dotnet run -- set-password <plaintext>  # prints a PBKDF2 hash for Auth:PasswordHash
dotnet run -- dump-pdf <file.pdf>       # runs the packing-slip parser and prints what it extracted
dotnet ef migrations add <Name>
dotnet ef database update
```

No automated test suite.

### Local prerequisites

A running local PostgreSQL, plus three `dotnet user-secrets` values (never committed —
`appsettings.json` has placeholders only):

```bash
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=DigitalBox;Username=postgres;Password=<yours>"
dotnet user-secrets set "Jwt:Key" "<random 32+ byte string>"
dotnet user-secrets set "Auth:PasswordHash" "<output of: dotnet run -- set-password <pw>>"
```

`Auth:Username` defaults to `warehouse` in `appsettings.json`; override in secrets/SSM if desired.

## Architecture

**Stack**: ASP.NET Core 9 Web API, EF Core + `Npgsql.EntityFrameworkCore.PostgreSQL`,
controllers (no minimal APIs), `UglyToad.PdfPig` for PDF text extraction. Swashbuckle pinned
to **9.0.6** (10.x has breaking `Microsoft.OpenApi` changes — Henderson gotcha). No ASP.NET
Identity: auth is a single shared credential (see below).

**`Program.cs`** is the composition root: DI + JWT bearer + CORS + Swagger, then two CLI
command branches (`set-password`, `dump-pdf`) that run and exit before `app.Run()`, then the
HTTP pipeline with a global exception handler returning `{ message }`.

**Auth** — one shared username/password, no user table. `AuthController.Login` checks
`Auth:Username` + PBKDF2-verifies against `Auth:PasswordHash` (`Services/PasswordHasher.cs`),
issues a 12h JWT (`Services/JwtTokenService.cs`), and applies an in-memory per-IP lockout
(`Services/LoginThrottle.cs`, 5 failures / 15 min → `423`). Every controller except
`HealthController` and `AuthController.Login` is `[Authorize]`. There are no roles.

**Data model** (`Data/ApplicationDbContext.cs`, `Entities/`):
- `Order` — `OrderNumber`, `Marketplace` (enum→string), `ShipDate` (DateOnly?), `Status`
  (`Open`/`Shipped`/`Cancelled`), `ParseStatus` (`Parsed`/`NeedsReview`/`Failed`),
  `SearchText` (normalized blob, GIN `pg_trgm` index), `ActionedBy`, timestamps.
- `OrderLineItem` — title / quantity / sku / sortOrder, cascade-deleted with the order.
- `PackingSlip` — the uploaded PDF bytes in a `bytea` column; `Sha256` is unique and is how
  duplicate uploads are rejected. Access goes through `IPackingSlipStore` so the bytes can
  move to S3 later without touching callers.
- `OrderEvent` — append-only audit (`Created`/`Shipped`/`Cancelled`/`Edited`). Backs the
  history views and a future undo feature.

**Ingestion** (`Services/OrderIngestionService.cs`): per uploaded PDF — SHA-256 → dedupe
check → `IPackingSlipParser.Parse` → create Order + line items + slip + `Created` event in
one `SaveChanges`. Each file is its own unit so one bad file doesn't fail the batch.
`Confidence.Partial` → `ParseStatus.NeedsReview`; an exception → `Failed` (order still
created as a stub for manual entry).

**PDF parsing** (`Services/PackingSlipParser.cs`): PdfPig words → grouped into visual rows by
Y coordinate → regex anchors for "Order #" / "Ship Date" → locate the line-item table header
(`Description` + `Qty`) → read rows beneath it until a totals/footer marker. This replaces the
old `ContentHelper.js` token/URL-encoding state machine. It is heuristic — expect to tune
`FindLineItems` / the regexes against real slips per marketplace; use `dump-pdf`.

**Marketplace detection** (`Services/MarketplaceDetector.cs`): order-number shape heuristics
ported from the old `HttpHelper.filterForMarketplace`. Stored on the order at creation; an
operator can override via `PUT /api/orders/{id}`.

**Endpoints** (`Controllers/OrdersController.cs`): `POST /upload` (multipart, ≤50 files),
`GET /` (q / marketplace / status / sort / page), `GET /{id}`, `GET /{id}/packing-slip`
(streams the PDF), `PUT /{id}` (correct parsed fields — Open orders only),
`POST /ship`, `POST /cancel` (both take `orderIds[]` + `actionedBy`).

**CORS** is a named policy; origin from `Cors:AllowedOrigin` (env `Cors__AllowedOrigin`),
falls back to the Vite dev origin `http://localhost:5173`.

## Deployment (mirror Henderson — not yet wired)

`Dockerfile`, `Caddyfile`, `.github/workflows/deploy-api.yml` are in place, modeled on
Henderson's. Target: EC2 + Docker behind Caddy (auto-HTTPS), RDS Postgres, image via ECR,
deploy on `master` push through GitHub Actions OIDC + SSM Run Command (no SSH). Secrets in
SSM Parameter Store `/digitalbox/prod/*` → `/etc/digitalbox-api.env` → `docker run --env-file`.
The workflow needs repo `vars`: `AWS_REGION`, `ECR_REPOSITORY`, `EC2_INSTANCE_ID`,
`AWS_DEPLOY_ROLE_ARN`. Migrations to RDS: `dotnet ef migrations bundle --self-contained
-r linux-x64` run from the instance (RDS isn't publicly reachable), with
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`.

## Gotchas

- Only the .NET **10** SDK is installed on this machine, but the .NET 9 runtime is present,
  so `net9.0` builds and runs. Keep the target at `net9.0` to match Henderson's Docker base
  images and package versions.
- Pin any added `Microsoft.*` / EF Core package to the `9.0.x` line explicitly; a bare
  `dotnet add package` grabs a newer-TFM release that won't restore (Henderson hit this).
- `dump-pdf` is the fastest way to iterate on the parser — no DB or HTTP needed.
- Dev port is **5180**. Henderson's API dev profile uses 5194 and is often left running on
  this machine, so DigitalBox deliberately avoids it. The UI's `.env.local` and the CORS
  fallback must agree with whatever port is set here.
