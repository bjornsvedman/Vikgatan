import type { Receipt } from './ReviewDesk'
import { blankExpense, type ExpenseDraft, type ImprovementType } from './expenseReviewModel'

export default function ExpenseReview({ receipt, expenses, onChange, disabled }: {
  receipt: Receipt, expenses: ExpenseDraft[], onChange: (expenses: ExpenseDraft[]) => void, disabled: boolean,
}) {
  function update(index: number, patch: Partial<ExpenseDraft>) {
    onChange(expenses.map((expense, current) => current === index
      ? { ...expense, confirm: false, ...patch } : expense))
  }
  return <fieldset className="form-section expense-review" disabled={disabled}>
    <legend>03 What was this receipt for?</legend>
    <p>For one kind of expense, use the whole receipt amount. Add portions only when the receipt contains different kinds of expenses. All portions, including excluded items, must add up to the receipt total.</p>
    <p>ROT, insurance and subsidy adjustments are not assessed in this version. Amounts are recorded expenses, not calculated deductions. Save a draft if information is missing.</p>
    <p><a href="https://www.skatteverket.se/privat/fastigheterochbostad/forsaljningavbostad/avdragforrenoveringarochnybyggnad.4.6d02084411db6e252fe80009228.html" target="_blank" rel="noreferrer">Skatteverket category guidance</a></p>
    {(receipt.expenseSuggestions ?? []).length > 0 && <details>
      <summary>Unconfirmed scan suggestions</summary>
      {receipt.expenseSuggestions!.map((suggestion, index) => <p key={index}>
        <strong>{suggestion.description}: {suggestion.type}</strong> — {suggestion.evidence} · Missing/uncertain: {suggestion.uncertainty}
      </p>)}
      <button type="button" className="text-button" onClick={() => onChange([
        ...expenses, ...receipt.expenseSuggestions!.map(suggestion => ({
          ...blankExpense(suggestion.description ?? '', suggestion.amount?.toString() ?? '', receipt.currency ?? ''),
          type: suggestion.type,
        })),
      ])}>Use suggested expense portions</button>
      <p>Suggestions add new, unconfirmed portions. Check that they do not duplicate portions you already entered.</p>
    </details>}
    <div className="receipt-picker-pages">
      <button type="button" className="text-button" onClick={() => onChange([...expenses,
        blankExpense(receipt.description ?? receipt.vendor ?? receipt.fileName,
          expenses.length === 0 ? receipt.totalAmountExact ?? receipt.totalAmount?.toString() ?? '' : '', receipt.currency ?? ''),
      ])}>{expenses.length === 0 ? 'Use the whole receipt amount' : 'Add another expense portion'}</button>
      <button type="button" className="text-button" onClick={() => onChange([
        ...expenses, ...receipt.lineItems.map(item => blankExpense(item.description, item.lineTotal?.toString() ?? '', receipt.currency ?? '')),
      ])}>Create expense portions from receipt items</button>
    </div>
    <p>Receipt items add new portions, not replacements. Check for duplicates and verify amounts against the receipt.</p>
    {expenses.length === 0 && <p role="status">Classification is missing. Add included or excluded expenses before approval.</p>}
    {expenses.map((expense, index) => <div className="expense-draft" key={expense.key}>
      <h4>Expense portion {index + 1}</h4>
      <div className="field-grid">
        <label className="field field-wide"><span>Work / expense description</span>
          <input maxLength={1000} value={expense.description} onChange={event => update(index, { description: event.target.value })} />
        </label>
        <label className="field"><span>Work / expense date (verify; may differ from receipt)</span>
          <input type="date" value={expense.expenseDate} onChange={event => update(index, { expenseDate: event.target.value })} />
        </label>
        <label className="field"><span>Classification</span><select value={expense.type} onChange={event => update(index, { type: event.target.value as ImprovementType })}>
          <option value="Unclassified">Unclassified</option><option value="BasicImprovement">Basic improvement</option>
          <option value="RepairsAndMaintenance">Repairs and maintenance</option><option value="Excluded">Excluded / not deductible</option>
        </select></label>
        <label className="field"><span>Amount for this portion</span>
          <input inputMode="decimal" value={expense.amount} onChange={event => update(index, { amount: event.target.value })} />
        </label>
        <label className="field"><span>Currency</span>
          <input value="SEK" readOnly />
        </label>
        {expense.currency !== 'SEK' && <p className="message message-error">Stored currency: {expense.currency || 'not set'}. This portion cannot enter confirmed report totals until its currency is explicitly corrected. No conversion is available.</p>}
        {expense.type === 'Excluded' ? <label className="field field-wide"><span>Exclusion reason</span>
          <textarea maxLength={1000} value={expense.exclusionReason} onChange={event => update(index, { exclusionReason: event.target.value })} />
        </label> : <>
          <label className="field field-wide"><span>How was it used, and what changed from before?</span>
            <textarea maxLength={4000} value={expense.workDetails} onChange={event => update(index, { workDetails: event.target.value })} />
          </label>
        </>}
        <label className="field field-wide"><span>Notes (optional)</span>
          <textarea maxLength={4000} value={expense.notes} onChange={event => update(index, { notes: event.target.value })} />
        </label>
      </div>
      <label><input type="checkbox" checked={expense.confirm} onChange={event => update(index, { confirm: event.target.checked })} /> I confirm this classification and the currently known expense facts.</label>
      <button type="button" className="text-button" onClick={() => onChange(expenses.filter((_, current) => current !== index).map(item => ({ ...item, confirm: false })))}>Remove expense portion (receipt retained)</button>
    </div>)}
  </fieldset>
}
