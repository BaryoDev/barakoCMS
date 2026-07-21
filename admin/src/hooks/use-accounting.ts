import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';

// Mirrors the BarakoCMS.Accounting module responses (camelCase over the wire).
// Read-only: chart of accounts, running balances, and per-account ledgers.

/** Account type enum shared with the Accounting module. */
export enum AccountType {
  Asset = 0,
  Liability = 1,
  Equity = 2,
  Income = 3,
  Expense = 4,
}

export const ACCOUNT_TYPE_LABELS: Record<number, string> = {
  0: 'Asset',
  1: 'Liability',
  2: 'Equity',
  3: 'Income',
  4: 'Expense',
};

export function accountTypeLabel(type: number): string {
  return ACCOUNT_TYPE_LABELS[type] ?? 'Unknown';
}

export interface AccountBalance {
  code: string;
  name: string;
  type: number;
  parentCode: string | null;
  balance: number;
}

export interface Account {
  id: string;
  code: string;
  name: string;
  type: number;
  parentCode: string | null;
  memberId: string | null;
  payeeName: string | null;
  isActive: boolean;
  createdAt: string;
  isDebitNormal: boolean;
}

// The ledger shape isn't guaranteed. Keep the entry type loose and parse
// defensively at the render layer — pick whatever date/memo/amount fields exist.
export interface LedgerEntry {
  date?: string;
  description?: string;
  memo?: string;
  debit?: number;
  credit?: number;
  balance?: number;
  runningBalance?: number;
  [key: string]: unknown;
}

export interface Ledger {
  entries?: LedgerEntry[];
  [key: string]: unknown;
}

/**
 * All accounts with their balance.
 *
 * `asOf` (yyyy-MM-dd) asks the server for balances as at the end of that day —
 * only entries posted on or before it are counted. Omit it for current balances.
 */
export function useBalances(asOf?: string) {
  return useQuery({
    queryKey: ['accounting', 'balances', asOf ?? 'current'],
    queryFn: async () =>
      (
        await api.get<AccountBalance[]>('/api/accounting/balances', {
          params: asOf ? { asOf } : undefined,
        })
      ).data,
  });
}

/** The full chart of accounts. */
export function useAccounts() {
  return useQuery({
    queryKey: ['accounting', 'accounts'],
    queryFn: async () => (await api.get<Account[]>('/api/accounting/accounts')).data,
  });
}

/** Ledger for a single account. Fetched on demand (e.g. when a dialog opens). */
export function useLedger(code: string | undefined) {
  return useQuery({
    queryKey: ['accounting', 'ledger', code],
    queryFn: async () =>
      (await api.get<Ledger>(`/api/accounting/accounts/${encodeURIComponent(code!)}/ledger`)).data,
    enabled: !!code,
    // A 404 means the account simply has no ledger — treat as empty, don't retry.
    retry: false,
  });
}
