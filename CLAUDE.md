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
dotnet run                                        # Development profile, http://localhost:5180 (Swagger at /swagger)
dotnet run -- create-admin <user> "<name>" [pw]   # seeds an Admin account (only way to make one); prints the password
dotnet run -- dump-pdf <file.pdf>                 # runs the packing-slip parser and prints what it extracted
dotnet ef migrations add <Name>
dotnet ef database update
```

No automated test suite.

### Local prerequisites

A running local PostgreSQL, plus two `dotnet user-secrets` values (never committed —
`appsettings.json` has placeholders only):

```bash
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=DigitalBox;Username=postgres;Password=<yours>"
dotnet user-secrets set "Jwt:Key" "<random 32+ byte string>"
```

Then `dotnet ef database update` and `dotnet run -- create-admin <user> "<name>"` to get a login.
`Jwt:AccessTokenHours` defaults to 8.

## Architecture

**Stack**: ASP.NET Core 9 Web API, EF Core + `Npgsql.EntityFrameworkCore.PostgreSQL`,
controllers (no minimal APIs), `UglyToad.PdfPig` for PDF text extraction, `CsvHelper` for the
inventory-report CSV. Swashbuckle pinned to **9.0.6** (10.x has breaking `Microsoft.OpenApi`
changes — Henderson gotcha). No ASP.NET Identity — a hand-rolled user table (see Auth below).

**`Program.cs`** is the composition root: DI + JWT bearer + CORS + Swagger, then two CLI
command branches (`create-admin`, `dump-pdf`) that run and exit before `app.Run()`, then the
HTTP pipeline with a global exception handler returning `{ message }`.

**Auth** — per-user accounts (`Entities/User`, `Data` table `Users`). Username-only (no email),
two roles (`User` / `Admin`). `AuthController.Login` looks the user up by lower-cased username,
requires `IsActive`, PBKDF2-verifies against `PasswordHash` (`Services/PasswordHasher.cs`),
issues an 8h JWT (`Services/JwtTokenService.cs`, lifetime from `Jwt:AccessTokenHours`) carrying
`sub`/role/`stamp` claims, applies an in-memory per-IP lockout (`Services/LoginThrottle.cs`,
**50** failures / 15 min → `423`) and a 400ms delay on failure. The JWT bearer
`OnTokenValidated` event re-reads the user on **every** request and rejects the token if the
account is gone, deactivated, or its `SecurityStamp` changed — so deactivation and password
resets take effect immediately. Passwords are **admin-issued only**: `UsersController`
(`[Authorize(Roles=Admin)]`) creates users and resets passwords, always returning a
system-generated passphrase once (`Services/PasswordGenerator.cs`) — there is no self-service
and no set-your-own-password. New accounts are always `User`; **admins are seeded only via
`dotnet run -- create-admin`**. `UserService` holds the shared normalize/construct helpers.
Every controller except `HealthController` and `AuthController.Login` is `[Authorize]`.
`Order.ActionedByUserId` / `OrderEvent.ActorUserId` are soft (FK-less) references to the acting
user; `ActionedBy` / `Actor` keep the display-name snapshot for old rows and renames.

**Data model** (`Data/ApplicationDbContext.cs`, `Entities/`):
- `Order` — `OrderNumber`, `Marketplace` (enum→string), `ShipDate` (DateOnly?), `Status`
  (`Open`/`Shipped`/`Cancelled`), `ParseStatus` (`Parsed`/`NeedsReview`/`Failed`), `IsPriority`,
  `Notes` (both feed the queue sort / `SearchText`), `SearchText` (normalized blob of order # +
  item titles/skus + note, GIN `pg_trgm` index), `ActionedBy`, timestamps. Composite index
  `(Status, IsPriority)`.
- `OrderLineItem` — title / quantity / sku / sortOrder, cascade-deleted with the order.
- `PackingSlip` — the uploaded PDF bytes in a `bytea` column; `Sha256` is unique and is how
  duplicate uploads are rejected. Access goes through `IPackingSlipStore` so the bytes can
  move to S3 later without touching callers.
- `OrderEvent` — append-only audit (`Created`/`Shipped`/`Cancelled`/`Edited`/`Reopened`). Backs
  the history views. Never set `Id` on a new event added to a tracked `order.Events` (EF emits
  UPDATE→"affected 0" — the child-PK bug hit twice this project).
- `User` — login accounts; see **Auth** above. `Users` table, unique lower-cased `Username`.

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
`GET /` (q / marketplace / **priority** / status / sort / page — priority orders lead the Open
queue under every sort; every branch ends `.ThenBy(o => o.Id)` for stable pagination), `GET /{id}`,
`GET /{id}/packing-slip` (streams the PDF), `PUT /{id}` (correct parsed fields — Open orders only,
409s otherwise), `POST /{id}/priority` (any status, no event), `PUT /{id}/notes` (any status —
that's why it's separate from `PUT /{id}`, no event), `POST /ship`, `POST /cancel`, `POST /undo`
(all take just `orderIds[]` — the actor is the signed-in user, from the JWT; undo reopens
Shipped/Cancelled → Open, appends `Reopened`, keeps priority/notes).

**User admin** (`Controllers/UsersController.cs`, `api/users`, `[Authorize(Roles=Admin)]`):
`GET /` (list), `POST /` (`{username,displayName}` → user + one-time `generatedPassword`),
`POST /{id}/reset-password`, `POST /{id}/deactivate` + `/activate`, `PUT /{id}` (rename).

**Shippable Items report** (`Controllers/ReportsController.cs`, `api/reports/shippable-items`):
multipart CSV upload + `skuColumn`/`titleColumn`/`qtyColumn` form fields (UI maps them),
cross-references against open-order line items, returns a JSON preview (UI builds the download
CSV). `Services/ShippableItemsReport.cs` is the pure algorithm (port of the old
`InventoryCheckWorker.js` — case-insensitive SKU-or-title match, `-<digit>` variant skip,
blank on-hand → 0); `Services/InventoryCsv.cs` is the CsvHelper wrapper. Synchronous, in-memory
— no worker thread / polling / temp files.

**CORS** is a named policy; origin from `Cors:AllowedOrigin` (env `Cors__AllowedOrigin`),
falls back to the Vite dev origin `http://localhost:5173`.

## Deployment (mirror Henderson — not yet wired)

`Dockerfile`, `Caddyfile`, `.github/workflows/deploy-api.yml` are in place, modeled on
Henderson's. Target: EC2 + Docker behind Caddy (auto-HTTPS), RDS Postgres, image via ECR,
deploy on `master` push through GitHub Actions OIDC + SSM Run Command (no SSH). Secrets in
SSM Parameter Store `/digitalbox/prod/*` → `/etc/digitalbox-api.env` → `docker run --env-file`
(`ConnectionStrings__Default`, `Jwt__Key`, `Cors__AllowedOrigin` — no `Auth__*` any more).
The workflow needs repo `vars`: `AWS_REGION`, `ECR_REPOSITORY`, `EC2_INSTANCE_ID`,
`AWS_DEPLOY_ROLE_ARN`. Migrations to RDS: `dotnet ef migrations bundle --self-contained
-r linux-x64` run from the instance (RDS isn't publicly reachable), with
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`. **After the first migration**, seed the admin once
on the instance: `docker run --rm --env-file /etc/digitalbox-api.env <image> create-admin
<user> "<name>"` — note the printed passphrase and hand it to the owner.

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
