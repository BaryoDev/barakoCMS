'use client';

import { useState } from 'react';
import { PageHeader } from '@/components/patterns/page-header';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import {
  useClientErrors,
  useResolveClientError,
  type ClientErrorDto,
  type ErrorSeverity,
} from '@/hooks/use-errors';

type StatusFilter = 'unresolved' | 'resolved' | 'all';
type SeverityFilter = 'all' | ErrorSeverity;

const STATUS_OPTIONS: { value: StatusFilter; label: string }[] = [
  { value: 'unresolved', label: 'Unresolved' },
  { value: 'resolved', label: 'Resolved' },
  { value: 'all', label: 'All' },
];

const SEVERITY_OPTIONS: { value: SeverityFilter; label: string }[] = [
  { value: 'all', label: 'All severities' },
  { value: 'error', label: 'Error' },
  { value: 'warning', label: 'Warning' },
];

function resolvedParam(status: StatusFilter): boolean | undefined {
  if (status === 'unresolved') return false;
  if (status === 'resolved') return true;
  return undefined;
}

function formatDate(value?: string | null): string {
  if (!value) return '—';
  const d = new Date(value);
  return isNaN(d.getTime()) ? '—' : d.toLocaleString();
}

function SeverityBadge({ severity }: { severity: string }) {
  const isWarning = severity === 'warning';
  return (
    <Badge
      variant={isWarning ? 'outline' : 'destructive'}
      className={
        isWarning ? 'border-amber-500/40 bg-amber-500/10 text-amber-700 dark:text-amber-400' : ''
      }
    >
      {severity}
    </Badge>
  );
}

export default function ErrorsPage() {
  const [status, setStatus] = useState<StatusFilter>('unresolved');
  const [severity, setSeverity] = useState<SeverityFilter>('all');
  const [q, setQ] = useState('');
  const [selected, setSelected] = useState<ClientErrorDto | null>(null);

  const { data, isLoading, isError } = useClientErrors({
    page: 1,
    pageSize: 25,
    resolved: resolvedParam(status),
    severity: severity === 'all' ? undefined : severity,
    q: q.trim() || undefined,
  });

  const resolve = useResolveClientError();
  const showResolve = status !== 'resolved';
  const rows = data?.items ?? [];

  return (
    <>
      <PageHeader
        title="Errors"
        description="Client-side errors captured across the platform, newest first."
        actions={
          <div className="flex flex-wrap items-center gap-2">
            <Input
              value={q}
              onChange={(e) => setQ(e.target.value)}
              placeholder="Search messages…"
              className="h-8 w-44"
            />
            <Select value={status} onValueChange={(v) => setStatus(v as StatusFilter)}>
              <SelectTrigger className="w-36" size="sm">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {STATUS_OPTIONS.map((o) => (
                  <SelectItem key={o.value} value={o.value}>
                    {o.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <Select value={severity} onValueChange={(v) => setSeverity(v as SeverityFilter)}>
              <SelectTrigger className="w-40" size="sm">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {SEVERITY_OPTIONS.map((o) => (
                  <SelectItem key={o.value} value={o.value}>
                    {o.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        }
      />

      {isError ? (
        <Card>
          <CardHeader>
            <CardTitle className="text-sm font-medium">Diagnostics module not available</CardTitle>
          </CardHeader>
          <CardContent className="text-muted-foreground text-sm">
            <p>
              The client-error diagnostics module isn&apos;t installed on the API host, or the
              endpoint couldn&apos;t be reached. Once the module is wired up, captured errors show up
              here.
            </p>
          </CardContent>
        </Card>
      ) : isLoading ? (
        <Card>
          <CardContent className="space-y-2 pt-6">
            {Array.from({ length: 6 }).map((_, i) => (
              <Skeleton key={i} className="h-10 w-full" />
            ))}
          </CardContent>
        </Card>
      ) : rows.length === 0 ? (
        <Card>
          <CardContent className="text-muted-foreground py-12 text-center text-sm">
            No errors 🎉
          </CardContent>
        </Card>
      ) : (
        <Card>
          <CardContent className="pt-6">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Severity</TableHead>
                  <TableHead>Message</TableHead>
                  <TableHead className="text-right">Count</TableHead>
                  <TableHead>Last seen</TableHead>
                  <TableHead>Tenant</TableHead>
                  <TableHead>Source</TableHead>
                  {showResolve && <TableHead className="w-0" />}
                </TableRow>
              </TableHeader>
              <TableBody>
                {rows.map((row) => (
                  <TableRow
                    key={row.id}
                    className="cursor-pointer"
                    onClick={() => setSelected(row)}
                  >
                    <TableCell>
                      <SeverityBadge severity={row.severity} />
                    </TableCell>
                    <TableCell className="max-w-xs">
                      <span className="block truncate font-mono text-xs">{row.message}</span>
                    </TableCell>
                    <TableCell className="text-right font-mono text-xs tabular-nums">
                      {row.count}
                    </TableCell>
                    <TableCell className="text-muted-foreground text-xs whitespace-nowrap">
                      {formatDate(row.lastSeenAt)}
                    </TableCell>
                    <TableCell className="text-muted-foreground text-xs">
                      {row.tenant || '—'}
                    </TableCell>
                    <TableCell className="text-muted-foreground max-w-[12rem] truncate text-xs">
                      {row.source || '—'}
                    </TableCell>
                    {showResolve && (
                      <TableCell className="text-right">
                        <Button
                          size="xs"
                          variant="outline"
                          disabled={resolve.isPending}
                          onClick={(e) => {
                            e.stopPropagation();
                            resolve.mutate(row.id);
                          }}
                        >
                          Resolve
                        </Button>
                      </TableCell>
                    )}
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      )}

      <Dialog open={!!selected} onOpenChange={(open) => !open && setSelected(null)}>
        <DialogContent className="sm:max-w-2xl">
          {selected && (
            <>
              <DialogHeader>
                <DialogTitle className="flex items-center gap-2">
                  <SeverityBadge severity={selected.severity} />
                  <span className="truncate font-mono text-sm">{selected.kind}</span>
                </DialogTitle>
                <DialogDescription className="font-mono break-words">
                  {selected.message}
                </DialogDescription>
              </DialogHeader>

              <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-sm sm:grid-cols-3">
                <Detail label="Kind" value={selected.kind} />
                <Detail label="Source" value={selected.source} />
                <Detail label="Status" value={selected.status?.toString()} />
                <Detail label="Count" value={selected.count.toString()} />
                <Detail label="Tenant" value={selected.tenant} />
                <Detail label="User" value={selected.username} />
                <Detail label="App version" value={selected.appVersion} />
                <Detail label="First seen" value={formatDate(selected.firstSeenAt)} />
                <Detail label="Last seen" value={formatDate(selected.lastSeenAt)} />
                <Detail className="col-span-full" label="URL" value={selected.url} />
                <Detail className="col-span-full" label="User agent" value={selected.userAgent} />
              </dl>

              {selected.stack && (
                <div>
                  <p className="text-muted-foreground mb-1 text-xs font-medium">Stack trace</p>
                  <pre className="bg-muted max-h-72 overflow-auto rounded-md p-3 text-xs whitespace-pre-wrap">
                    {selected.stack}
                  </pre>
                </div>
              )}
            </>
          )}
        </DialogContent>
      </Dialog>
    </>
  );
}

function Detail({
  label,
  value,
  className,
}: {
  label: string;
  value?: string | null;
  className?: string;
}) {
  return (
    <div className={className}>
      <dt className="text-muted-foreground text-xs">{label}</dt>
      <dd className="break-words">{value || '—'}</dd>
    </div>
  );
}
