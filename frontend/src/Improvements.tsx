import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import type { Receipt } from './ReviewDesk'
import type { ExpenseFacts } from './expenseReviewModel'
import ExpenseReport from './ExpenseReport'
import './Improvements.css'

type PropertyType = '' | 'SmallHouse' | 'Bostadsratt' | 'OwnerOccupiedApartment'
type SaleFacts = { propertyType: PropertyType | null, acquisitionDate: string | null, saleDate: string | null }
type Expense = {
  id: string, receiptId: string, description: string, expenseDate: string | null,
  type: string, amount: string | null, currency: string | null, source: Receipt,
  reviewFacts: ExpenseFacts, warnings: { code: string, message: string }[],
}
type Readiness = {
  confirmedExpenses: number, pendingExpenses: number, excludedExpenses: number,
  missingFacts: string[], canGenerateReport: boolean,
}
async function readResponse<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const problem = await response.json() as { detail?: string, title?: string }
    throw new Error(problem.detail ?? problem.title ?? `Request failed (${response.status}).`)
  }
  return response.json() as Promise<T>
}

export default function Improvements({ propertyId, propertyName, onDirty, onSaving }: {
  propertyId: string, propertyName: string, onDirty: (dirty: boolean) => void, onSaving: (saving: boolean) => void,
}) {
  const [facts, setFacts] = useState<SaleFacts>({ propertyType: null, acquisitionDate: null, saleDate: null })
  const [expenses, setExpenses] = useState<Expense[]>([])
  const [readiness, setReadiness] = useState<Readiness | null>(null)
  const [receipts, setReceipts] = useState<Receipt[]>([])
  const [receiptPage, setReceiptPage] = useState(1)
  const [receiptCount, setReceiptCount] = useState(0)
  const [dirty, setDirty] = useState(false)
  const [saving, setSaving] = useState(false)
  const [loaded, setLoaded] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const base = `/api/properties/${encodeURIComponent(propertyId)}`
  useEffect(() => { onDirty(dirty) }, [dirty, onDirty])
  useEffect(() => () => onDirty(false), [onDirty])
  useEffect(() => {
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault() }
    if (dirty || saving) window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty, saving])

  useEffect(() => {
    const controller = new AbortController()
    async function load() {
      try {
        const [saleFacts, entries, status] = await Promise.all([
          fetch(`${base}/sale-facts`, { signal: controller.signal }).then(readResponse<SaleFacts>),
          fetch(`${base}/expenses`, { signal: controller.signal }).then(readResponse<Expense[]>),
          fetch(`${base}/readiness`, { signal: controller.signal }).then(readResponse<Readiness>),
        ])
        if (!controller.signal.aborted) { setFacts(saleFacts); setExpenses(entries); setReadiness(status); setLoaded(true) }
      } catch (requestError) {
        if (!controller.signal.aborted) setError(requestError instanceof Error ? requestError.message : 'Could not load improvements.')
      }
    }
    void load()
    return () => controller.abort()
  }, [base])
  useEffect(() => {
    const controller = new AbortController()
    async function load() {
      try {
        const result = await readResponse<{ items: Receipt[], totalCount: number }>(await fetch(
          `/api/documents/archive?propertyId=${encodeURIComponent(propertyId)}&status=Completed&page=${receiptPage}&pageSize=25`,
          { signal: controller.signal },
        ))
        if (!controller.signal.aborted) { setReceipts(result.items); setReceiptCount(result.totalCount) }
      } catch (requestError) {
        if (!controller.signal.aborted) setError(requestError instanceof Error ? requestError.message : 'Could not load reviewed receipts.')
      }
    }
    void load()
    return () => controller.abort()
  }, [propertyId, receiptPage])

  async function saveFacts(event: FormEvent) {
    event.preventDefault()
    setSaving(true); onSaving(true); setError(''); setNotice('')
    try {
      const saved = await readResponse<SaleFacts>(await fetch(`${base}/sale-facts`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ...facts, propertyType: facts.propertyType || null }),
      }))
      setFacts(saved); setDirty(false); setNotice('Property sale facts saved.')
      setReadiness(await readResponse<Readiness>(await fetch(`${base}/readiness`)))
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : 'Could not save facts.')
    } finally { setSaving(false); onSaving(false) }
  }

  return <section className="improvements" aria-label="Improvement readiness">
    <p><strong>{propertyName}</strong>: Classify expenses in receipt review once. This page reuses those facts; confirmation is not a guarantee of deductibility.</p>
    {error && <p className="message message-error" role="alert">{error}</p>}
    {notice && <p className="message message-success" role="status">{notice}</p>}
    {!loaded && !error && <p role="status">Loading improvement readiness...</p>}
    <form onSubmit={event => void saveFacts(event)}>
      <fieldset disabled={!loaded || saving}>
        <legend>Property sale facts</legend>
        <label>Property type<select value={facts.propertyType ?? ''} onChange={event => {
          setFacts(current => ({ ...current, propertyType: event.target.value as PropertyType })); setDirty(true)
        }}>
          <option value="">Not set</option><option value="SmallHouse">Small house</option>
          <option value="Bostadsratt">Bostadsratt</option><option value="OwnerOccupiedApartment">Owner-occupied apartment</option>
        </select></label>
        <label>Acquisition date<input type="date" value={facts.acquisitionDate ?? ''} onChange={event => {
          setFacts(current => ({ ...current, acquisitionDate: event.target.value || null })); setDirty(true)
        }} /></label>
        <label>Sale date (when known)<input type="date" value={facts.saleDate ?? ''} onChange={event => {
          setFacts(current => ({ ...current, saleDate: event.target.value || null })); setDirty(true)
        }} /></label>
        <button className="button-primary" disabled={!dirty}>Save facts</button>
      </fieldset>
    </form>
    {loaded && <ExpenseReport propertyId={propertyId} blocked={dirty || saving} />}
    {readiness && <section aria-label="Future tax report readiness">
      <h2>Future tax worksheet — not part of the MVP summary</h2>
      <p>{readiness.confirmedExpenses} confirmed · {readiness.pendingExpenses} pending confirmation · {readiness.excludedExpenses} excluded expense portions</p>
      <ul>{readiness.missingFacts.map(fact => <li key={fact}>{fact}</li>)}</ul>
      <p>The preparation summary above is available without tax calculations. Official worksheet filling and deductible totals remain deferred.</p>
    </section>}
    <h2>Expense evidence ({expenses.length})</h2>
    {loaded && expenses.length === 0 && <p>No classified expenses yet. Open a receipt below or use <Link to="/review">Review</Link> for new receipts.</p>}
    {expenses.map(expense => <article className="expense-draft" key={expense.id}>
      <h3>{expense.description}</h3>
      <p>{expense.amount ?? 'Amount missing'} {expense.currency ?? 'Currency missing'} · {expense.expenseDate ?? 'Date missing'} · {expense.type === 'BasicImprovement' ? 'Basic improvement' : expense.type === 'RepairsAndMaintenance' ? 'Repairs and maintenance' : expense.type === 'Excluded' ? 'Excluded / not deductible' : 'Unclassified'} · {expense.reviewFacts.confirmedAtUtc ? 'Confirmation recorded — see warnings' : 'Needs confirmation'}</p>
      <p>Source: <a href={`/api/documents/${expense.receiptId}/file`} target="_blank" rel="noreferrer">{expense.source.vendor || expense.source.fileName}</a></p>
      {expense.warnings.length > 0 && <ul>{expense.warnings.map(warning => <li key={warning.code}>{warning.message}</li>)}</ul>}
      <Link to={expense.source.status === 'Completed' ? `/archive/${expense.receiptId}/edit` : '/review'}>Review / correct expense facts</Link>
    </article>)}
    <h2>Reviewed receipts (including legacy classification pending)</h2>
    {receipts.map(receipt => <p key={receipt.id}><Link to={`/archive/${receipt.id}/edit`}>{receipt.vendor || receipt.fileName} — {receipt.documentDate || 'Undated'}</Link></p>)}
    <div className="receipt-picker-pages">
      <button type="button" className="text-button" disabled={receiptPage === 1 || saving} onClick={() => setReceiptPage(page => page - 1)}>Previous receipts</button>
      <span>Page {receiptPage} / {Math.max(1, Math.ceil(receiptCount / 25))}</span>
      <button type="button" className="text-button" disabled={receiptPage * 25 >= receiptCount || saving} onClick={() => setReceiptPage(page => page + 1)}>Next receipts</button>
    </div>
  </section>
}
