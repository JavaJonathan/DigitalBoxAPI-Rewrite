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

# 3. Schema + first account + run
dotnet ef database update
dotnet run -- create-admin owner "Owner Name"   # prints a generated passphrase — save it
dotnet run                                      # http://localhost:5180  — Swagger UI at /swagger
```

Sign in with the admin account from step 3. That admin adds everyone else (and resets
passwords) from the **Users** screen in the UI; new passwords are generated and shown once.

See [CLAUDE.md](CLAUDE.md) for architecture and deployment details.
