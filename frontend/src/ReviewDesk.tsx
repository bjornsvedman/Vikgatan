import { useCallback, useEffect, useRef, useState, type ChangeEvent } from 'react'
import { Archive, ClipboardCheck, FolderOpen, MessageSquare, Save, Search, X } from 'lucide-react'
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
  propertyId: string
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

type PropertyWorkspace = {
  id: string
  name: string
  isDefault: boolean
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

type FolderImportOutcome = {
  fileName: string
  status: 'imported' | 'duplicate' | 'skipped' | 'failed' | 'cancelled'
  message: string
}

type FolderImportReport = {
  totalFiles: number
  outcomes: FolderImportOutcome[]
  currentFile: string | null
  running: boolean
  cancelled: boolean
}

const supportedReceiptExtensions = new Set(['.pdf', '.jpg', '.jpeg', '.png', '.webp'])

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
  const [properties, setProperties] = useState<PropertyWorkspace[]>([])
  const [selectedPropertyId, setSelectedPropertyId] = useState('')
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [reviewDraft, setReviewDraft] = useState<ReviewDraft | null>(null)
  const [editDraft, setEditDraft] = useState<ReviewDraft | null>(null)
  const [editReceipt, setEditReceipt] = useState<Receipt | null>(null)
  const [editLoading, setEditLoading] = useState(false)
  const [requestToken, setRequestToken] = useState('')
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [uploading, setUploading] = useState(false)
  const [folderImport, setFolderImport] = useState<FolderImportReport | null>(null)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [question, setQuestion] = useState('')
  const [questionResponse, setQuestionResponse] = useState<ReceiptQuestionResponse | null>(null)
  const [asking, setAsking] = useState(false)
  const folderImportController = useRef<AbortController | null>(null)
  const draft = editId ? editDraft : reviewDraft
  const setDraft = editId ? setEditDraft : setReviewDraft

  const configureFolderInput = useCallback((element: HTMLInputElement | null) => {
    element?.setAttribute('webkitdirectory', '')
  }, [])

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

        const propertiesResponse = await fetch('/api/properties/')
        if (!propertiesResponse.ok) throw await responseError(propertiesResponse)
        const availableProperties = await propertiesResponse.json() as PropertyWorkspace[]
        if (!active) return
        setProperties(availableProperties)
        const defaultProperty = availableProperties.find((property) => property.isDefault) ?? availableProperties[0]
        if (!defaultProperty) throw new Error('Create a property workspace before adding receipts.')
        setSelectedPropertyId(defaultProperty.id)

        const queueResponse = await fetch(`/api/documents/review-queue?propertyId=${encodeURIComponent(defaultProperty.id)}`)
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

  async function refreshQueue(focusId?: string, propertyId = selectedPropertyId) {
    const response = await fetch(`/api/documents/review-queue?propertyId=${encodeURIComponent(propertyId)}`)
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

  async function selectProperty(propertyId: string) {
    if (!propertyId || propertyId === selectedPropertyId) return
    setError('')
    setNotice('')
    try {
      const response = await fetch(`/api/properties/${encodeURIComponent(propertyId)}/default`, { method: 'PUT' })
      if (!response.ok) throw await responseError(response)
      setSelectedPropertyId(propertyId)
      setProperties((current) => current.map((property) => ({ ...property, isDefault: property.id === propertyId })))
      setReceipts([])
      setSelectedId(null)
      setReviewDraft(null)
      setQuestionResponse(null)
      setFolderImport(null)
      await refreshQueue(undefined, propertyId)
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : 'Could not change the selected property.')
    }
  }

  async function createProperty() {
    const name = window.prompt('Name this property workspace')
    if (!name?.trim()) return
    setError('')
    try {
      const response = await fetch('/api/properties/', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: name.trim() }),
      })
      if (!response.ok) throw await responseError(response)
      const property = await response.json() as PropertyWorkspace
      setProperties((current) => [...current, property])
      await selectProperty(property.id)
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : 'Could not create the property.')
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
      const response = await fetch(`/api/documents/ingest?propertyId=${encodeURIComponent(selectedPropertyId)}`, {
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

  async function importFolder(event: ChangeEvent<HTMLInputElement>) {
    const files = Array.from(event.target.files ?? [])
    event.target.value = ''
    if (files.length === 0 || uploading) return

    const supportedFiles: File[] = []
    const outcomes: FolderImportOutcome[] = []
    for (const file of files) {
      const fileName = file.webkitRelativePath || file.name
      const extensionIndex = file.name.lastIndexOf('.')
      const extension = extensionIndex < 0 ? '' : file.name.slice(extensionIndex).toLowerCase()
      if (supportedReceiptExtensions.has(extension)) {
        supportedFiles.push(file)
      } else {
        outcomes.push({ fileName, status: 'skipped', message: 'Unsupported file type.' })
      }
    }

    const controller = new AbortController()
    folderImportController.current = controller
    setUploading(true)
    setError('')
    setNotice('')
    setFolderImport({ totalFiles: files.length, outcomes: [...outcomes], currentFile: null, running: true, cancelled: false })
    let lastImportedId: string | undefined

    try {
      for (let index = 0; index < supportedFiles.length; index += 1) {
        const file = supportedFiles[index]
        const fileName = file.webkitRelativePath || file.name
        if (controller.signal.aborted) {
          for (const unprocessed of supportedFiles.slice(index)) {
            outcomes.push({
              fileName: unprocessed.webkitRelativePath || unprocessed.name,
              status: 'cancelled',
              message: 'Not uploaded because the folder import was cancelled.',
            })
          }
          break
        }

        setFolderImport({ totalFiles: files.length, outcomes: [...outcomes], currentFile: fileName, running: true, cancelled: false })
        let outcome: FolderImportOutcome
        try {
          const form = new FormData()
          form.append('file', file)
          const response = await fetch(`/api/documents/ingest?propertyId=${encodeURIComponent(selectedPropertyId)}`, {
            method: 'POST',
            headers: { 'X-CSRF-TOKEN': requestToken },
            body: form,
            signal: controller.signal,
          })

          if (response.status === 409) {
            outcome = { fileName, status: 'duplicate', message: 'An identical file is already in the archive.' }
          } else if (response.ok) {
            const created = await response.json() as Receipt
            lastImportedId = created.id
            outcome = { fileName, status: 'imported', message: 'Added to the review queue.' }
          } else {
            throw await responseError(response)
          }
        } catch (requestError) {
          if (controller.signal.aborted) {
            outcome = {
              fileName,
              status: 'cancelled',
              message: 'Request cancelled; check the archive because the server may have completed it.',
            }
            outcomes.push(outcome)
            for (const unprocessed of supportedFiles.slice(index + 1)) {
              outcomes.push({
                fileName: unprocessed.webkitRelativePath || unprocessed.name,
                status: 'cancelled',
                message: 'Not uploaded because the folder import was cancelled.',
              })
            }
            break
          }

          outcome = {
            fileName,
            status: 'failed',
            message: requestError instanceof Error ? requestError.message : 'Could not upload this file.',
          }
        }

        outcomes.push(outcome)
        setFolderImport({
          totalFiles: files.length,
          outcomes: [...outcomes],
          currentFile: null,
          running: true,
          cancelled: controller.signal.aborted,
        })
      }

      try {
        await refreshQueue(lastImportedId)
      } catch (refreshError) {
        setError(refreshError instanceof Error
          ? `Folder import finished, but the review queue could not be refreshed: ${refreshError.message}`
          : 'Folder import finished, but the review queue could not be refreshed.')
      }
      setFolderImport({
        totalFiles: files.length,
        outcomes: [...outcomes],
        currentFile: null,
        running: false,
        cancelled: controller.signal.aborted,
      })
    } finally {
      folderImportController.current = null
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
      const response = await fetch(`/api/documents/query?propertyId=${encodeURIComponent(selectedPropertyId)}`, {
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
          <label className="property-selector">
            <span className="sr-only">Default property</span>
            <select
              aria-label="Default property"
              onChange={(event) => void selectProperty(event.target.value)}
              value={selectedPropertyId}
              disabled={properties.length === 0 || uploading}
            >
              {properties.map((property) => <option key={property.id} value={property.id}>{property.name}</option>)}
            </select>
            <button
              type="button"
              className="text-button"
              onClick={() => void createProperty()}
              disabled={loading || uploading}
            >
              Add property
            </button>
          </label>
          <span className="local-indicator"><span />Local workspace</span>
          <label className={`upload-button${uploading ? ' is-busy' : ''}`}>
            <span>{uploading ? 'Uploading…' : 'Upload receipt'}</span>
            <input
              type="file"
              accept=".pdf,.jpg,.jpeg,.png,.webp"
              onChange={(event) => void uploadReceipt(event)}
              disabled={uploading || !requestToken || !selectedPropertyId}
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
          {view === 'review' && <div className="review-actions">
            <label className={`folder-import-button${uploading ? ' is-busy' : ''}`}>
              <FolderOpen size={15} aria-hidden="true" />
              <span>Import folder</span>
              <input
                ref={configureFolderInput}
                type="file"
                multiple
                onChange={(event) => void importFolder(event)}
                disabled={uploading || !requestToken || !selectedPropertyId}
                aria-label="Select a folder to import receipts from"
              />
            </label>
            <div className="queue-summary" aria-label={`${receipts.length} receipts awaiting review`}>
              <strong>{receipts.length.toString().padStart(2, '0')}</strong>
              <span>in queue</span>
              <i />
              <strong>{pendingCount.toString().padStart(2, '0')}</strong>
              <span>new</span>
            </div>
          </div>}
        </section>

        {error && <div className="message message-error" role="alert">{error}</div>}
        {notice && <div className="message message-success" role="status">{notice}</div>}
        {view === 'review' && folderImport && <section className="folder-import-report" aria-label="Folder import results">
          <div className="folder-import-heading">
            <div>
              <h2>{folderImport.cancelled ? 'Folder import cancelled' : folderImport.running ? 'Importing folder' : 'Folder import complete'}</h2>
              <p role="status">{folderImport.outcomes.length} of {folderImport.totalFiles} files processed
                {folderImport.currentFile ? ` · ${folderImport.currentFile}` : ''}
              </p>
            </div>
            {folderImport.running && <button className="text-button" type="button" onClick={() => folderImportController.current?.abort()}>Cancel import</button>}
          </div>
          <progress max={folderImport.totalFiles} value={folderImport.outcomes.length} aria-label="Folder import progress" />
          <p className="folder-import-counts">
            {folderImport.outcomes.filter((outcome) => outcome.status === 'imported').length} imported ·{' '}
            {folderImport.outcomes.filter((outcome) => outcome.status === 'duplicate').length} already present ·{' '}
            {folderImport.outcomes.filter((outcome) => outcome.status === 'skipped').length} skipped ·{' '}
            {folderImport.outcomes.filter((outcome) => outcome.status === 'failed').length} failed ·{' '}
            {folderImport.outcomes.filter((outcome) => outcome.status === 'cancelled').length} not uploaded
          </p>
          {folderImport.outcomes.length > 0 && <details>
            <summary>File results ({folderImport.outcomes.length})</summary>
            <ul>{folderImport.outcomes.map((outcome, index) => (
              <li className={`folder-import-${outcome.status}`} key={`${outcome.fileName}-${index}`}>
                <span>{outcome.fileName}</span><span>{outcome.message}</span>
              </li>
            ))}</ul>
          </details>}
        </section>}

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
            <button className="button-primary question-button" disabled={asking || !requestToken || !selectedPropertyId || !question.trim()} type="submit">
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

        <ReceiptArchive
          active={view === 'archive'}
          key={selectedPropertyId}
          propertyId={selectedPropertyId}
          refreshVersion={archiveVersion}
          updatedReceipt={lastSavedReceipt}
        />

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