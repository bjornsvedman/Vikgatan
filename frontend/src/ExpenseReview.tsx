import type { Receipt } from './ReviewDesk'
import {
  blankExpense, consolidateExpenseDrafts, type ExpenseDraft, type ImprovementType,
} from './expenseReviewModel'

const legacyClassificationsValue = '__legacy_classifications__'
const improvementTypes: { value: ImprovementType, label: string }[] = [
  { value: 'Unclassified', label: 'Choose classification' },
  { value: 'BasicImprovement', label: 'Basic improvement' },
  { value: 'RepairsAndMaintenance', label: 'Repairs and maintenance' },
  { value: 'Excluded', label: 'Excluded / not deductible' },
]

export default function ExpenseReview({ receipt, expenses, onChange, disabled }: {
  receipt: Receipt, expenses: ExpenseDraft[], onChange: (expenses: ExpenseDraft[]) => void, disabled: boolean,
}) {
  const selectedType = expenses.length === 0 ? ''
    : expenses.length === 1 ? expenses[0].type
      : expenses.every(expense => expense.type === expenses[0].type) ? expenses[0].type
        : legacyClassificationsValue

  function chooseClassification(type: ImprovementType) {
    if (expenses.length === 0) {
      const expense = blankExpense(receipt.description ?? receipt.vendor ?? receipt.fileName,
        receipt.totalAmountExact ?? receipt.totalAmount?.toString() ?? '', receipt.currency ?? '')
      onChange([{ ...expense, expenseDate: receipt.documentDate ?? '', type, confirm: true }])
    } else if (expenses.length === 1) {
      onChange([{ ...expenses[0], type, confirm: true }])
    } else {
      const consolidated = consolidateExpenseDrafts(expenses, type)
      if (consolidated) onChange([consolidated])
    }
  }

  return <label className="field receipt-classification-type">
    <span>Classification for the entire receipt</span>
    <select value={selectedType} disabled={disabled}
      onChange={event => event.target.value !== legacyClassificationsValue
        && chooseClassification(event.target.value as ImprovementType)}>
      {selectedType === legacyClassificationsValue
        ? <option value={legacyClassificationsValue}>Choose one classification for this receipt</option>
        : selectedType === '' ? <option value="">Choose classification</option> : null}
      {improvementTypes.map(type => <option key={type.value} value={type.value}>{type.label}</option>)}
    </select>
  </label>
}
