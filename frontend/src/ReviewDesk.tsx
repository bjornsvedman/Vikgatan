import { useEffect, useState, type ChangeEvent } from 'react'
import './ReviewDesk.css'

type ReceiptStatus = 'Pending' | 'Processing' | 'NeedsReview' | 'Completed' | 'Failed'

type ReceiptLineItem = {
  id: string
  description: string
  quantity: number | null
  unitPrice: number | null
  lineTotal: number | null
  category: string | null
}

type Receipt = {
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
  const [receipts, setReceipts] = useState<Receipt[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [draft, setDraft] = useState<ReviewDraft | null>(null)
  const [requestToken, setRequestToken] = useState('')
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [uploading, setUploading] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')

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
          setDraft(toDraft(queue[0]))
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

  const selectedReceipt = receipts.find((receipt) => receipt.id === selectedId) ?? null

  async function refreshQueue(focusId?: string) {
    const response = await fetch('/api/documents/review-queue')
    if (!response.ok) throw await responseError(response)
    const queue = (await response.json()) as Receipt[]
    setReceipts(queue)

    const next = queue.find((receipt) => receipt.id === focusId)
      ?? queue.find((receipt) => receipt.id === selectedId)
      ?? queue[0]
    if (next) {
      setSelectedId(next.id)
      setDraft(toDraft(next))
    } else {
      setSelectedId(null)
      setDraft(null)
    }
  }

  function selectReceipt(receipt: Receipt) {
    setSelectedId(receipt.id)
    setDraft(toDraft(receipt))
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
    if (!selectedId || !draft) return

    setSaving(true)
    setError('')
    setNotice('')
    try {
      const response = await fetch(`/api/documents/${selectedId}/review`, {
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
          markReviewed,
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
      setNotice(markReviewed ? 'Review completed.' : 'Review draft saved.')
      await refreshQueue(markReviewed ? undefined : updated.id)
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : 'Could not save this review.')
    } finally {
      setSaving(false)
    }
  }

  const pendingCount = receipts.filter((receipt) => receipt.status === 'Pending').length

  return (
    <div className="app-shell">
      <header className="topbar">
        <a className="wordmark" href="/" aria-label="Receipt desk home">
          <span className="wordmark-mark">R</span>
          <span>Renovation archive</span>
        </a>
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

      <main className="page-content">
        <section className="page-heading">
          <div>
            <p className="eyebrow">DOCUMENT REVIEW</p>
            <h1>Receipt desk</h1>
          </div>
          <div className="queue-summary" aria-label={`${receipts.length} receipts awaiting review`}>
            <strong>{receipts.length.toString().padStart(2, '0')}</strong>
            <span>in queue</span>
            <i />
            <strong>{pendingCount.toString().padStart(2, '0')}</strong>
            <span>new</span>
          </div>
        </section>

        {error && <div className="message message-error" role="alert">{error}</div>}
        {notice && <div className="message message-success" role="status">{notice}</div>}

        <section className="workbench" aria-label="Receipt review workspace">
          <aside className="queue-pane">
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
                <h2>Select a receipt</h2>
                <p>Upload a file or choose an item from the queue.</p>
              </div>
            ) : (
              <form onSubmit={(event) => { event.preventDefault(); void saveReview(false) }}>
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
                        <input type="number" min="0" step="0.01" value={draft.totalAmount} onChange={(event) => updateDraft('totalAmount', event.target.value)} />
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
                            <input type="number" min="0" step="0.01" value={item.quantity} onChange={(event) => updateLineItem(index, { quantity: event.target.value })} />
                          </label>
                          <label className="field line-unit-price">
                            <span>Unit price</span>
                            <input type="number" min="0" step="0.01" value={item.unitPrice} onChange={(event) => updateLineItem(index, { unitPrice: event.target.value })} />
                          </label>
                          <label className="field line-amount">
                            <span>Line total</span>
                            <input type="number" min="0" step="0.01" value={item.lineTotal} onChange={(event) => updateLineItem(index, { lineTotal: event.target.value })} />
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
                    <button className="button-secondary" type="submit" disabled={saving}>{saving ? 'Saving…' : 'Save draft'}</button>
                    <button className="button-primary" type="button" disabled={saving} onClick={() => void saveReview(true)}>{saving ? 'Saving…' : 'Mark reviewed'}</button>
                  </div>
                </div>
              </form>
            )}
          </section>
        </section>
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