# Invoice Manager

Local-first receipt and invoice manager. See [PLAN.md](PLAN.md) for scope and implementation decisions.

## Prerequisites

- .NET 10 SDK
- Node.js and npm
- Docker Desktop with Docker Compose

## Receipt privacy

Real receipts and everything derived from them are private: keep originals, extracted data, database backups, reports and sensitive screenshots/logs out of Git. Use synthetic test fixtures only. Store documents in ignored local `storage` and keep exports/backups outside the repository. Review staged paths and content before committing; `.gitignore` does not protect already tracked or force-added files.

The public blank Skatteverket template and application artwork are allowed exceptions. Foundry features can send document data to external services: enable/use them only with explicit authorization for the data and destination. Generated HTML summaries contain private financial information even though generation is local.

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

New uploads are then sent to the configured Responses API deployment. Successful extraction fills draft metadata, stores the raw structured result, generates a 1536-dimensional embedding from the extracted summary, and queues the receipt as `NeedsReview`. It also offers evidence-grounded **unconfirmed** expense-classification suggestions; purchased materials alone cannot establish their use or the previous standard. Failed or disabled extraction leaves manual entry available. No property/sale facts are sent by this feature. Use only synthetic documents for unapproved environments.

## Import receipts from a folder

On the **Review** page, select **Import folder** and choose a local folder. The browser includes files from its subfolders; only PDF, JPG/JPEG, PNG, and WEBP files are uploaded. Each selected file goes through the normal upload validation and exact-content duplicate check. New files are added to the review queue, not automatically approved. Unsupported files are skipped, and invalid or failed uploads are reported individually while the remaining files continue. The UI shows progress and lets you cancel the remaining uploads; a request already in flight may still finish, so check the archive after cancellation.

The browser sends the selected files to the local API; it does not send local folder paths or grant the API permission to scan arbitrary directories. Different scans or photos of the same receipt are not detected as duplicates and remain for human review.

## Property workspaces

The database migration creates a default **Main property** workspace and assigns existing receipts to it. Use the property selector in the header to change the default workspace, or choose **Add property** to create another. Changing the default affects future single-file uploads, folder imports, archive browsing, review queue, and receipt questions; it does not move previously imported receipts.

Each receipt belongs to exactly one property. Exact-file duplicate detection is scoped to the selected property, so the same file can be independently added to another property's workspace. The default is a UI preselection only: uploads (including folder imports), archive browsing, review queues, and questions always send the selected property ID. Receipt-specific routes use the receipt's stored ownership. The API also supports `GET /api/properties`, `POST /api/properties`, and `PUT /api/properties/{id}/default`.

## Classify expenses during receipt review

In **Review**, correct the receipt details, then use **What was this receipt for?** before **Mark reviewed**. For one kind of expense, select **Use the whole receipt amount**. Add another **expense portion** only when the receipt contains different kinds of expenses. You can also create portions from receipt items or scan suggestions; these append new entries, so check for duplicates. Mixed receipts may contain **Basic improvement**, **Repairs and maintenance**, and **Excluded / not deductible** portions. Scan suggestions are never automatically confirmed. Expense portions have stable IDs linked to the original receipt, independent of replaceable extracted line-item IDs.

Each confirmed portion needs its description, actual work/expense date, amount and classification. Included portions also require work details explaining use and previous/new standard; excluded portions require an explicit reason. These are evidence-completeness requirements, **not an eligibility decision**. ROT, insurance/subsidies and adjustment questions are postponed: no adjustment values are required, unknown values are not replaced with zero, and previously saved values are preserved when omitted in review saves. No adjustments are subtracted by the preparation summary.

The first version supports **SEK only**. Currency textboxes are read-only SEK; new uploads and missing extracted currencies default to SEK. Existing or explicitly extracted foreign currencies remain stored and display an unsupported-currency warning, and cannot be approved or enter confirmed report totals. **Correct a misread currency to SEK** requires explicit confirmation that the original amounts are already SEK; it changes no amounts and requires re-confirmation. Genuinely foreign-currency receipts remain unsupported. Archive/Q&A retain their stored currency labels; nothing is converted or bulk relabelled.

Approval requires explicit confirmation of every portion and exact coverage of the receipt total with included and excluded SEK amounts. **Save draft** allows incomplete and overlapping allocations without a receipt-total cap. Amounts retain four-decimal precision through receipt review and expense storage. Negative receipts/credits are not supported; a zero-value receipt can be explicitly excluded. No currency conversion, deduction calculations, direct PDF generation or official worksheet filling is implemented.

Receipt corrections, expense portions and confirmation save atomically. Confirmation records a timestamp and receipt review version. Relevant receipt edits invalidate expense confirmations; expense edits require re-confirmation. Unchanged confirmed inputs retain their confirmation timestamp. Conflicting concurrent edits return 409 and retain unsaved input for correction/reload.

Open **Improvements** at `/improvements` to save property type, acquisition date and sale date once, see classified evidence and missing report facts, or open a receipt to correct its expense facts. Reviewed receipts are listed with bounded pagination, including legacy receipts needing classification confirmation. Existing Completed receipts and old ledger drafts are preserved, but no tax classification is approved during migration. Repairs require later work-specific sale-condition/wear and latest-repair assessments; sale-year eligibility and full rule/worksheet verification remain future work. Property facts alone do not guarantee a complete report.

In-page navigation/property changes and reload/close warn about unsaved input; browser history navigation may discard unsaved changes. Receipt deletion returns **409 Conflict** while expense evidence references it. Remove linked portions through receipt review and save a draft before deleting the receipt; exclusion retains evidence rather than deleting it.

`PUT /api/documents/{id}/review` accepts the full metadata/line-item snapshot and an optional `expenses` snapshot; expense saves require the returned `reviewVersion`. Omitted expense IDs create portions; supplied IDs must belong to that receipt; omitted existing portions are removed. `markReviewed: true` requires SEK and complete confirmed portions for new approvals. Omitted/null adjustment fields preserve previously saved facts. Legacy metadata-only edits can preserve Completed status but do not confer tax readiness. `GET /api/properties/{propertyId}/readiness` lists future tax worksheet blockers separately from the available MVP preparation summary.

Sale facts and the legacy draft API remain property-scoped: `GET/PUT /api/properties/{propertyId}/sale-facts`, `GET/POST /api/properties/{propertyId}/expenses`, and `PUT/DELETE /api/properties/{propertyId}/expenses/{expenseId}`. Legacy expense writes still require reviewed same-property evidence and invalidate receipt-linked classifications; they cannot confirm expenses or move existing splits to another receipt. Use receipt review as the primary editing path. Decimal values accept JSON numbers or invariant decimal strings; missing values use `null`. Unknown properties/entries return 404 and invalid data returns 400. Probable business-expense duplicate reconciliation remains deferred.

## MVP expense preparation summary

On **Improvements**, select **Preview expense summary** or **Download expense summary**. The download is a self-contained HTML file: open it in a browser and use **Print / Save as PDF**, or print the preview. Property type/acquisition/sale facts are optional for this summary. Save property edits first; reports use committed data, do not call AI, and never modify receipts.

The report lists recorded SEK expenses by expense year and classification, exact annual/category totals, descriptions and receipt/expense-portion IDs. **Excluded expenses are separate and never enter potential-improvement totals.** A receipt contributes only when its source is reviewed, all portions have current confirmations and complete MVP facts, all currencies are SEK, and its portions exactly reconcile to the receipt total. If any portion fails these checks, the entire receipt is listed as unresolved outside confirmed totals. Partial and empty summaries are allowed and clearly labelled; omitted amounts are not zero.

**This is not a deductible total, SKV 2197 or a tax declaration.** ROT, insurance/subsidies, thresholds, sale-year windows, condition/wear and eligibility are not assessed. Recorded amounts may already reflect adjustments on the original invoice; no extra adjustment is applied. Never transfer these totals directly into a declaration.

`GET /api/properties/{propertyId}/expense-report` returns preview data and encoded printable HTML; `/expense-report/download` returns an HTML attachment with a generated safe filename. Each generation reads a consistent property-scoped database snapshot and includes a timestamp/version. A later download regenerates current data and may differ from a preview. References identify local originals; receipt files are not embedded. No migration or PDF library is required for this feature.

## Browse the receipt archive

The **Archive** page at `/archive` shows receipts in the selected property workspace, including reviewed documents, grouped by document year (newest first) with undated receipts last. Sort within each year by category, document date, vendor, or stored amount in either direction. Amount sorting does not convert currencies; use the currency filter when comparing amounts.

Combine year/date-range, category, vendor, currency, and review-status filters, then select **Apply**. Active filters can be removed individually or cleared together. Undated and uncategorized receipts have explicit filter choices. Pages contain up to 25 receipts. Select a receipt to view its details, line items, and original file without losing filters or ordering. Client-side navigation preserves archive state when visiting other pages and returning. The **Review** page at `/review` retains the existing review workflow.

`GET /api/documents/archive` returns a page plus selected-property filter choices. It accepts `propertyId` and the filters `year`, `undated`, `dateFrom`, `dateTo`, `category`, `uncategorized`, `vendor`, `currency`, `status`, `sortBy` (`date`, `category`, `vendor`, `amount`), `direction` (`asc`, `desc`), `page`, and `pageSize` (1-100, default 25). An explicit, non-empty `propertyId` query parameter is required; missing, malformed, or empty GUIDs return HTTP 400. Category and vendor filters are case-insensitive exact matches. Browsing uses PostgreSQL only, without AI calls.

## Correct an approved receipt

Open a reviewed receipt in the archive and select **Edit receipt**. The edit page at `/archive/{id}/edit` includes metadata, line items and expense classification. **Save draft changes** keeps ordinary Completed status while changed expense confirmations become pending. **Confirm and save** validates all known facts/allocations and returns to the archive with its existing filters and sorting. **Cancel** discards edits after an unsaved-change warning. The receipt ID, original file, and content hash are preserved; saved corrections immediately affect archive filters and PostgreSQL totals. Invalid input leaves stored data unchanged.

Content changes invalidate the stored embedding and its model metadata so old document content cannot be used for vector matching. Unchanged normalized content retains its embedding. Editing does not call Foundry or automatically regenerate embeddings: invalidated receipts remain available through archive browsing and SQL queries, and may appear in the bounded recent-receipt fallback, but are excluded from vector matching until re-embedded. Embedding backfill remains deferred.

API clients should send the returned review version and full metadata, line-item and expense snapshots, not only changed fields.

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

Vite supports direct navigation and refreshes at `/archive`, `/archive/{id}/edit`, `/review`, and `/improvements`. Any future static deployment must serve the SPA entry point for these frontend routes while keeping `/api` requests routed to the backend.

```powershell
dotnet build InvoiceManager.sln
npm --prefix frontend run build
```
