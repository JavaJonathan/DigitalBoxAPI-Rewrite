# DigitalBox API

ASP.NET Core 9 + PostgreSQL backend for DigitalBox, an internal warehouse-fulfillment tool.
Warehouse staff upload packing-slip PDFs; the API parses each into an order with line items,
and staff search / filter / ship / cancel from the [DigitalBox UI](https://github.com/JavaJonathan/DigitalBoxUI-Rewrite).

## Quick start

```bash
# 1. Postgres database
createdb DigitalBox   # or: CREATE DATABASE "DigitalBox";

# 2. Secrets (not committed)
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=DigitalBox;Username=postgres;Password=YOURPASS"
dotnet user-secrets set "Jwt:Key" "$(openssl rand -base64 48)"
dotnet user-secrets set "Auth:PasswordHash" "$(dotnet run -- set-password chooseapassword | tail -1)"

# 3. Schema + run
dotnet ef database update
dotnet run          # http://localhost:5180  — Swagger UI at /swagger
```

Default login username is `warehouse` (change via `Auth:Username`). The password is whatever
you passed to `set-password`.

See [CLAUDE.md](CLAUDE.md) for architecture and deployment details.
