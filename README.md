# Invoice Manager

Local-first receipt and invoice manager. See [PLAN.md](PLAN.md) for scope and implementation decisions.

## Prerequisites

- .NET 10 SDK
- Node.js and npm
- Docker Desktop with Docker Compose

## Local PostgreSQL

Copy `.env.example` to `.env`, set a local-only development password, then start PostgreSQL:

```powershell
Copy-Item .env.example .env
# Edit .env and set POSTGRES_PASSWORD
docker compose up -d
```

PostgreSQL is published only on `127.0.0.1:5432`. The Compose service uses the pgvector PostgreSQL 17 image and a named volume.

## API

Set the connection string in User Secrets, using the same local password as `.env`:

```powershell
dotnet user-secrets set "ConnectionStrings:Postgres" "Host=localhost;Port=5432;Database=invoice_manager;Username=invoice_manager;Password=YOUR_LOCAL_PASSWORD" --project InvoiceManager.Api
```

Replace `YOUR_LOCAL_PASSWORD` locally; do not commit or share the value.

```powershell
dotnet run --project InvoiceManager.Api
```

API local settings and credentials should be supplied through .NET User Secrets or the organization's approved identity configuration. Do not commit credentials or real receipt files.

## Frontend

```powershell
cd frontend
npm install
npm run dev
```

## Build checks

```powershell
dotnet build InvoiceManager.sln
npm --prefix frontend run build
```
