import { useEffect, useRef, useState, type FormEvent } from 'react'
import { ArrowDown, ArrowUp, ChevronLeft, ChevronRight, ExternalLink, FileText, Filter, RotateCcw, X } from 'lucide-react'
import type { Receipt, ReceiptStatus } from './ReviewDesk'
import './ReceiptArchive.css'

type ArchivePage = {
  items: Receipt[]
  totalCount: number
  page: number
  pageSize: number
  years: number[]
  categories: string[]
  vendors: string[]
  currencies: string[]
}

type Filters = {
  year: string
  dateFrom: string
  dateTo: string
  category: string
  vendor: string
  currency: string
  status: string
}

const emptyFilters: Filters = { year: '', dateFrom: '', dateTo: '', category: '', vendor: '', currency: '', status: '' }
const filterLabels: Record<keyof Filters, string> = {
  year: 'Year', dateFrom: 'From', dateTo: 'To', category: 'Category', vendor: 'Vendor', currency: 'Currency', status: 'Status',
}
const statuses: ReceiptStatus[] = ['Completed', 'NeedsReview', 'Pending', 'Processing', 'Failed']

function amountLabel(amount: number | null, currency: string | null) {
  return amount === null ? 'Not set' : `${currency ?? ''} ${new Intl.NumberFormat('en-SE', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(amount)}`.trim()
}

function statusLabel(status: string) {
  return status === 'Completed' ? 'Reviewed' : status === 'NeedsReview' ? 'Needs review' : status
}

function ReceiptArchive({ active, refreshVersion }: { active: boolean, refreshVersion: number }) {
  const [filters, setFilters] = useState<Filters>(emptyFilters)
  const [applied, setApplied] = useState<Filters>(emptyFilters)
  const [sortBy, setSortBy] = useState('date')
  const [direction, setDirection] = useState('desc')
  const [page, setPage] = useState(1)
  const [result, setResult] = useState<ArchivePage | null>(null)
  const [pending, setPending] = useState(true)
  const [error, setError] = useState('')
  const [retry, setRetry] = useState(0)
  const [selected, setSelected] = useState<Receipt | null>(null)
  const detailRef = useRef<HTMLElement>(null)
  const triggerRef = useRef<HTMLButtonElement | null>(null)
  const selectedId = selected?.id

  useEffect(() => {
    if (active && selectedId) detailRef.current?.focus()
  }, [active, selectedId])

  const parameters = new URLSearchParams({ sortBy, direction, page: String(page), pageSize: '25' })
  if (applied.year === 'undated') parameters.set('undated', 'true')
  else if (applied.year) parameters.set('year', applied.year)
  if (applied.category === 'uncategorized') parameters.set('uncategorized', 'true')
  else if (applied.category) parameters.set('category', applied.category.slice('category:'.length))
  for (const key of ['dateFrom', 'dateTo', 'vendor', 'currency', 'status'] as const) {
    if (applied[key]) parameters.set(key, applied[key])
  }
  const query = parameters.toString()

  useEffect(() => {
    if (!active) return
    const controller = new AbortController()
    async function load() {
      setPending(true)
      setError('')
      try {
        const response = await fetch(`/api/documents/archive?${query}`, { signal: controller.signal })
        if (!response.ok) {
          const problem = await response.json().catch(() => null) as { detail?: string, title?: string } | null
          throw new Error(problem?.detail ?? problem?.title ?? `Could not load archive (${response.status}).`)
        }
        const body = await response.json() as ArchivePage
        if (!controller.signal.aborted) {
          setResult(body)
          setSelected((current) => current ? body.items.find((receipt) => receipt.id === current.id) ?? current : null)
        }
      } catch (requestError) {
        if (!controller.signal.aborted) setError(requestError instanceof Error ? requestError.message : 'Could not load archive.')
      } finally {
        if (!controller.signal.aborted) setPending(false)
      }
    }
    void load()
    return () => controller.abort()
  }, [active, query, refreshVersion, retry])

  function applyFilters(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setApplied({ ...filters })
    setPage(1)
  }

  function clearFilters() {
    setFilters(emptyFilters)
    setApplied(emptyFilters)
    setPage(1)
  }

  function removeFilter(key: keyof Filters) {
    setFilters((current) => ({ ...current, [key]: '' }))
    setApplied((current) => ({ ...current, [key]: '' }))
    setPage(1)
  }

  function filterValue(key: keyof Filters, value: string) {
    if (key === 'category') return value === 'uncategorized' ? 'Uncategorized' : value.slice('category:'.length)
    if (key === 'year' && value === 'undated') return 'Undated'
    if (key === 'status') return statusLabel(value)
    return value
  }

  const groups = new Map<string, Receipt[]>()
  for (const receipt of result?.items ?? []) {
    const year = receipt.documentDate?.slice(0, 4) ?? 'Undated'
    groups.set(year, [...(groups.get(year) ?? []), receipt])
  }
  const activeFilters = (Object.entries(applied) as [keyof Filters, string][]).filter(([, value]) => value !== '')
  const totalPages = Math.max(1, Math.ceil((result?.totalCount ?? 0) / 25))

  return (
    <section className="archive-view" id="archive-panel" aria-labelledby="page-title" hidden={!active}>
      <div className="archive-heading">
        <h2>All receipts</h2>
        <span role="status">{pending ? 'Loading receipts...' : error ? 'Archive unavailable' : `${result?.totalCount ?? 0} ${result?.totalCount === 1 ? 'receipt' : 'receipts'}`}</span>
      </div>
      <form className="archive-filters" onSubmit={applyFilters}>
        <label><span id="archive-year-label">Year</span><select aria-labelledby="archive-year-label" value={filters.year} onChange={(event) => setFilters({ ...filters, year: event.target.value, dateFrom: '', dateTo: '' })}>
          <option value="">All years</option>
          {result?.years.map((year) => <option key={year} value={year}>{year}</option>)}
          <option value="undated">Undated</option>
        </select></label>
        <label><span id="archive-category-label">Category</span><select aria-labelledby="archive-category-label" value={filters.category} onChange={(event) => setFilters({ ...filters, category: event.target.value })}>
          <option value="">All categories</option><option value="uncategorized">Uncategorized</option>
          {result?.categories.map((category) => <option key={category} value={`category:${category}`}>{category}</option>)}
        </select></label>
        <label><span id="archive-vendor-label">Vendor</span><select aria-labelledby="archive-vendor-label" value={filters.vendor} onChange={(event) => setFilters({ ...filters, vendor: event.target.value })}>
          <option value="">All vendors</option>
          {result?.vendors.map((vendor) => <option key={vendor}>{vendor}</option>)}
        </select></label>
        <label><span id="archive-currency-label">Currency</span><select aria-labelledby="archive-currency-label" value={filters.currency} onChange={(event) => setFilters({ ...filters, currency: event.target.value })}>
          <option value="">All currencies</option>
          {result?.currencies.map((currency) => <option key={currency}>{currency}</option>)}
        </select></label>
        <label><span id="archive-status-label">Review status</span><select aria-labelledby="archive-status-label" value={filters.status} onChange={(event) => setFilters({ ...filters, status: event.target.value })}>
          <option value="">All statuses</option>
          {statuses.map((status) => <option key={status} value={status}>{statusLabel(status)}</option>)}
        </select></label>
        <label>From<input type="date" disabled={filters.year === 'undated'} value={filters.dateFrom} max={filters.dateTo || undefined} onChange={(event) => setFilters({ ...filters, dateFrom: event.target.value })} /></label>
        <label>To<input type="date" disabled={filters.year === 'undated'} value={filters.dateTo} min={filters.dateFrom || undefined} onChange={(event) => setFilters({ ...filters, dateTo: event.target.value })} /></label>
        <div className="archive-filter-actions">
          <button type="submit" className="button-primary"><Filter size={15} aria-hidden="true" />Apply</button>
          <button type="button" className="archive-icon" title="Clear all filters" aria-label="Clear all filters" onClick={clearFilters}><RotateCcw size={17} /></button>
        </div>
      </form>
      {activeFilters.length > 0 && <div className="archive-active-filters" aria-label="Active filters">
        {activeFilters.map(([key, value]) => <button type="button" key={key} onClick={() => removeFilter(key)} title={`Remove ${filterLabels[key]} filter`}>
          {filterLabels[key]}: {filterValue(key, value)}<X size={13} aria-hidden="true" />
        </button>)}
      </div>}
      <div className="archive-toolbar">
        <span>By document year</span>
        <div>
          <label><span id="archive-sort-label">Sort within year</span><select aria-labelledby="archive-sort-label" value={sortBy} onChange={(event) => { setSortBy(event.target.value); setPage(1) }}>
            <option value="date">Document date</option><option value="category">Category</option><option value="vendor">Vendor</option><option value="amount">Amount</option>
          </select></label>
          <button type="button" className="archive-icon" aria-label={direction === 'asc' ? 'Sort descending' : 'Sort ascending'} title={direction === 'asc' ? 'Ascending; switch to descending' : 'Descending; switch to ascending'} onClick={() => { setDirection(direction === 'asc' ? 'desc' : 'asc'); setPage(1) }}>
            {direction === 'asc' ? <ArrowUp size={18} /> : <ArrowDown size={18} />}
          </button>
        </div>
      </div>
      {error && <div className="message message-error" role="alert">{error} <button type="button" className="text-button" onClick={() => setRetry((value) => value + 1)}>Retry</button></div>}
      <div className="archive-layout">
        <div className="archive-results" aria-busy={pending}>
          {pending ? <p className="empty-message">Loading receipts...</p> : error ? null : result?.items.length === 0 ? (
            <div className="archive-empty"><FileText size={30} aria-hidden="true" /><h3>{activeFilters.length ? 'No matching receipts' : 'No receipts yet'}</h3>
              {activeFilters.length > 0 && <button className="text-button" type="button" onClick={clearFilters}>Clear filters</button>}
            </div>
          ) : Array.from(groups, ([year, receipts]) => (
            <section className="archive-year" key={year} aria-label={`${year} receipts`}>
              <h3>{year}</h3>
              <div className="archive-table-scroll"><table className="archive-table">
                <thead><tr><th scope="col">Receipt</th><th scope="col">Date</th><th scope="col">Category</th><th scope="col">Amount</th><th scope="col">Status</th></tr></thead>
                <tbody>{receipts.map((receipt) => <tr key={receipt.id} className={selected?.id === receipt.id ? 'is-selected' : ''}>
                  <td><button className="archive-receipt-link" type="button" onClick={(event) => { triggerRef.current = event.currentTarget; setSelected(receipt) }} aria-label={`Open receipt ${receipt.vendor || receipt.fileName}`}>
                    <FileText size={19} aria-hidden="true" /><span><strong>{receipt.vendor || 'Vendor not set'}</strong><small>{receipt.fileName}</small></span>
                  </button></td>
                  <td>{receipt.documentDate ?? 'Not set'}</td><td>{receipt.category || 'Uncategorized'}</td>
                  <td className="archive-amount">{amountLabel(receipt.totalAmount, receipt.currency)}</td>
                  <td><span className={`status-mark status-${receipt.status.toLowerCase()}`}>{statusLabel(receipt.status)}</span></td>
                </tr>)}</tbody>
              </table></div>
            </section>
          ))}
          <nav className="archive-pagination" aria-label="Archive pages">
            <span>Page {page} of {totalPages}</span>
            <button type="button" className="archive-icon" title="Previous page" aria-label="Previous page" disabled={page === 1 || pending} onClick={() => setPage(page - 1)}><ChevronLeft size={19} /></button>
            <button type="button" className="archive-icon" title="Next page" aria-label="Next page" disabled={page >= totalPages || pending || !!error} onClick={() => setPage(page + 1)}><ChevronRight size={19} /></button>
          </nav>
        </div>
        {selected && <aside className="archive-detail" aria-label="Archive receipt details" ref={detailRef} tabIndex={-1}>
          <div className="archive-detail-heading"><h3>{selected.vendor || selected.fileName}</h3>
            <button type="button" className="archive-icon" title="Close receipt details" aria-label="Close receipt details" onClick={() => { setSelected(null); triggerRef.current?.focus() }}><X size={18} /></button>
          </div>
          <p className="archive-file-name">{selected.fileName}</p>
          <dl className="archive-metadata">
            <div><dt>Date</dt><dd>{selected.documentDate ?? 'Not set'}</dd></div>
            <div><dt>Category</dt><dd>{selected.category || 'Uncategorized'}</dd></div>
            <div><dt>Amount</dt><dd>{amountLabel(selected.totalAmount, selected.currency)}</dd></div>
            <div><dt>Status</dt><dd>{statusLabel(selected.status)}</dd></div>
          </dl>
          {selected.description && <p className="archive-description">{selected.description}</p>}
          <a className="archive-original" href={`/api/documents/${selected.id}/file`} target="_blank" rel="noreferrer"><ExternalLink size={16} aria-hidden="true" />Open original</a>
          <div className="archive-preview">
            {selected.fileName.toLowerCase().endsWith('.pdf') ? <iframe key={selected.id} src={`/api/documents/${selected.id}/file`} title={`Original ${selected.fileName}`} />
              : <img src={`/api/documents/${selected.id}/file`} alt={`Original ${selected.fileName}`} />}
          </div>
          {selected.lineItems.length > 0 && <div className="archive-line-items"><h4>Line items</h4>
            {selected.lineItems.map((item) => <div key={item.id}><span>{item.description}<small>{item.quantity !== null ? `Quantity: ${item.quantity}` : ''}{item.category ? ` / ${item.category}` : ''}{item.unitPrice !== null ? ` / Unit: ${amountLabel(item.unitPrice, selected.currency)}` : ''}</small></span><strong>{amountLabel(item.lineTotal, selected.currency)}</strong></div>)}
          </div>}
        </aside>}
      </div>
    </section>
  )
}

export default ReceiptArchive