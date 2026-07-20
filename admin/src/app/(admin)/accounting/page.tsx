'use client';

import { useState } from 'react';
import { PageHeader } from '@/components/patterns/page-header';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { Badge } from '@/components/ui/badge';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import {
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
} from '@/components/ui/tabs';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import {
  useBalances,
  useAccounts,
  useLedger,
  accountTypeLabel,
  type AccountBalance,
  type Account,
  type LedgerEntry,
} from '@/hooks/use-accounting';

// Currency-less money: 2 decimals, grouped. Negatives are colored at the call site.
const money = (n: number) =>
  new Intl.NumberFormat(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(n);

function Money({ value }: { value: number }) {
  return (
    <span className={`font-mono tabular-nums ${value < 0 ? 'text-rose-600' : ''}`}>
      {money(value)}
    </span>
  );
}

function TypeBadge({ type }: { type: number }) {
  return (
    <Badge variant="secondary" className="font-normal">
      {accountTypeLabel(type)}
    </Badge>
  );
}

export default function AccountingPage() {
  return (
    <>
      <PageHeader
        title="Accounting"
        description="Chart of accounts and balances from the Accounting module."
      />

      <Tabs defaultValue="balances">
        <TabsList>
          <TabsTrigger value="balances">Balances</TabsTrigger>
          <TabsTrigger value="accounts">Chart of accounts</TabsTrigger>
        </TabsList>
        <TabsContent value="balances">
          <BalancesTab />
        </TabsContent>
        <TabsContent value="accounts">
          <AccountsTab />
        </TabsContent>
      </Tabs>
    </>
  );
}

function ModuleUnavailable() {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm font-medium">Accounting module not available</CardTitle>
      </CardHeader>
      <CardContent className="text-muted-foreground text-sm">
        <p>
          The Accounting module didn&apos;t respond. It may not be installed on this API host, or the
          endpoints are turned off. Once it&apos;s wired up, balances and the chart of accounts show
          here.
        </p>
      </CardContent>
    </Card>
  );
}

function TableSkeleton() {
  return (
    <div className="space-y-2">
      {Array.from({ length: 6 }).map((_, i) => (
        <Skeleton key={i} className="h-9 w-full" />
      ))}
    </div>
  );
}

function EmptyCard({ children }: { children: React.ReactNode }) {
  return (
    <Card>
      <CardContent className="text-muted-foreground py-8 text-center text-sm">{children}</CardContent>
    </Card>
  );
}

function BalancesTab() {
  const { data, isLoading, isError } = useBalances();
  const [selected, setSelected] = useState<AccountBalance | null>(null);

  if (isLoading) return <TableSkeleton />;
  if (isError) return <ModuleUnavailable />;
  if (!data || data.length === 0) return <EmptyCard>No accounts yet</EmptyCard>;

  return (
    <>
      <Card className="py-0">
        <CardContent className="px-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Code</TableHead>
                <TableHead>Name</TableHead>
                <TableHead>Type</TableHead>
                <TableHead className="text-right">Balance</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.map((row) => (
                <TableRow
                  key={row.code}
                  className="cursor-pointer"
                  onClick={() => setSelected(row)}
                >
                  <TableCell className="font-mono text-xs">{row.code}</TableCell>
                  <TableCell>{row.name}</TableCell>
                  <TableCell>
                    <TypeBadge type={row.type} />
                  </TableCell>
                  <TableCell className="text-right">
                    <Money value={row.balance} />
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      <LedgerDialog account={selected} onClose={() => setSelected(null)} />
    </>
  );
}

function AccountsTab() {
  const { data, isLoading, isError } = useAccounts();

  if (isLoading) return <TableSkeleton />;
  if (isError) return <ModuleUnavailable />;
  if (!data || data.length === 0) return <EmptyCard>No accounts yet</EmptyCard>;

  return (
    <Card className="py-0">
      <CardContent className="px-0">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Code</TableHead>
              <TableHead>Name</TableHead>
              <TableHead>Type</TableHead>
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {data.map((acct: Account) => {
              const isChild = !!acct.parentCode;
              return (
                <TableRow key={acct.id} className={acct.isActive ? '' : 'text-muted-foreground'}>
                  <TableCell className="font-mono text-xs">{acct.code}</TableCell>
                  <TableCell>
                    <span className={isChild ? 'pl-4' : ''}>
                      {isChild && <span className="text-muted-foreground mr-1">↳</span>}
                      {acct.name}
                    </span>
                    {isChild && (
                      <span className="text-muted-foreground ml-2 font-mono text-xs">
                        under {acct.parentCode}
                      </span>
                    )}
                  </TableCell>
                  <TableCell>
                    <TypeBadge type={acct.type} />
                  </TableCell>
                  <TableCell className="text-right">
                    {!acct.isActive && (
                      <Badge variant="outline" className="text-muted-foreground font-normal">
                        inactive
                      </Badge>
                    )}
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </CardContent>
    </Card>
  );
}

// --- Ledger (defensive parsing) -------------------------------------------

/** Pull an entries array out of an unpredictable ledger payload. */
function extractEntries(ledger: unknown): LedgerEntry[] {
  if (!ledger) return [];
  if (Array.isArray(ledger)) return ledger as LedgerEntry[];
  if (typeof ledger === 'object') {
    const obj = ledger as Record<string, unknown>;
    if (Array.isArray(obj.entries)) return obj.entries as LedgerEntry[];
    if (Array.isArray(obj.lines)) return obj.lines as LedgerEntry[];
    if (Array.isArray(obj.items)) return obj.items as LedgerEntry[];
    // Fall back to the first array-valued property we find.
    const firstArray = Object.values(obj).find((v) => Array.isArray(v));
    if (Array.isArray(firstArray)) return firstArray as LedgerEntry[];
  }
  return [];
}

function pick(entry: LedgerEntry, keys: string[]): unknown {
  for (const k of keys) {
    if (entry[k] !== undefined && entry[k] !== null) return entry[k];
  }
  return undefined;
}

function asNumber(v: unknown): number | undefined {
  if (typeof v === 'number' && isFinite(v)) return v;
  if (typeof v === 'string' && v.trim() !== '' && !isNaN(Number(v))) return Number(v);
  return undefined;
}

function formatDate(v: unknown): string {
  if (typeof v !== 'string' && typeof v !== 'number') return '';
  const d = new Date(v);
  if (isNaN(d.getTime())) return String(v);
  return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

function LedgerDialog({
  account,
  onClose,
}: {
  account: AccountBalance | null;
  onClose: () => void;
}) {
  return (
    <Dialog open={!!account} onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>
            {account ? (
              <span className="flex items-center gap-2">
                <span className="font-mono text-sm">{account.code}</span>
                {account.name}
              </span>
            ) : (
              'Ledger'
            )}
          </DialogTitle>
          <DialogDescription>
            {account ? `Ledger entries for this account.` : ''}
          </DialogDescription>
        </DialogHeader>
        {account && <LedgerBody code={account.code} />}
      </DialogContent>
    </Dialog>
  );
}

function LedgerBody({ code }: { code: string }) {
  const { data, isLoading, isError, error } = useLedger(code);

  if (isLoading) {
    return (
      <div className="space-y-2 py-2">
        {Array.from({ length: 5 }).map((_, i) => (
          <Skeleton key={i} className="h-8 w-full" />
        ))}
      </div>
    );
  }

  // A 404 means this account simply has no ledger — show the empty state, not an error.
  const status =
    error && typeof error === 'object' && 'response' in error
      ? (error as { response?: { status?: number } }).response?.status
      : undefined;
  if (isError && status !== 404) {
    return (
      <p className="text-muted-foreground py-6 text-center text-sm">
        Couldn&apos;t load this ledger.
      </p>
    );
  }

  const entries = extractEntries(data);
  if (entries.length === 0) {
    return (
      <p className="text-muted-foreground py-6 text-center text-sm">
        No ledger entries for this account.
      </p>
    );
  }

  return (
    <div className="max-h-[60vh] overflow-y-auto">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Date</TableHead>
            <TableHead>Description</TableHead>
            <TableHead className="text-right">Debit</TableHead>
            <TableHead className="text-right">Credit</TableHead>
            <TableHead className="text-right">Balance</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {entries.map((entry, i) => {
            const date = formatDate(pick(entry, ['date', 'entryDate', 'postedAt', 'createdAt', 'timestamp']));
            const desc = pick(entry, ['description', 'memo', 'narrative', 'note', 'reference']);
            const debit = asNumber(pick(entry, ['debit', 'debitAmount', 'dr']));
            const credit = asNumber(pick(entry, ['credit', 'creditAmount', 'cr']));
            const balance = asNumber(pick(entry, ['runningBalance', 'balance', 'runningTotal']));
            return (
              <TableRow key={i}>
                <TableCell className="whitespace-nowrap text-xs">{date || '—'}</TableCell>
                <TableCell className="whitespace-normal">
                  {typeof desc === 'string' || typeof desc === 'number' ? String(desc) : '—'}
                </TableCell>
                <TableCell className="text-right">
                  {debit !== undefined ? <Money value={debit} /> : <span className="text-muted-foreground">—</span>}
                </TableCell>
                <TableCell className="text-right">
                  {credit !== undefined ? <Money value={credit} /> : <span className="text-muted-foreground">—</span>}
                </TableCell>
                <TableCell className="text-right">
                  {balance !== undefined ? <Money value={balance} /> : <span className="text-muted-foreground">—</span>}
                </TableCell>
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </div>
  );
}
