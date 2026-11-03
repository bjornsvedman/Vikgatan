# Plan: Local Receipt and Invoice Manager

Build a local-first receipt and invoice manager with a .NET 10 LTS Minimal API, React + TypeScript frontend using Vite, PostgreSQL/pgvector, and Microsoft Foundry for document extraction and answers, pending confirmation of available models and approvals. The first version focuses on reliable ingestion, human review of extracted values, structured and semantic search, and answers cited to source documents. Defer OneDrive and automatic file watching.

## Steps

1. **Foundation:** Create the .NET 10 API, Vite frontend, and Docker Compose PostgreSQL service with pgvector and persistent storage. Confirm which Foundry models are available, whether they can be deployed, and whether their use is approved for the document data. Check region, quota, and embedding model availability. Separately check the Azure credits and entitlements included with the Visual Studio Professional subscription; optionally use an isolated Azure dev/test resource if Foundry is available there. Personal-subscription access does not imply Volvo approval. Do not send real receipts to it without authorization. Use the prescribed local authentication method, and define allowed document directories and upload limits.
2. **Data and application model:** Keep the application modular without adding distributed services. Store each document's ID, original filename, controlled storage path, content hash, document date, vendor, currency, total amount, category, description, extracted JSON, processing and review status, errors, and embedding model/version. Represent invoice line items explicitly. Add EF Core migrations and indexes for structured filters and duplicate detection. Use `Pgvector.EntityFrameworkCore` and choose a fixed embedding model and vector dimension.
3. **AI integration:** Define separate interfaces for document extraction, answer generation, and embeddings so providers can be replaced without assuming their APIs are interchangeable. Implement adapters for approved Microsoft Foundry deployments, with structured-output validation. Confirm the selected models support required image or PDF input and JSON behavior; determine whether PDF rasterization or a separate document service is needed. Record validation issues and confidence where available, and re-embed documents if the embedding model or dimensions change. Keep Gemini as an alternative only if it is separately approved and useful.
4. **Ingestion and review:** Start with a manually triggered scan of a configured folder and/or single-document upload. Make scans a `POST` action, deduplicate using content hashes, and track queued, processing, completed, failed, and review-needed states. Save extracted fields and line items, then provide an endpoint and UI for reviewing and editing them. Restrict file access to configured paths and validate file type and size. Defer a background watcher until there is a demonstrated need.
5. **Retrieval and cited answers:** Use PostgreSQL filters and aggregates for exact dates, vendors, categories, and costs, and pgvector for semantic matches on descriptions and item text. Treat stored dates and amounts as authoritative; let the model explain retrieved evidence, not invent facts or calculate authoritative totals. Include document IDs and relevant fields in citations. Serve images through an ID-based endpoint that resolves files only under the configured document directory.
6. **Frontend and streaming:** Build the Vite UI with a query/chat view, cited document results and thumbnails, and ingestion and review views. For queries sent as `POST`, stream responses with `fetch`; browser `EventSource` only supports `GET`. Define cancellation, error, and completion behavior. Keep the frontend as a local-focused SPA; server-side rendering is not needed for the current scope.
7. **Testing and evaluation:** Test extraction validation, idempotent ingestion, storage-path containment, structured filters and aggregates, and citations. Create a small synthetic golden dataset with expected document IDs and exact-value answers. Measure retrieval Recall@K separately from answer correctness and latency.
8. **Deferred phase:** Add Microsoft Graph/OneDrive sync and change notifications after the local workflow is proven useful. Account for authentication, conflicts, and remote changes.

## Relevant Files

- `docker-compose.yml` — PostgreSQL/pgvector service and persistent volume.
- `InvoiceManager.sln` — .NET solution.
- `InvoiceManager.Api/` — API, application services, Foundry adapters, EF Core persistence, and endpoints.
- `InvoiceManager.Api/appsettings.json` and local identity configuration — non-secret defaults and approved Foundry authentication.
- `frontend/` — Vite React + TypeScript app.
- `evals/` — synthetic evaluation cases and expected retrieval results.

## Verification

1. Confirm an approved Foundry deployment is callable from local development, supports the required document input and structured extraction, and provides an embedding model. Verify authentication, region, quota, and data approval. Check subscription credits and entitlements before any optional Azure dev deployment, keeping it separate from Volvo-approved resources. Start PostgreSQL, apply migrations, and confirm pgvector is available.
2. Ingest the same sample file twice and verify it creates only one record. Verify visible failures for malformed, unsupported, or oversized files.
3. Verify invalid or uncertain extracted fields require review and that edits persist.
4. Test exact filters and monetary aggregates independently from semantic search; verify citations map to source records.
5. Verify the image endpoint cannot serve files outside the configured document directory.
6. Run backend and frontend tests and builds; exercise streamed POST queries, cancellation, and errors in the browser.
7. Evaluate retrieval and answer quality separately, reporting Recall@K, exact-value correctness, and latency.

## Decisions

- Use .NET 10 LTS rather than .NET 9, which is out of support as of November 2026.
- Keep Vite; this is a local SPA consuming a .NET API.
- Use relational queries for exact facts and totals, and pgvector for semantic recall.
- Keep the first phase local and manually triggered. Defer OneDrive, watchers, and distributed infrastructure.
- Prefer Microsoft Foundry behind task-specific abstractions, pending confirmation of approved deployments, model capabilities, embeddings, quota, region, and data handling. Keep Gemini as an optional approved alternative.
- For streamed POST queries, use `fetch` rather than native browser `EventSource`.
- Keep the architecture modular and proportionate; avoid microservices and unnecessary framework layers.
