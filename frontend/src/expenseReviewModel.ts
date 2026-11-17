import type { Receipt } from './ReviewDesk'

export type ImprovementType = 'Unclassified' | 'BasicImprovement' | 'RepairsAndMaintenance' | 'Excluded'
export type ExpenseFacts = {
  id: string
  description: string
  expenseDate: string | null
  type: ImprovementType
  amount: string | null
  currency: string | null
  notes: string | null
  workDetails: string | null
  exclusionReason: string | null
  rotAmount: string | null
  otherCompensationAmount: string | null
  adjustmentsAlreadyInAmount: boolean | null
  confirmedAtUtc: string | null
  confirmedReceiptVersion: number | null
}
export type ExpenseDraft = {
  key: string
  id: string | null
  description: string
  expenseDate: string
  type: ImprovementType
  amount: string
  currency: string
  notes: string
  workDetails: string
  exclusionReason: string
  rotAmount: string
  otherCompensationAmount: string
  adjustmentsAlreadyInAmount: boolean | null
  confirm: boolean
}

export function blankExpense(description = '', amount = '', currency = 'SEK'): ExpenseDraft {
  return {
    key: crypto.randomUUID(), id: null, description, expenseDate: '', type: 'Unclassified',
    amount, currency: currency || 'SEK', notes: '', workDetails: '', exclusionReason: '', rotAmount: '',
    otherCompensationAmount: '', adjustmentsAlreadyInAmount: null, confirm: false,
  }
}

function addExpenseAmounts(expenses: ExpenseDraft[]) {
  let total = 0n
  for (const expense of expenses) {
    const match = expense.amount.trim().match(/^(\d+)(?:\.(\d{1,4}))?$/)
    if (!match) return ''
    total += BigInt(match[1]) * 10000n + BigInt((match[2] ?? '').padEnd(4, '0'))
  }
  const whole = total / 10000n
  const fraction = (total % 10000n).toString().padStart(4, '0').replace(/0+$/, '')
  return fraction ? `${whole}.${fraction}` : whole.toString()
}

function joined(values: string[], maximum: number) {
  const result = values.filter(Boolean).join('\n\n')
  return result.length <= maximum ? result : ''
}

export function consolidateExpenseDrafts(expenses: ExpenseDraft[], type: ImprovementType): ExpenseDraft | null {
  if (expenses.length === 0) return null
  const first = expenses[0]
  const workDetails = joined(expenses.map(expense => expense.workDetails), 4000)
  const exclusionReason = joined(expenses.map(expense => expense.exclusionReason), 1000)
  const portionSummary = `Original portions: ${expenses.map(expense =>
    `${expense.type}: ${expense.amount || 'amount missing'} ${expense.currency || 'currency missing'} on ${expense.expenseDate || 'date missing'}`
    + ` (ROT ${expense.rotAmount || 'not set'}, compensation ${expense.otherCompensationAmount || 'not set'},`
    + ` adjustments included ${expense.adjustmentsAlreadyInAmount === null ? 'not set' : expense.adjustmentsAlreadyInAmount})`).join('; ')}`
  const notes = joined([...expenses.map(expense => expense.notes), portionSummary], 4000)
  if (!notes || expenses.some(expense => expense.workDetails && !workDetails)
    || expenses.some(expense => expense.exclusionReason && !exclusionReason)
    || expenses.some(expense => expense.notes && !notes)) return null
  const sameDate = expenses.every(expense => expense.expenseDate === first.expenseDate)
  const sameCurrency = expenses.every(expense => expense.currency === first.currency)
  const description = expenses.map(expense => expense.description).filter(Boolean).join('; ')
  if (description.length > 1000) return null
  return {
    ...first,
    description,
    expenseDate: sameDate ? first.expenseDate : '',
    type,
    amount: addExpenseAmounts(expenses),
    currency: sameCurrency ? first.currency : '',
    notes,
    workDetails,
    exclusionReason,
    rotAmount: expenses.every(expense => expense.rotAmount === first.rotAmount) ? first.rotAmount : '',
    otherCompensationAmount: expenses.every(expense => expense.otherCompensationAmount === first.otherCompensationAmount)
      ? first.otherCompensationAmount : '',
    adjustmentsAlreadyInAmount: expenses.every(expense => expense.adjustmentsAlreadyInAmount === first.adjustmentsAlreadyInAmount)
      ? first.adjustmentsAlreadyInAmount : null,
    confirm: false,
  }
}

export function expenseDrafts(receipt: Receipt): ExpenseDraft[] {
  const drafts = (receipt.expenses ?? []).map(expense => ({
    key: expense.id, id: expense.id, description: expense.description,
    expenseDate: expense.expenseDate ?? '', type: expense.type, amount: expense.amount ?? '',
    currency: expense.currency ?? 'SEK', notes: expense.notes ?? '', workDetails: expense.workDetails ?? '',
    exclusionReason: expense.exclusionReason ?? '', rotAmount: expense.rotAmount ?? '',
    otherCompensationAmount: expense.otherCompensationAmount ?? '',
    adjustmentsAlreadyInAmount: expense.adjustmentsAlreadyInAmount,
    confirm: expense.confirmedAtUtc !== null && expense.confirmedReceiptVersion === receipt.reviewVersion,
  }))
  if (drafts.length <= 1 || !drafts.every(expense => expense.type === drafts[0].type)) return drafts
  const consolidated = consolidateExpenseDrafts(drafts, drafts[0].type)
  return consolidated ? [consolidated] : drafts
}
