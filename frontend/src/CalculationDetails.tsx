import { ExternalLink, ListChecks } from 'lucide-react'

export type ReceiptCalculation = {
  year: number | null
  vendor: string | null
  category: string | null
  groupBy: 'none' | 'year' | 'vendor' | 'category'
  reviewedOnly: boolean
  totals: { label: string, currency: string, amount: string, receiptCount: number }[]
  receipts: {
    id: string
    fileName: string
    vendor: string | null
    documentDate: string | null
    category: string | null
    currency: string
    amount: string
    groupLabel: string
  }[]
}

function CalculationDetails({ calculation }: { calculation: ReceiptCalculation }) {
  return (
    <div className="calculation-details">
      <section className="calculation-scope" aria-label="Calculation scope">
        <strong>{calculation.reviewedOnly ? 'Reviewed receipts only' : 'All review statuses'}</strong>
        <dl>
          <div><dt>Year</dt><dd>{calculation.year ?? 'All years'}</dd></div>
          <div><dt>Vendor</dt><dd>{calculation.vendor || 'All vendors'}</dd></div>
          <div><dt>Category</dt><dd>{calculation.category || 'All categories'}</dd></div>
          <div><dt>Grouped by</dt><dd>{calculation.groupBy === 'none' ? 'Currency' : `${calculation.groupBy} and currency`}</dd></div>
        </dl>
      </section>
      {calculation.receipts.length > 0 && (
        <details className="calculation-breakdown">
          <summary><ListChecks size={17} aria-hidden="true" />Calculation breakdown ({calculation.receipts.length} receipts)</summary>
          {calculation.totals.map((total) => (
            <section className="calculation-group" key={`${total.label}/${total.currency}`} aria-label={`${total.label} / ${total.currency}`}>
              <h3>{total.label} / {total.currency}</h3>
              <div className="calculation-table-scroll">
                <table className="calculation-table">
                  <caption className="sr-only">Contributing receipts for {total.label}, {total.currency}</caption>
                  <thead><tr><th scope="col">Receipt</th><th scope="col">Date</th><th scope="col">Category</th><th scope="col">Recorded amount</th></tr></thead>
                  <tbody>
                    {calculation.receipts.filter((receipt) => receipt.groupLabel === total.label && receipt.currency === total.currency).map((receipt) => (
                      <tr key={receipt.id}>
                        <td><a href={`/api/documents/${receipt.id}/file`} target="_blank" rel="noreferrer" title={`Open ${receipt.fileName}`}>
                          <span>{receipt.vendor || receipt.fileName}<small>{receipt.fileName}</small></span><ExternalLink size={13} aria-hidden="true" />
                        </a></td>
                        <td>{receipt.documentDate ?? 'Not set'}</td>
                        <td>{receipt.category || 'Uncategorized'}</td>
                        <td>{receipt.currency} {receipt.amount}</td>
                      </tr>
                    ))}
                  </tbody>
                  <tfoot><tr><th scope="row" colSpan={3}>SQL total ({total.receiptCount} {total.receiptCount === 1 ? 'receipt' : 'receipts'})</th><td>{total.currency} {total.amount}</td></tr></tfoot>
                </table>
              </div>
            </section>
          ))}
        </details>
      )}
    </div>
  )
}

export default CalculationDetails