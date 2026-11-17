import { useCallback, useEffect, useRef, useState, type ChangeEvent } from 'react'
import { Archive, ClipboardCheck, FolderOpen, MessageSquare, Save, Search, Settings, X } from 'lucide-react'
import { Link, NavLink, useLocation, useMatch, useNavigate } from 'react-router-dom'
import ReceiptArchive from './ReceiptArchive'
import Admin from './Admin'
import ExpenseReview from './ExpenseReview'
import { expenseDrafts, type ExpenseDraft, type ExpenseFacts, type ImprovementType } from './expenseReviewModel'
import CalculationDetails, { type ReceiptCalculation } from './CalculationDetails'
import { ProjectSelect } from './Projects'
import { useProjects } from './projectModel'
import './ReviewDesk.css'

export type ReceiptStatus = 'Pending' | 'Processing' | 'NeedsReview' | 'Completed' | 'Failed'

type ReceiptLineItem = {
  id: string
  description: string
  quantity: number | null
  unitPrice: number | null
  lineTotal: number | null
  projectId?: string | null
  projectName?: string | null
  lineTotalExact?: string | null
  quantityExact?: string | null
  unitPriceExact?: string | null
}

export type Receipt = {
  id: string
  propertyId: string
  fileName: string
  documentDate: string | null
  vendor: string | null
  currency: string | null
  totalAmount: number | null
  totalAmountExact?: string | null
  description: string | null
  status: ReceiptStatus
  processingError: string | null
  createdAtUtc: string
  updatedAtUtc: string
  lineItems: ReceiptLineItem[]
  reviewVersion: number
  expenses: ExpenseFacts[]
  expenseSuggestions?: { description: string | null, type: ImprovementType, amount: number | null, evidence: string, uncertainty: string }[]
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
  sourceProjects?: string[]
  matchedItems?: { id: string, description: string, project: string, amount: string | null }[]
}

type ReceiptQuestionResponse = {
  answer: string
  sources: ReceiptCitation[]
  totalSourceCount?: number | null
  calculation?: ReceiptCalculation | null
}

type DraftLineItem = {
  key: string
  id?: string
  description: string
  quantity: string
  unitPrice: string
  lineTotal: string
}

type ReviewDraft = {
  projectId: string
  vendor: string
  documentDate: string
  currency: string
  totalAmount: string
  description: string
  lineItems: DraftLineItem[]
  expenses: ExpenseDraft[]
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
const mixedProjectsValue = '__mixed_projects__'

function sharedProjectId(lineItems: ReceiptLineItem[]) {
  if (lineItems.length === 0) return ''
  const projectId = lineItems[0].projectId ?? ''
  return lineItems.every(item => (item.projectId ?? '') === projectId) ? projectId : mixedProjectsValue
}

function toDraft(receipt: Receipt): ReviewDraft {
  return {
    projectId: sharedProjectId(receipt.lineItems),
    vendor: receipt.vendor ?? '',
    documentDate: receipt.documentDate ?? '',
    currency: receipt.currency ?? 'SEK',
    totalAmount: receipt.totalAmountExact ?? receipt.totalAmount?.toString() ?? '',
    description: receipt.description ?? '',
    expenses: expenseDrafts(receipt),
    lineItems: receipt.lineItems.map((item) => ({
      key: item.id,
      id: item.id,
      description: item.description,
      quantity: item.quantityExact ?? item.quantity?.toString() ?? '',
      unitPrice: item.unitPriceExact ?? item.unitPrice?.toString() ?? '',
      lineTotal: item.lineTotalExact ?? item.lineTotal?.toString() ?? '',
    })),
  }
}

function wholeReceiptLineItem(draft: ReviewDraft): DraftLineItem {
  return {
    key: crypto.randomUUID(),
    description: draft.description || draft.vendor || 'Whole receipt',
    quantity: '1',
    unitPrice: draft.totalAmount,
    lineTotal: draft.totalAmount,
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
  const view = editId ? 'edit' : pathname === '/' ? 'home' : pathname === '/archive' ? 'archive' : pathname === '/review' ? 'review' : pathname === '/admin' ? 'admin' : 'not-found'
  const pageTitle = view === 'home' ? 'Ask your receipts' : view === 'archive' ? 'Receipt archive' : view === 'review' ? 'Review receipts' : view === 'edit' ? 'Edit reviewed receipt' : view === 'admin' ? 'Admin' : 'Page not found'
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
  const projectsState = useProjects(selectedPropertyId, requestToken)
  const questionController = useRef<AbortController | null>(null)
  useEffect(() => () => questionController.current?.abort(), [selectedPropertyId])
  const folderImportController = useRef<AbortController | null>(null)
  const draft = editId ? editDraft : reviewDraft
  const setDraft = editId ? setEditDraft : setReviewDraft
  const reviewDirty = draft !== null && selectedReceiptForDraft() !== null
    && JSON.stringify(draft) !== JSON.stringify(toDraft(selectedReceiptForDraft()!))

  function selectedReceiptForDraft() {
    return editId ? editReceipt : receipts.find(receipt => receipt.id === selectedId) ?? null
  }

  useEffect(() => {
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault() }
    if (reviewDirty || saving || projectsState.saving) window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [reviewDirty, saving, projectsState.saving])

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
    if (!editId || !selectedPropertyId) return
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
        if (receipt.propertyId !== selectedPropertyId) throw new Error('Select the receipt’s property before editing it.')
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
  }, [editId, selectedPropertyId])

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
    if (saving || projectsState.saving || (reviewDirty && !window.confirm('Discard unsaved review or property changes?'))) return
    setError('')
    setNotice('')
    try {
      const response = await fetch(`/api/properties/${encodeURIComponent(propertyId)}/default`, { method: 'PUT' })
      if (!response.ok) throw await responseError(response)
      setSelectedPropertyId(propertyId)
      setAsking(false)
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
    if (saving || (reviewDirty && !window.confirm('Discard unsaved receipt review?'))) return
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
    setDraft((current) => current ? { ...current, [key]: value,
      expenses: key === 'expenses' ? value as ExpenseDraft[] : current.expenses.map(expense => ({ ...expense, confirm: false })),
    } : current)
  }

  function updateReceiptProject(projectId: string) {
    setDraft(current => current ? {
      ...current,
      projectId,
      ...(projectId && current.lineItems.length === 0 ? {
        expenses: current.expenses.map(expense => ({ ...expense, confirm: false })),
        lineItems: [wholeReceiptLineItem(current)],
      } : {}),
    } : current)
  }

  function updateLineItem(index: number, patch: Partial<DraftLineItem>) {
    setDraft((current) => current ? {
      ...current,
      expenses: current.expenses.map(expense => ({ ...expense, confirm: false })),
      lineItems: current.lineItems.map((item, itemIndex) =>
        itemIndex === index ? { ...item, ...patch } : item),
    } : current)
  }

  function addLineItem() {
    setDraft((current) => current ? {
      ...current,
      expenses: current.expenses.map(expense => ({ ...expense, confirm: false })),
      lineItems: [...current.lineItems, {
        key: crypto.randomUUID(),
        description: '',
        quantity: '',
        unitPrice: '',
        lineTotal: '',
      }],
    } : current)
  }

  function removeLineItem(index: number) {
    setDraft((current) => current ? {
      ...current,
      expenses: current.expenses.map(expense => ({ ...expense, confirm: false })),
      lineItems: current.lineItems.filter((_, itemIndex) => itemIndex !== index),
    } : current)
  }

  async function saveReview(markReviewed: boolean) {
    if (!selectedReceipt || !draft || !requestToken) return
    if (draft.projectId === mixedProjectsValue) {
      setError('Choose one project for this receipt before saving.')
      return
    }

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
          totalAmount: draft.totalAmount === '' ? null : draft.totalAmount,
          description: draft.description,
          markReviewed,
          reviewVersion: selectedReceipt.reviewVersion ?? 0,
          expenses: draft.expenses.map(expense => ({
            ...expense, expenseDate: expense.expenseDate || null, amount: expense.amount || null,
            currency: expense.currency || null, rotAmount: expense.rotAmount || null,
            otherCompensationAmount: expense.otherCompensationAmount || null,
          })),
          lineItems: draft.lineItems.map((item) => ({
            id: item.id ?? null,
            projectId: draft.projectId || null,
            description: item.description,
            quantity: item.quantity || null,
            unitPrice: item.unitPrice || null,
            lineTotal: item.lineTotal || null,
          })),
        }),
      })

      if (!response.ok) throw await responseError(response)
      const updated = (await response.json()) as Receipt
      setLastSavedReceipt(updated)
      setQuestionResponse(null)
      if (editId) {
        setEditReceipt(updated)
        setEditDraft(toDraft(updated))
        setNotice('Receipt updated.')
        setArchiveVersion((version) => version + 1)
        if (markReviewed) navigate('/archive')
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
    const controller = new AbortController()
    questionController.current?.abort()
    questionController.current = controller
    try {
      const response = await fetch(`/api/documents/query?propertyId=${encodeURIComponent(selectedPropertyId)}`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-CSRF-TOKEN': requestToken,
        },
        body: JSON.stringify({ question: trimmedQuestion }),
        signal: controller.signal,
      })

      if (!response.ok) throw await responseError(response)
      const result = await response.json() as ReceiptQuestionResponse
      if (!controller.signal.aborted) setQuestionResponse(result)
    } catch (requestError) {
      if (!controller.signal.aborted) setError(requestError instanceof Error ? requestError.message : 'Could not query receipts.')
    } finally {
      if (!controller.signal.aborted) setAsking(false)
    }
  }

  const pendingCount = receipts.filter((receipt) => receipt.status === 'Pending').length

  return (
    <div className="app-shell" onClickCapture={event => {
      const target = event.target
      const link = target instanceof Element ? target.closest('a') : null
      if (!link || link.target === '_blank' || !link.getAttribute('href')?.startsWith('/') || link.getAttribute('href') === pathname) return
      if (saving || projectsState.saving || (reviewDirty && !window.confirm('Discard unsaved review or property changes?'))) {
        event.preventDefault(); event.stopPropagation()
      } else {
        if (reviewDirty) {
          const source = selectedReceiptForDraft()
          setDraft(source ? toDraft(source) : null)
        }
      }
    }}>
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
              disabled={properties.length === 0 || uploading || projectsState.saving}
            >
              {properties.map((property) => <option key={property.id} value={property.id}>{property.name}</option>)}
            </select>
            <button
              type="button"
              className="text-button"
              onClick={() => void createProperty()}
              disabled={loading || uploading || projectsState.saving}
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
        <NavLink to="/admin"><Settings size={16} aria-hidden="true" />Admin</NavLink>
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
                        {source.documentDate || 'Date not set'} · Receipt total: {formatAmount(source.totalAmount, source.currency)}
                        {source.sourceProjects && <> · Source projects: {source.sourceProjects.join(', ')}</>}
                        {source.matchedItems?.map(item => <small key={item.id}>{item.description} · {item.project} · {item.amount ?? 'Amount missing'}</small>)}
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

        {view === 'admin' && selectedPropertyId && <Admin
          propertyName={properties.find(property => property.id === selectedPropertyId)?.name ?? ''}
          projectsState={projectsState}
        />}

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
              <form aria-busy={saving} inert={saving} onSubmit={(event) => { event.preventDefault(); void saveReview(false) }}>
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

                <div className={`document-preview${selectedReceipt.fileName.toLowerCase().endsWith('.pdf') ? '' : ' document-preview-image'}`}>
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
                        <input className="currency-input" aria-label="Currency" value="SEK" readOnly />
                      </div>
                    </label>
                    <p className="field-wide">This first version supports SEK only. No currency conversions.</p>
                    {(draft.currency !== 'SEK' || draft.expenses.some(expense => expense.currency !== 'SEK')) && <div className="message message-error field-wide" role="status">
                      <p>Stored receipt currency: {draft.currency || 'not set'}. Some amounts are not SEK and cannot be approved or included in confirmed report totals. Their original currency is preserved.</p>
                      <button type="button" className="text-button" onClick={() => {
                        if (!window.confirm('Only correct this if the original receipt and all expense amounts are already SEK and the currency was misread. This does not convert or change any amounts. Continue?')) return
                        setDraft(current => current ? { ...current, currency: 'SEK',
                          expenses: current.expenses.map(expense => ({ ...expense, currency: 'SEK', confirm: false })),
                        } : current)
                      }}>Correct a misread currency to SEK</button>
                    </div>}
                    <label className="field field-wide">
                      <span>Description</span>
                      <textarea rows={3} value={draft.description} onChange={(event) => updateDraft('description', event.target.value)} maxLength={4000} />
                    </label>
                  </div>
                </div>

                <div className="form-section line-items-section">
                  <div className="section-label section-label-action">
                    <div><span>02</span><h3>Receipt-wide choices and line items</h3></div>
                    <button className="text-button" type="button" onClick={addLineItem}>Add item +</button>
                  </div>
                  <p className="project-summary">These two choices apply to this receipt and all its line items.</p>
                  <div className="receipt-wide-choices">
                    <ProjectSelect projects={projectsState.projects} value={draft.projectId}
                      onChange={updateReceiptProject}
                      label="Project for this receipt" disabled={projectsState.loading || projectsState.saving}
                      placeholder={draft.projectId === mixedProjectsValue ? { value: mixedProjectsValue, label: 'Mixed projects — choose one' } : undefined} />
                    <ExpenseReview key={selectedReceipt.id} receipt={{ ...selectedReceipt, currency: draft.currency, totalAmountExact: draft.totalAmount,
                      description: draft.description, vendor: draft.vendor }} expenses={draft.expenses}
                      onChange={expenses => updateDraft('expenses', expenses)} disabled={saving} />
                  </div>
                  {projectsState.error && <p className="message message-error" role="alert">{projectsState.error}</p>}
                  {draft.projectId === mixedProjectsValue && <p className="project-summary" role="alert">
                    This receipt currently has mixed project assignments. Choose one project above to apply it to every line item.
                  </p>}
                  {draft.lineItems.length === 0 ? (
                    <div className="line-items-empty"><p>No itemized details. Assigning a project adds one item for the full receipt amount.</p>
                      <button type="button" className="text-button" onClick={() => setDraft(current => current && current.lineItems.length === 0 ? {
                        ...current, expenses: current.expenses.map(expense => ({ ...expense, confirm: false })),
                        lineItems: [wholeReceiptLineItem(current)],
                      } : current)}>Create one item for the whole receipt amount</button></div>
                  ) : (
                    <div className="line-items-list">
                      <table className="line-items-table">
                        <caption className="sr-only">Editable receipt line items</caption>
                        <thead>
                          <tr>
                            <th scope="col">Description</th>
                            <th scope="col">Qty</th>
                            <th scope="col">Unit price</th>
                            <th scope="col">Line total</th>
                            <th scope="col"><span className="sr-only">Actions</span></th>
                          </tr>
                        </thead>
                        <tbody>
                          {draft.lineItems.map((item, index) => (
                            <tr key={item.key}>
                              <td>
                                <input aria-label={`Item ${index + 1} description`} value={item.description}
                                  onChange={(event) => updateLineItem(index, { description: event.target.value })} maxLength={1000} />
                              </td>
                              <td>
                                <input aria-label={`Item ${index + 1} quantity`} type="number" min="0" step="0.0001"
                                  value={item.quantity} onChange={(event) => updateLineItem(index, { quantity: event.target.value })} />
                              </td>
                              <td>
                                <input aria-label={`Item ${index + 1} unit price`} type="number" min="0" step="0.0001"
                                  value={item.unitPrice} onChange={(event) => updateLineItem(index, { unitPrice: event.target.value })} />
                              </td>
                              <td>
                                <input aria-label={`Item ${index + 1} line total`} type="number" min="0" step="0.0001"
                                  value={item.lineTotal} onChange={(event) => updateLineItem(index, { lineTotal: event.target.value })} />
                              </td>
                              <td>
                                <button className="remove-button" type="button"
                                  aria-label={`Remove ${item.description || `line item ${index + 1}`}`} onClick={() => removeLineItem(index)}>Remove</button>
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                </div>

                <div className="form-actions">
                  <span className="save-note">Changes are saved to your local archive.</span>
                  <div>
                    {editId ? <>
                      <button className="button-secondary edit-action" type="button" disabled={saving} onClick={() => {
                        if (!reviewDirty || window.confirm('Discard unsaved receipt review?')) {
                          setEditDraft(selectedReceipt ? toDraft(selectedReceipt) : null)
                          navigate('/archive')
                        }
                      }}><X size={16} aria-hidden="true" />Cancel</button>
                      <button className="button-secondary edit-action" type="submit" disabled={saving || !requestToken}>Save draft changes</button>
                      <button className="button-primary edit-action" type="button" disabled={saving || !requestToken} onClick={() => void saveReview(true)}><Save size={16} aria-hidden="true" />{saving ? 'Saving...' : 'Confirm and save'}</button>
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