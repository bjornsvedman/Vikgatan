import { useEffect, useState, type ChangeEvent } from 'react'
import { Archive, ClipboardCheck, MessageSquare, Save, Search, X } from 'lucide-react'
import { Link, NavLink, useLocation, useMatch, useNavigate } from 'react-router-dom'
import ReceiptArchive from './ReceiptArchive'
import CalculationDetails, { type ReceiptCalculation } from './CalculationDetails'
import './ReviewDesk.css'

export type ReceiptStatus = 'Pending' | 'Processing' | 'NeedsReview' | 'Completed' | 'Failed'

type ReceiptLineItem = {
  id: string
  description: string
  quantity: number | null
  unitPrice: number | null
  lineTotal: number | null
  category: string | null
}

export type Receipt = {
  id: string
  fileName: string
  documentDate: string | null
  vendor: string | null
  currency: string | null
  totalAmount: number | null
  category: string | null
  description: string | null
  status: ReceiptStatus
  processingError: string | null
  createdAtUtc: string
  updatedAtUtc: string
  lineItems: ReceiptLineItem[]
}

type ReceiptCitation = {
  id: string
  fileName: string
  vendor: string | null
  documentDate: string | null
  currency: string | null
  totalAmount: number | null
  status: ReceiptStatus
}

type ReceiptQuestionResponse = {
  answer: string
  sources: ReceiptCitation[]
  totalSourceCount?: number | null
  calculation?: ReceiptCalculation | null
}

type DraftLineItem = {
  key: string
  description: string
  quantity: string
  unitPrice: string
  lineTotal: string
  category: string
}

type ReviewDraft = {
  vendor: string
  documentDate: string
  currency: string
  totalAmount: string
  category: string
  description: string
  lineItems: DraftLineItem[]
}

type ApiProblem = {
  title?: string
  detail?: string
}

function toDraft(receipt: Receipt): ReviewDraft {
  return {
    vendor: receipt.vendor ?? '',
    documentDate: receipt.documentDate ?? '',
    currency: receipt.currency ?? '',
    totalAmount: receipt.totalAmount?.toString() ?? '',
    category: receipt.category ?? '',
    description: receipt.description ?? '',
    lineItems: receipt.lineItems.map((item) => ({
      key: item.id,
      description: item.description,
      quantity: item.quantity?.toString() ?? '',
      unitPrice: item.unitPrice?.toString() ?? '',
      lineTotal: item.lineTotal?.toString() ?? '',
      category: item.category ?? '',
    })),
  }
}

async function responseError(response: Response): Promise<Error> {
  try {
    const problem = (await response.json()) as ApiProblem
    return new Error(problem.detail ?? problem.title ?? `Request failed (${response.status}).`)
  } catch {
    return new Error(`Request failed (${response.status}).`)
  }
}

function ReviewDesk() {
  const { pathname } = useLocation()
  const navigate = useNavigate()
  const editId = useMatch('/archive/:receiptId/edit')?.params.receiptId
  const view = editId ? 'edit' : pathname === '/' ? 'home' : pathname === '/archive' ? 'archive' : pathname === '/review' ? 'review' : 'not-found'
  const pageTitle = view === 'home' ? 'Ask your receipts' : view === 'archive' ? 'Receipt archive' : view === 'review' ? 'Review receipts' : view === 'edit' ? 'Edit reviewed receipt' : 'Page not found'
  const [archiveVersion, setArchiveVersion] = useState(0)
  const [lastSavedReceipt, setLastSavedReceipt] = useState<Receipt | null>(null)
  const [receipts, setReceipts] = useState<Receipt[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [reviewDraft, setReviewDraft] = useState<ReviewDraft | null>(null)
  const [editDraft, setEditDraft] = useState<ReviewDraft | null>(null)
  const [editReceipt, setEditReceipt] = useState<Receipt | null>(null)
  const [editLoading, setEditLoading] = useState(false)
  const [requestToken, setRequestToken] = useState('')
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [uploading, setUploading] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [question, setQuestion] = useState('')
  const [questionResponse, setQuestionResponse] = useState<ReceiptQuestionResponse | null>(null)
  const [asking, setAsking] = useState(false)
  const draft = editId ? editDraft : reviewDraft
  const setDraft = editId ? setEditDraft : setReviewDraft

  useEffect(() => {
    document.title = `${pageTitle} | Renovation archive`
    window.scrollTo(0, 0)
  }, [pageTitle])

  useEffect(() => {
    let active = true

    async function initialize() {
      try {
        const tokenResponse = await fetch('/api/antiforgery/token')
        if (!tokenResponse.ok) throw await responseError(tokenResponse)
        const tokenBody = (await tokenResponse.json()) as { requestToken: string }
        if (!active) return
        setRequestToken(tokenBody.requestToken)

        const queueResponse = await fetch('/api/documents/review-queue')
        if (!queueResponse.ok) throw await responseError(queueResponse)
        const queue = (await queueResponse.json()) as Receipt[]
        if (!active) return
        setReceipts(queue)
        if (queue.length > 0) {
          setSelectedId(queue[0].id)
          setReviewDraft(toDraft(queue[0]))
        }
      } catch (requestError) {
        if (active) setError(requestError instanceof Error ? requestError.message : 'Could not load receipts.')
      } finally {
        if (active) setLoading(false)
      }
    }

    void initialize()
    return () => {
      active = false
    }
  }, [])

  useEffect(() => {
    if (!editId) return
    const controller = new AbortController()
    async function loadReceipt() {
      setEditLoading(true)
      setEditReceipt(null)
      setEditDraft(null)
      setError('')
      setNotice('')
      try {
        const response = await fetch(`/api/documents/${encodeURIComponent(editId!)}`, { signal: controller.signal })
        if (!response.ok) throw await responseError(response)
        const receipt = await response.json() as Receipt
        if (receipt.status !== 'Completed') throw new Error('This receipt is not approved. Use the review queue to review it.')
        if (!controller.signal.aborted) {
          setEditReceipt(receipt)
          setEditDraft(toDraft(receipt))
        }
      } catch (requestError) {
        if (!controller.signal.aborted) setError(requestError instanceof Error ? requestError.message : 'Could not load receipt.')
      } finally {
        if (!controller.signal.aborted) setEditLoading(false)
      }
    }
    void loadReceipt()
    return () => controller.abort()
  }, [editId])

  const selectedReceipt = editId
    ? editReceipt?.id === editId ? editReceipt : null
    : receipts.find((receipt) => receipt.id === selectedId) ?? null

  async function refreshQueue(focusId?: string) {
    const response = await fetch('/api/documents/review-queue')
    if (!response.ok) throw await responseError(response)
    const queue = (await response.json()) as Receipt[]
    setReceipts(queue)
    setArchiveVersion((version) => version + 1)

    const next = queue.find((receipt) => receipt.id === focusId)
      ?? queue.find((receipt) => receipt.id === selectedId)
      ?? queue[0]
    if (next) {
      setSelectedId(next.id)
      setReviewDraft(toDraft(next))
    } else {
      setSelectedId(null)
      setReviewDraft(null)
    }
  }

  function selectReceipt(receipt: Receipt) {
    setSelectedId(receipt.id)
    setReviewDraft(toDraft(receipt))
    setError('')
    setNotice('')
  }

  async function uploadReceipt(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0]
    event.target.value = ''
    if (!file) return

    setUploading(true)
    setError('')
    setNotice('')
    try {
      const form = new FormData()
      form.append('file', file)
      const response = await fetch('/api/documents/ingest', {
        method: 'POST',
        headers: { 'X-CSRF-TOKEN': requestToken },
        body: form,
      })

      if (response.status === 409) {
        const existing = (await response.json()) as Receipt
        setNotice(`${existing.fileName} is already in your archive.`)
        await refreshQueue(existing.id)
      } else {
        if (!response.ok) throw await responseError(response)
        const created = (await response.json()) as Receipt
        setNotice('Receipt added to the review queue.')
        await refreshQueue(created.id)
      }
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : 'Could not upload this file.')
    } finally {
      setUploading(false)
    }
  }

  function updateDraft<K extends keyof ReviewDraft>(key: K, value: ReviewDraft[K]) {
    setDraft((current) => current ? { ...current, [key]: value } : current)
  }

  function updateLineItem(index: number, patch: Partial<DraftLineItem>) {
    setDraft((current) => current ? {
      ...current,
      lineItems: current.lineItems.map((item, itemIndex) =>
        itemIndex === index ? { ...item, ...patch } : item),
    } : current)
  }

  function addLineItem() {
    setDraft((current) => current ? {
      ...current,
      lineItems: [...current.lineItems, {
        key: crypto.randomUUID(),
        description: '',
        quantity: '',
        unitPrice: '',
        lineTotal: '',
        category: '',
      }],
    } : current)
  }

  function removeLineItem(index: number) {
    setDraft((current) => current ? {
      ...current,
      lineItems: current.lineItems.filter((_, itemIndex) => itemIndex !== index),
    } : current)
  }

  async function saveReview(markReviewed: boolean) {
    if (!selectedReceipt || !draft || !requestToken) return

    setSaving(true)
    setError('')
    setNotice('')
    try {
      const response = await fetch(`/api/documents/${selectedReceipt.id}/review`, {
        method: 'PUT',
        headers: {
          'Content-Type': 'application/json',
          'X-CSRF-TOKEN': requestToken,
        },
        body: JSON.stringify({
          vendor: draft.vendor,
          documentDate: draft.documentDate || null,
          currency: draft.currency,
          totalAmount: draft.totalAmount === '' ? null : Number(draft.totalAmount),
          category: draft.category,
          description: draft.description,
          markReviewed: !!editId || markReviewed,
          lineItems: draft.lineItems.map((item) => ({
            description: item.description,
            quantity: item.quantity === '' ? null : Number(item.quantity),
            unitPrice: item.unitPrice === '' ? null : Number(item.unitPrice),
            lineTotal: item.lineTotal === '' ? null : Number(item.lineTotal),
            category: item.category,
          })),
        }),
      })

      if (!response.ok) throw await responseError(response)
      const updated = (await response.json()) as Receipt
      setLastSavedReceipt(updated)
      setQuestionResponse(null)
      if (editId) {
        setNotice('Receipt updated.')
        setArchiveVersion((version) => version + 1)
        navigate('/archive')
      } else {
        setNotice(markReviewed ? 'Review completed.' : 'Review draft saved.')
        await refreshQueue(markReviewed ? undefined : updated.id)
      }
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : 'Could not save this review.')
    } finally {
      setSaving(false)
    }
  }

  async function askReceipts(event: ChangeEvent<HTMLFormElement>) {
    event.preventDefault()
    const trimmedQuestion = question.trim()
    if (!trimmedQuestion) return

    setAsking(true)
    setError('')
    setQuestionResponse(null)
    try {
      const response = await fetch('/api/documents/query', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-CSRF-TOKEN': requestToken,
        },
        body: JSON.stringify({ question: trimmedQuestion }),
      })

      if (!response.ok) throw await responseError(response)
      setQuestionResponse((await response.json()) as ReceiptQuestionResponse)
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : 'Could not query receipts.')
    } finally {
      setAsking(false)
    }
  }

  const pendingCount = receipts.filter((receipt) => receipt.status === 'Pending').length

  return (
    <div className="app-shell">
      <header className="topbar">
        <Link className="wordmark" to="/" aria-label="Receipt desk home">
          <span className="wordmark-mark">R</span>
          <span>Renovation archive</span>
        </Link>
        <div className="topbar-meta">
          <span className="local-indicator"><span />Local workspace</span>
          <label className={`upload-button${uploading ? ' is-busy' : ''}`}>
            <span>{uploading ? 'Uploading…' : 'Upload receipt'}</span>
            <input
              type="file"
              accept=".pdf,.jpg,.jpeg,.png,.webp"
              onChange={(event) => void uploadReceipt(event)}
              disabled={uploading || !requestToken}
            />
          </label>
        </div>
      </header>

      <nav className="page-nav" aria-label="Main navigation">
        <NavLink to="/" end><MessageSquare size={16} aria-hidden="true" />Ask receipts</NavLink>
        <NavLink to="/archive"><Archive size={16} aria-hidden="true" />Archive</NavLink>
        <NavLink to="/review"><ClipboardCheck size={16} aria-hidden="true" />Review<span className="nav-count">{receipts.length}</span></NavLink>
      </nav>

      <main className="page-content">
        <section className="page-heading">
          <div>
            <p className="eyebrow">{view === 'home' ? 'RENOVATION ARCHIVE' : 'DOCUMENTS'}</p>
            <h1 id="page-title">{pageTitle}</h1>
          </div>
          {view === 'review' && <div className="queue-summary" aria-label={`${receipts.length} receipts awaiting review`}>
            <strong>{receipts.length.toString().padStart(2, '0')}</strong>
            <span>in queue</span>
            <i />
            <strong>{pendingCount.toString().padStart(2, '0')}</strong>
            <span>new</span>
          </div>}
        </section>

        {error && <div className="message message-error" role="alert">{error}</div>}
        {notice && <div className="message message-success" role="status">{notice}</div>}

        <section className="question-panel question-home" aria-label="Ask questions about receipts" hidden={view !== 'home'}>
          <form className="question-form" onSubmit={(event) => void askReceipts(event)}>
            <label className="question-field">
              <span className="sr-only">Question</span>
              <textarea
                maxLength={2000}
                onChange={(event) => setQuestion(event.target.value)}
                placeholder="How much did I spend on flooring in 2024?"
                rows={3}
                value={question}
              />
            </label>
            <button className="button-primary question-button" disabled={asking || !requestToken || !question.trim()} type="submit">
              <Search size={17} aria-hidden="true" />
              {asking ? 'Searching…' : 'Ask'}
            </button>
          </form>
          {questionResponse && (
            <div className="question-answer" role="status">
              <p>{questionResponse.answer}</p>
              {questionResponse.calculation && <CalculationDetails calculation={questionResponse.calculation} />}
              {questionResponse.sources.length > 0 && (
                <div className="citation-list" aria-label="Sources">
                  {questionResponse.sources.map((source) => (
                    <a
                      className="citation-item"
                      href={`/api/documents/${source.id}/file`}
                      key={source.id}
                      rel="noreferrer"
                      target="_blank"
                    >
                      <span className="citation-kind">SOURCE</span>
                      <span className="citation-name">{source.vendor || source.fileName}</span>
                      <span className="citation-detail">
                        {source.documentDate || 'Date not set'} · {formatAmount(source.totalAmount, source.currency)}
                      </span>
                      <span className="citation-open">View ↗</span>
                    </a>
                  ))}
                </div>
              )}
            </div>
          )}
        </section>

        <ReceiptArchive active={view === 'archive'} refreshVersion={archiveVersion} updatedReceipt={lastSavedReceipt} />

        {view === 'not-found' && <Link className="text-button" to="/">Back to questions</Link>}

        <div id="review-panel" aria-labelledby="page-title" hidden={view !== 'review' && view !== 'edit'}>
        <section className={`workbench${editId ? ' is-editing' : ''}`} aria-label={editId ? 'Edit approved receipt' : 'Receipt review workspace'}>
          <aside className="queue-pane" hidden={!!editId}>
            <div className="pane-heading">
              <div>
                <h2>Review queue</h2>
                <p>{receipts.length === 1 ? '1 document' : `${receipts.length} documents`}</p>
              </div>
              <span className="queue-count">{receipts.length}</span>
            </div>

            {loading ? (
              <p className="empty-message">Loading receipts…</p>
            ) : receipts.length === 0 ? (
              <p className="empty-message">No receipts to review.</p>
            ) : (
              <div className="queue-list">
                {receipts.map((receipt) => (
                  <button
                    className={`queue-item${receipt.id === selectedId ? ' is-selected' : ''}`}
                    key={receipt.id}
                    onClick={() => selectReceipt(receipt)}
                    type="button"
                  >
                    <span className="queue-item-top">
                      <span className="queue-vendor">{receipt.vendor || receipt.fileName}</span>
                      <span className={`status-mark status-${receipt.status.toLowerCase()}`}>
                        {receipt.status === 'NeedsReview' ? 'Review' : receipt.status}
                      </span>
                    </span>
                    <span className="queue-file">{receipt.vendor ? receipt.fileName : 'Vendor not set'}</span>
                    <span className="queue-item-bottom">
                      <span>{receipt.documentDate || 'Date not set'}</span>
                      <span>{formatAmount(receipt.totalAmount, receipt.currency)}</span>
                    </span>
                  </button>
                ))}
              </div>
            )}
          </aside>

          <section className="detail-pane" aria-label="Selected receipt details">
            {!draft || !selectedReceipt ? (
              <div className="detail-empty">
                <span className="empty-mark">—</span>
                <h2>{editId ? editLoading ? 'Loading receipt...' : 'Receipt unavailable' : 'Select a receipt'}</h2>
                {editId ? <Link to="/archive">Back to archive</Link> : <p>Upload a file or choose an item from the queue.</p>}
              </div>
            ) : (
              <form aria-busy={saving} onSubmit={(event) => { event.preventDefault(); void saveReview(false) }}>
                <div className="detail-heading">
                  <div className="document-title">
                    <span className="file-type">{selectedReceipt.fileName.split('.').pop()?.toUpperCase() ?? 'FILE'}</span>
                    <div>
                      <h2>{selectedReceipt.fileName}</h2>
                      <p>Added {formatDateTime(selectedReceipt.createdAtUtc)}</p>
                    </div>
                  </div>
                  <span className={`status-mark status-${selectedReceipt.status.toLowerCase()}`}>
                    {selectedReceipt.status === 'NeedsReview' ? 'Review' : selectedReceipt.status}
                  </span>
                </div>

                <div className="document-preview">
                  {selectedReceipt.fileName.toLowerCase().endsWith('.pdf') ? (
                    <iframe
                      src={`/api/documents/${selectedReceipt.id}/file`}
                      title={`Preview of ${selectedReceipt.fileName}`}
                    />
                  ) : (
                    <img
                      src={`/api/documents/${selectedReceipt.id}/file`}
                      alt={`Preview of ${selectedReceipt.fileName}`}
                    />
                  )}
                </div>

                {selectedReceipt.processingError && (
                  <div className="message message-error document-processing-error" role="status">
                    {selectedReceipt.processingError}
                  </div>
                )}

                <div className="form-section">
                  <div className="section-label">
                    <span>01</span>
                    <h3>Document details</h3>
                  </div>
                  <div className="field-grid">
                    <label className="field field-wide">
                      <span>Vendor</span>
                      <input value={draft.vendor} onChange={(event) => updateDraft('vendor', event.target.value)} maxLength={256} />
                    </label>
                    <label className="field">
                      <span>Document date</span>
                      <input type="date" value={draft.documentDate} onChange={(event) => updateDraft('documentDate', event.target.value)} />
                    </label>
                    <label className="field">
                      <span>Total amount</span>
                      <div className="money-input">
                        <input type="number" min="0" step="0.0001" value={draft.totalAmount} onChange={(event) => updateDraft('totalAmount', event.target.value)} />
                        <input className="currency-input" aria-label="Currency" maxLength={3} value={draft.currency} onChange={(event) => updateDraft('currency', event.target.value.toUpperCase())} placeholder="SEK" />
                      </div>
                    </label>
                    <label className="field">
                      <span>Category</span>
                      <input value={draft.category} onChange={(event) => updateDraft('category', event.target.value)} maxLength={128} />
                    </label>
                    <label className="field field-wide">
                      <span>Description</span>
                      <textarea rows={3} value={draft.description} onChange={(event) => updateDraft('description', event.target.value)} maxLength={4000} />
                    </label>
                  </div>
                </div>

                <div className="form-section line-items-section">
                  <div className="section-label section-label-action">
                    <div><span>02</span><h3>Line items</h3></div>
                    <button className="text-button" type="button" onClick={addLineItem}>Add item +</button>
                  </div>
                  {draft.lineItems.length === 0 ? (
                    <p className="line-items-empty">No itemized details.</p>
                  ) : (
                    <div className="line-items-list">
                      {draft.lineItems.map((item, index) => (
                        <div className="line-item-row" key={item.key}>
                          <label className="field line-description">
                            <span>Description</span>
                            <input value={item.description} onChange={(event) => updateLineItem(index, { description: event.target.value })} maxLength={1000} />
                          </label>
                          <label className="field line-quantity">
                            <span>Qty</span>
                            <input type="number" min="0" step="0.0001" value={item.quantity} onChange={(event) => updateLineItem(index, { quantity: event.target.value })} />
                          </label>
                          <label className="field line-unit-price">
                            <span>Unit price</span>
                            <input type="number" min="0" step="0.0001" value={item.unitPrice} onChange={(event) => updateLineItem(index, { unitPrice: event.target.value })} />
                          </label>
                          <label className="field line-amount">
                            <span>Line total</span>
                            <input type="number" min="0" step="0.0001" value={item.lineTotal} onChange={(event) => updateLineItem(index, { lineTotal: event.target.value })} />
                          </label>
                          <label className="field line-category">
                            <span>Category</span>
                            <input value={item.category} onChange={(event) => updateLineItem(index, { category: event.target.value })} maxLength={128} />
                          </label>
                          <button className="remove-button" type="button" aria-label={`Remove ${item.description || `line item ${index + 1}`}`} onClick={() => removeLineItem(index)}>Remove</button>
                        </div>
                      ))}
                    </div>
                  )}
                </div>

                <div className="form-actions">
                  <span className="save-note">Changes are saved to your local archive.</span>
                  <div>
                    {editId ? <>
                      <button className="button-secondary edit-action" type="button" disabled={saving} onClick={() => navigate('/archive')}><X size={16} aria-hidden="true" />Cancel</button>
                      <button className="button-primary edit-action" type="submit" disabled={saving || !requestToken}><Save size={16} aria-hidden="true" />{saving ? 'Saving...' : 'Save changes'}</button>
                    </> : <>
                      <button className="button-secondary" type="submit" disabled={saving || !requestToken}>{saving ? 'Saving…' : 'Save draft'}</button>
                      <button className="button-primary" type="button" disabled={saving || !requestToken} onClick={() => void saveReview(true)}>{saving ? 'Saving…' : 'Mark reviewed'}</button>
                    </>}
                  </div>
                </div>
              </form>
            )}
          </section>
        </section>
        </div>
      </main>
    </div>
  )
}

function formatAmount(amount: number | null, currency: string | null) {
  if (amount === null) return '—'
  return `${currency ?? ''} ${new Intl.NumberFormat('en-SE', { maximumFractionDigits: 2 }).format(amount)}`.trim()
}

function formatDateTime(value: string) {
  return new Intl.DateTimeFormat('en-SE', { dateStyle: 'medium' }).format(new Date(value))
}

export default ReviewDesk