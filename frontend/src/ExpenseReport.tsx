import { useEffect, useRef, useState } from 'react'

type Summary = {
  html: string, generatedAtUtc: string, receiptCount: number, confirmedReceiptCount: number,
  includedTotal: string, excludedTotal: string, unresolved: { receiptId: string }[],
}

export default function ExpenseReport({ propertyId, blocked }: { propertyId: string, blocked: boolean }) {
  const [report, setReport] = useState<Summary | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const frame = useRef<HTMLIFrameElement>(null)
  const request = useRef<AbortController | null>(null)
  const [previewLoaded, setPreviewLoaded] = useState(false)
  const base = `/api/properties/${encodeURIComponent(propertyId)}/expense-report`
  useEffect(() => () => request.current?.abort(), [propertyId])

  async function generate(download: boolean) {
    setBusy(true); setError('')
    const controller = new AbortController()
    request.current = controller
    try {
      const response = await fetch(download ? `${base}/download` : base, { signal: controller.signal })
      if (!response.ok) {
        const problem = await response.json() as { detail?: string, title?: string }
        throw new Error(problem.detail ?? problem.title ?? `Report request failed (${response.status}).`)
      }
      if (download) {
        const content = await response.blob()
        if (controller.signal.aborted) return
        const url = URL.createObjectURL(content)
        const link = document.createElement('a')
        link.href = url; link.download = `expense-summary-${propertyId}.html`
        document.body.append(link); link.click(); link.remove()
        window.setTimeout(() => URL.revokeObjectURL(url), 1000)
      } else {
        const summary = await response.json() as Summary
        if (!controller.signal.aborted) { setPreviewLoaded(false); setReport(summary) }
      }
    } catch (requestError) {
      if (!controller.signal.aborted) setError(requestError instanceof Error ? requestError.message : 'Could not generate expense summary.')
    } finally { if (!controller.signal.aborted) setBusy(false) }
  }

  return <section aria-label="Expense preparation summary">
    <h2>Expense preparation summary (MVP)</h2>
    <p>Download recorded SEK expenses, grouped by expense year and classification. Excluded expenses are separate; unresolved receipts stay outside confirmed totals. This is not a tax deduction calculation or SKV 2197.</p>
    <p>ROT, insurance, subsidies and sale-condition/wear are not assessed. Sale dates are optional for this summary.</p>
    {blocked && <p role="status">Save or discard property changes before generating a summary. Reports use saved data only.</p>}
    {error && <p className="message message-error" role="alert">{error}</p>}
    <button className="button-secondary" disabled={busy || blocked} onClick={() => void generate(false)}>{busy ? 'Generating...' : 'Preview expense summary'}</button>
    <button className="button-primary" disabled={busy || blocked} onClick={() => void generate(true)}>Download expense summary</button>
    <p>HTML download: open it in your browser, then Print / Save as PDF. Each download regenerates from saved data and may differ from an older preview.</p>
    {report && <>
      <p role="status">{report.confirmedReceiptCount} of {report.receiptCount} receipts included · {report.unresolved.length} unresolved · Generated {report.generatedAtUtc}</p>
      <p>Recorded potential improvement expenses: {report.includedTotal} SEK (not deductions). Excluded: {report.excludedTotal} SEK.</p>
      <button className="text-button" disabled={busy || blocked || !previewLoaded} onClick={() => frame.current?.contentWindow?.print()}>Print preview / Save as PDF</button>
      <iframe ref={frame} title="Expense preparation summary preview" srcDoc={report.html}
        onLoad={() => setPreviewLoaded(true)}
        sandbox="allow-same-origin allow-modals" style={{ width: '100%', height: '650px', border: '1px solid #ccc' }} />
    </>}
  </section>
}
