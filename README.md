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

## Microsoft Foundry embeddings

The embedding provider calls the Azure OpenAI-compatible `/openai/v1/embeddings` route. Keep the approved resource endpoint and deployment name in User Secrets, not source files:

```powershell
az login
az account set --subscription "<approved subscription>"
dotnet user-secrets set "Foundry:Embeddings:Endpoint" "https://<resource>.services.ai.azure.com" --project InvoiceManager.Api
dotnet user-secrets set "Foundry:Embeddings:Deployment" "<embedding deployment name>" --project InvoiceManager.Api
```

The local identity needs inference access to the Azure AI resource (typically the **Cognitive Services OpenAI User** role). The configured vector size is 1536, matching the tested `text-embedding-3-small` output. If the deployment or dimension changes, update the schema and re-embed existing documents before using it. Never commit API keys, access tokens, or real receipts.

## Microsoft Foundry receipt extraction

Extraction is disabled by default. After confirming the deployment is approved for the documents you intend to process, enable it locally and set the vision deployment in User Secrets:

```powershell
dotnet user-secrets set "Foundry:Extraction:Enabled" "true" --project InvoiceManager.Api
dotnet user-secrets set "Foundry:Extraction:Endpoint" "https://<resource>.services.ai.azure.com" --project InvoiceManager.Api
dotnet user-secrets set "Foundry:Extraction:Deployment" "<vision deployment name>" --project InvoiceManager.Api
```

New uploads are then sent to the configured Responses API deployment. Successful extraction fills draft metadata, stores the raw structured result, generates a 1536-dimensional embedding from the extracted summary, and queues the receipt as `NeedsReview`. Failed extraction leaves the original file available for manual entry. Use only synthetic documents for unapproved environments.

## Browse the receipt archive

The **Archive** page at `/archive` shows all stored receipts, including reviewed documents, grouped by document year (newest first) with undated receipts last. Sort within each year by category, document date, vendor, or stored amount in either direction. Amount sorting does not convert currencies; use the currency filter when comparing amounts.

Combine year/date-range, category, vendor, currency, and review-status filters, then select **Apply**. Active filters can be removed individually or cleared together. Undated and uncategorized receipts have explicit filter choices. Pages contain up to 25 receipts. Select a receipt to view its read-only details, line items, and original file without losing filters or ordering. Client-side navigation preserves archive state when visiting other pages and returning. The **Review** page at `/review` retains the existing review workflow; editing already reviewed receipts from the archive remains future work.

`GET /api/documents/archive` returns a page plus archive-wide filter choices. Supported query parameters are `year`, `undated`, `dateFrom`, `dateTo`, `category`, `uncategorized`, `vendor`, `currency`, `status`, `sortBy` (`date`, `category`, `vendor`, `amount`), `direction` (`asc`, `desc`), `page`, and `pageSize` (1-100, default 25). Category and vendor filters are case-insensitive exact matches. Browsing uses PostgreSQL only, without AI calls.

## Ask questions about receipts

The home page at `/` focuses on **Ask your receipts**, with natural-language questions and cited answers. Archive browsing and review are on their own pages. `POST /api/documents/query` classifies the request, then either embeds it to find similar receipts or calculates exact totals directly in PostgreSQL. Exact totals can be filtered or grouped by year, vendor, and category, and are kept separate by currency; only reviewed receipts are counted. Semantic answers use a bounded context that includes recent receipts without embeddings when the collection is small. Answers include citations that open the original document. The model does not calculate authoritative totals.

## Frontend

```powershell
cd frontend
npm install
npm run dev
```

## Build checks

Vite supports direct navigation and refreshes at `/archive` and `/review`. Any future static deployment must serve the SPA entry point for these frontend routes while keeping `/api` requests routed to the backend.

```powershell
dotnet build InvoiceManager.sln
npm --prefix frontend run build
```
