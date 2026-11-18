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

## Start the app (Windows)

After the one-time `.env` and User Secrets setup above, start everything with either:

```powershell
.\start.ps1
```

Or double-click `start.cmd`. The launcher starts Docker Desktop if needed, brings up PostgreSQL, applies database migrations, starts the API and frontend in separate PowerShell windows, checks the frontend's API proxy, and opens the app at <http://127.0.0.1:5173/>. Close the API and frontend windows to stop those processes; PostgreSQL remains running until stopped with `docker compose stop`.

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

## Projects within properties

Projects organize **original receipt line items**, independently of tax-classified expense portions. A receipt still belongs to one property, and all its line items share a single project assignment. In Review, choose one project for the receipt; individual rows cannot override it. Unassigned is allowed. For receipts without items, **Create one item for the whole receipt amount** creates an editable item with the exact current amount; nothing is allocated automatically.

Create and rename projects from the **Admin** tab for the selected property. Project creation/renaming saves immediately; item assignments are made in Review and saved with the receipt. Names are unique case-insensitively within one property, not across properties. Renaming preserves assignments. Deletion, nesting and moving projects are deferred.

Archive tables/details show projects and support searchable project/Unassigned filters (including empty projects and receipts without items). Archive amounts remain **whole receipt totals**.

Ask your receipts is chat-only: the selected property is the scope, with no project search/filter or project-scope controls. Ask naturally about anything in that property's records, including projects; exact project names may be interpreted from the message within that property. Project spending uses reviewed **item line totals** and source receipt years/vendors, with separate currencies and complete item-level evidence. Missing amounts, unassigned/no-item evidence and receipt/item differences are disclosed; unmatched tax, shipping and discounts are never spread across projects. Project catalogs and joined names are not added to AI provider context; assignment/rename triggers no AI or re-embedding.

Project APIs: `GET/POST /api/properties/{propertyId}/projects`, `PUT /api/properties/{propertyId}/projects/{projectId}`. Archive, expenses and report endpoints accept optional `projectId` or `unassignedProject=true` (not both). The Ask chat sends the question and selected property only; project controls are not part of that UI. Review line snapshots carry stable `id` and optional `projectId`: omission preserves an existing assignment; explicit null clears it. Assigned receipts require a current `reviewVersion`; outdated ID-less replacement snapshots are rejected. Project-only changes preserve embeddings and classification confirmations. Apply all pending migrations before running the updated API; existing lines retain IDs, amounts and project assignments, while null project assignments remain Unassigned and legacy Category values are dropped.

Receipt and line-item **Category** fields have been removed. Use the receipt's property project (or **Unassigned**) to organize it; all line items on a receipt share the same assignment. Existing project assignments are preserved, and legacy category values are discarded. The tax-preparation expense classification described below is separate and remains in use.

## Classify expenses during receipt review

In **Review**, correct the receipt details, then choose a project and one expense classification side by side. Both choices apply to the entire receipt and all line items. The classification amount, currency, description, and date are taken from the receipt; line items remain supporting detail. Mixed expense types cannot be split into separate classifications.

Choosing a classification is the explicit confirmation for the receipt-wide type. This is a classification only, **not an eligibility decision**. ROT, insurance/subsidies and adjustment questions are postponed: no adjustment values are required, unknown values are not replaced with zero, and previously saved values are preserved when omitted in review saves.

The first version supports **SEK only**. Currency textboxes are read-only SEK; new uploads and missing extracted currencies default to SEK. Existing or explicitly extracted foreign currencies remain stored and display an unsupported-currency warning, and cannot be approved or enter confirmed report totals. **Correct a misread currency to SEK** requires explicit confirmation that the original amounts are already SEK; it changes no amounts and requires re-confirmation. Genuinely foreign-currency receipts remain unsupported. Archive/Q&A retain their stored currency labels; nothing is converted or bulk relabelled.

Approval requires a selected receipt-wide classification and uses the receipt's full total in SEK. **Save draft** remains available. Amounts retain four-decimal precision through receipt review and expense storage. Negative receipts/credits are not supported; a zero-value receipt can be explicitly excluded. No currency conversion, deduction calculations, direct PDF generation or official worksheet filling is implemented.

Receipt corrections, receipt-wide classification and confirmation save atomically. Confirmation records a timestamp and receipt review version. Relevant receipt edits invalidate classification confirmation; selecting a classification confirms its type. Unchanged confirmed inputs retain their confirmation timestamp. Conflicting concurrent edits return 409 and retain unsaved input for correction/reload.

The standalone Improvements tab and its preparation/readiness screens have been removed. Existing classification records and property sale facts remain stored. Older multiple classifications sharing one type are consolidated in the editor while preserving their details; records with different types require an explicit choice of one receipt-wide classification before replacing them. Do not remove the old records until the user saves that choice.

In-page navigation/property changes and reload/close warn about unsaved input; browser history navigation may discard unsaved changes. Receipt deletion returns **409 Conflict** while expense evidence references it. Remove linked portions through receipt review and save a draft before deleting the receipt; exclusion retains evidence rather than deleting it.

`PUT /api/documents/{id}/review` accepts the full metadata/line-item snapshot and an optional `expenses` snapshot; expense saves require the returned `reviewVersion`. Omitted expense IDs create records; supplied IDs must belong to that receipt; omitted existing records are removed on save. `markReviewed: true` requires exactly one receipt-wide expense record with complete confirmed facts in SEK and an amount matching the receipt total. Draft saves can retain older multiple records until the user consolidates them. Omitted/null adjustment fields preserve previously saved facts. Legacy metadata-only edits can preserve Completed status. The readiness, expense-report, and property sale-facts APIs remain available, but no standalone UI is provided for them.

Sale facts and the legacy draft API remain property-scoped: `GET/PUT /api/properties/{propertyId}/sale-facts`, `GET/POST /api/properties/{propertyId}/expenses`, and `PUT/DELETE /api/properties/{propertyId}/expenses/{expenseId}`. Legacy expense writes still require reviewed same-property evidence and invalidate receipt-linked classifications; they cannot confirm expenses or move existing splits to another receipt. Use receipt review as the primary editing path. Decimal values accept JSON numbers or invariant decimal strings; missing values use `null`. Unknown properties/entries return 404 and invalid data returns 400. Probable business-expense duplicate reconciliation remains deferred.

## Retained expense-preparation APIs (no standalone UI)


The report API lists recorded SEK expenses by expense year and classification, exact annual totals, descriptions and receipt/expense IDs. **Excluded expenses are separate and never enter potential-improvement totals.** A receipt contributes only when its source is reviewed, its single classification has a current confirmation and complete facts, its currency is SEK, and its amount exactly reconciles to the receipt total. Historical receipts with multiple classifications remain unresolved until consolidated. Partial and empty summaries are allowed and clearly labelled; omitted amounts are not zero.

**This is not a deductible total, SKV 2197 or a tax declaration.** ROT, insurance/subsidies, thresholds, sale-year windows, condition/wear and eligibility are not assessed. Recorded amounts may already reflect adjustments on the original invoice; no extra adjustment is applied. Never transfer these totals directly into a declaration.

`GET /api/properties/{propertyId}/expense-report` returns preview data and encoded printable HTML; `/expense-report/download` returns an HTML attachment with a generated safe filename. Each generation reads a consistent property-scoped database snapshot and includes a timestamp/version. A later download regenerates current data and may differ from a preview. References identify local originals; receipt files are not embedded. No migration or PDF library is required for this feature.

## Browse the receipt archive

The **Archive** page at `/archive` shows receipts in the selected property workspace, including reviewed documents, grouped by document year (newest first) with undated receipts last. Filter receipts by project and sort within each year by document date, vendor, or stored amount in either direction. Amount sorting does not convert currencies; use the currency filter when comparing amounts.

Combine project, year/date-range, vendor, currency, and review-status filters, then select **Apply**. Active filters can be removed individually or cleared together. Undated receipts have an explicit filter choice. Pages contain up to 25 receipts. Select a receipt to view its details, line items, and original file without losing filters or ordering. Client-side navigation preserves archive state when visiting other pages and returning. The **Review** page at `/review` retains the existing review workflow.

`GET /api/documents/archive` returns a page plus selected-property project/filter choices. It accepts `propertyId` and the filters `projectId`, `unassignedProject`, `year`, `undated`, `dateFrom`, `dateTo`, `vendor`, `currency`, `status`, `sortBy` (`date`, `vendor`, `amount`), `direction` (`asc`, `desc`), `page`, and `pageSize` (1-100, default 25). An explicit, non-empty `propertyId` query parameter is required; missing, malformed, or empty GUIDs return HTTP 400. Vendor filters are case-insensitive exact matches. Browsing uses PostgreSQL only, without AI calls.

## Correct an approved receipt

Open a reviewed receipt in the archive and select **Edit receipt**. The edit page at `/archive/{id}/edit` includes metadata, line items and expense classification. **Save draft changes** keeps ordinary Completed status while changed expense confirmations become pending. **Confirm and save** validates all known facts/allocations and returns to the archive with its existing filters and sorting. **Cancel** discards edits after an unsaved-change warning. The receipt ID, original file, and content hash are preserved; saved corrections immediately affect archive filters and PostgreSQL totals. Invalid input leaves stored data unchanged.

Content changes invalidate the stored embedding and its model metadata so old document content cannot be used for vector matching. Unchanged normalized content retains its embedding. Editing does not call Foundry or automatically regenerate embeddings: invalidated receipts remain available through archive browsing and SQL queries, and may appear in the bounded recent-receipt fallback, but are excluded from vector matching until re-embedded. Embedding backfill remains deferred.

API clients should send the returned review version and full metadata, line-item and expense snapshots, not only changed fields.

## Ask questions about receipts

The home page at `/` focuses on **Ask your receipts**, with natural-language questions and cited answers. Archive browsing and review are on their own pages. `POST /api/documents/query` accepts `propertyId` and classifies the request, then either embeds it to find similar receipts or calculates exact totals directly in PostgreSQL. An explicit, non-empty `propertyId` query parameter is required; missing, malformed, or empty GUIDs return HTTP 400. Receipt totals can be filtered or grouped by year and vendor; project-specific totals use reviewed line-item amounts grouped by project. All amounts are kept separate by currency and limited to the selected property. Semantic answers use a bounded context that includes recent receipts without embeddings when the collection is small. Answers include citations that open the original document. The model does not calculate authoritative totals.

SQL answers show the actual interpreted year, vendor, project grouping, and **Reviewed receipts only** scope. Expand **Calculation breakdown** to inspect every contributing receipt and stored amount, grouped with its SQL total. Summary source cards remain limited to 20, but the calculation breakdown is complete. Calculation amounts are sent as invariant decimal strings, so the browser neither recalculates nor rounds away stored precision. SQL totals and contributions are read from a consistent database snapshot. Empty results still show the interpreted filters.

Common English spending-total requests are routed to SQL even if the planner labels them descriptive. Explicit receipt-discovery questions remain descriptive unless the planner selects a total. Unsupported calculations such as averages, counts, or percentages are declined rather than silently treated as sums. Descriptive model prose containing numeric characters, common English number words, or financial calculation language is replaced with a safe message and verified source links; stored source-card values remain available. This is intentionally conservative and may suppress otherwise useful descriptive wording. These finite English-language guards are defense in depth, not a guarantee of correct interpretation for every wording: inspect the displayed filters and original receipts. Missing-amount disclosures, unreviewed-receipt warnings, and receipt-level arithmetic consistency checks remain out of scope.

## Frontend

```powershell
cd frontend
npm install
npm run dev
```

## Build checks

Vite supports direct navigation and refreshes at `/archive`, `/archive/{id}/edit`, `/review`, and `/admin`. Any future static deployment must serve the SPA entry point for these frontend routes while keeping `/api` requests routed to the backend.

```powershell
dotnet build InvoiceManager.sln
npm --prefix frontend run build
```
