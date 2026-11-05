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
dotnet ef database update --project InvoiceManager.Api
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

## Import receipts from a folder

On the **Review** page, select **Import folder** and choose a local folder. The browser includes files from its subfolders; only PDF, JPG/JPEG, PNG, and WEBP files are uploaded. Each selected file goes through the normal upload validation and exact-content duplicate check. New files are added to the review queue, not automatically approved. Unsupported files are skipped, and invalid or failed uploads are reported individually while the remaining files continue. The UI shows progress and lets you cancel the remaining uploads; a request already in flight may still finish, so check the archive after cancellation.

The browser sends the selected files to the local API; it does not send local folder paths or grant the API permission to scan arbitrary directories. Different scans or photos of the same receipt are not detected as duplicates and remain for human review.

## Property workspaces

The database migration creates a default **Main property** workspace and assigns existing receipts to it. Use the property selector in the header to change the default workspace, or choose **Add property** to create another. Changing the default affects future single-file uploads, folder imports, archive browsing, review queue, and receipt questions; it does not move previously imported receipts.

Each receipt belongs to exactly one property. Exact-file duplicate detection is scoped to the selected property, so the same file can be independently added to another property's workspace. The default is a UI preselection only: uploads (including folder imports), archive browsing, review queues, and questions always send the selected property ID. Receipt-specific routes use the receipt's stored ownership. The API also supports `GET /api/properties`, `POST /api/properties`, and `PUT /api/properties/{id}/default`.

## Browse the receipt archive

The **Archive** page at `/archive` shows receipts in the selected property workspace, including reviewed documents, grouped by document year (newest first) with undated receipts last. Sort within each year by category, document date, vendor, or stored amount in either direction. Amount sorting does not convert currencies; use the currency filter when comparing amounts.

Combine year/date-range, category, vendor, currency, and review-status filters, then select **Apply**. Active filters can be removed individually or cleared together. Undated and uncategorized receipts have explicit filter choices. Pages contain up to 25 receipts. Select a receipt to view its details, line items, and original file without losing filters or ordering. Client-side navigation preserves archive state when visiting other pages and returning. The **Review** page at `/review` retains the existing review workflow.

`GET /api/documents/archive` returns a page plus selected-property filter choices. It accepts `propertyId` and the filters `year`, `undated`, `dateFrom`, `dateTo`, `category`, `uncategorized`, `vendor`, `currency`, `status`, `sortBy` (`date`, `category`, `vendor`, `amount`), `direction` (`asc`, `desc`), `page`, and `pageSize` (1-100, default 25). An explicit, non-empty `propertyId` query parameter is required; missing, malformed, or empty GUIDs return HTTP 400. Category and vendor filters are case-insensitive exact matches. Browsing uses PostgreSQL only, without AI calls.

## Correct an approved receipt

Open a reviewed receipt in the archive and select **Edit receipt**. The edit page at `/archive/{id}/edit` reuses review validation for metadata and line items. **Save changes** keeps the receipt approved and returns to the archive with its existing filters and sorting. **Cancel** discards the edit without saving. The receipt ID, original file, and content hash are preserved; saved corrections immediately affect archive filters and PostgreSQL totals. Invalid input leaves the stored receipt unchanged and keeps the draft available for correction.

Content changes invalidate the stored embedding and its model metadata so old document content cannot be used for vector matching. Unchanged normalized content retains its embedding. Editing does not call Foundry or automatically regenerate embeddings: invalidated receipts remain available through archive browsing and SQL queries, and may appear in the bounded recent-receipt fallback, but are excluded from vector matching until re-embedded. Embedding backfill remains deferred.

API clients can use `PUT /api/documents/{id}/review` with `markReviewed: true` for approved corrections. Send the full metadata and line-item snapshot, not only the changed field.

## Ask questions about receipts

The home page at `/` focuses on **Ask your receipts**, with natural-language questions and cited answers. Archive browsing and review are on their own pages. `POST /api/documents/query` accepts `propertyId` and classifies the request, then either embeds it to find similar receipts or calculates exact totals directly in PostgreSQL. An explicit, non-empty `propertyId` query parameter is required; missing, malformed, or empty GUIDs return HTTP 400. Exact totals can be filtered or grouped by year, vendor, and category, and are kept separate by currency; only reviewed receipts in the selected property are counted. Semantic answers use a bounded context that includes recent receipts without embeddings when the collection is small. Answers include citations that open the original document. The model does not calculate authoritative totals.

SQL answers show the actual interpreted year, vendor, category, grouping, and **Reviewed receipts only** scope. Expand **Calculation breakdown** to inspect every contributing receipt and stored amount, grouped with its SQL total. Summary source cards remain limited to 20, but the calculation breakdown is complete. Calculation amounts are sent as invariant decimal strings, so the browser neither recalculates nor rounds away stored precision. SQL totals and contributions are read from a consistent database snapshot. Empty results still show the interpreted filters.

Common English spending-total requests are routed to SQL even if the planner labels them descriptive. Explicit receipt-discovery questions remain descriptive unless the planner selects a total. Unsupported calculations such as averages, counts, or percentages are declined rather than silently treated as sums. Descriptive model prose containing numeric characters, common English number words, or financial calculation language is replaced with a safe message and verified source links; stored source-card values remain available. This is intentionally conservative and may suppress otherwise useful descriptive wording. These finite English-language guards are defense in depth, not a guarantee of correct interpretation for every wording: inspect the displayed filters and original receipts. Missing-amount disclosures, unreviewed-receipt warnings, and receipt-level arithmetic consistency checks remain out of scope.

## Frontend

```powershell
cd frontend
npm install
npm run dev
```

## Build checks

Vite supports direct navigation and refreshes at `/archive`, `/archive/{id}/edit`, and `/review`. Any future static deployment must serve the SPA entry point for these frontend routes while keeping `/api` requests routed to the backend.

```powershell
dotnet build InvoiceManager.sln
npm --prefix frontend run build
```
