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

export function expenseDrafts(receipt: Receipt): ExpenseDraft[] {
  return (receipt.expenses ?? []).map(expense => ({
    key: expense.id, id: expense.id, description: expense.description,
    expenseDate: expense.expenseDate ?? '', type: expense.type, amount: expense.amount ?? '',
    currency: expense.currency ?? 'SEK', notes: expense.notes ?? '', workDetails: expense.workDetails ?? '',
    exclusionReason: expense.exclusionReason ?? '', rotAmount: expense.rotAmount ?? '',
    otherCompensationAmount: expense.otherCompensationAmount ?? '',
    adjustmentsAlreadyInAmount: expense.adjustmentsAlreadyInAmount,
    confirm: expense.confirmedAtUtc !== null && expense.confirmedReceiptVersion === receipt.reviewVersion,
  }))
}
