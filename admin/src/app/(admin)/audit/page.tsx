'use client';

import { useState } from 'react';
import { PageHeader } from '@/components/patterns/page-header';
import { PaginationControls } from '@/components/patterns/pagination-controls';
import { Card, CardContent } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { Badge } from '@/components/ui/badge';
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
import { useAuditLog, type AuditEventDto } from '@/hooks/use-audit-log';

function formatDate(value?: string | null): string {
  if (!value) return '—';
  const d = new Date(value);
  return isNaN(d.getTime()) ? '—' : d.toLocaleString();
}

function ActionBadge({ action }: { action: string }) {
  const isFailure = action.includes('failed') || action.includes('blocked') || action.includes('locked') || action.includes('reuse_detected');
  return (
    <Badge
      variant={isFailure ? 'destructive' : 'outline'}
      className={isFailure ? '' : 'border-emerald-500/40 bg-emerald-500/10 text-emerald-700 dark:text-emerald-400'}
    >
      <span className="font-mono text-xs">{action}</span>
    </Badge>
  );
}

export default function AuditLogPage() {
  const [action, setAction] = useState('');
  const [tenant, setTenant] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<AuditEventDto | null>(null);

  const { data, isLoading, isError } = useAuditLog({
    page,
    pageSize: 25,
    action: action.trim() || undefined,
    tenant: tenant.trim() || undefined,
    from: from ? new Date(from).toISOString() : undefined,
    to: to ? new Date(to).toISOString() : undefined,
  });

  const rows = data?.items ?? [];

  return (
    <>
      <PageHeader
        title="Audit log"
        description="Who did what, when — auth events and sensitive administrative actions, newest first."
        actions={
          <div className="flex flex-wrap items-center gap-2">
            <Input
              value={action}
              onChange={(e) => {
                setAction(e.target.value);
                setPage(1);
              }}
              placeholder="Action, e.g. auth.login.failed"
              className="h-8 w-56"
            />
            <Input
              value={tenant}
              onChange={(e) => {
                setTenant(e.target.value);
                setPage(1);
              }}
              placeholder="Tenant"
              className="h-8 w-32"
            />
            <Input
              type="date"
              value={from}
              onChange={(e) => {
                setFrom(e.target.value);
                setPage(1);
              }}
              className="h-8 w-36"
            />
            <Input
              type="date"
              value={to}
              onChange={(e) => {
                setTo(e.target.value);
                setPage(1);
              }}
              className="h-8 w-36"
            />
          </div>
        }
      />

      {isError ? (
        <Card>
          <CardContent className="text-muted-foreground py-12 text-center text-sm">
            Couldn&apos;t reach the audit log endpoint.
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
            No matching audit events.
          </CardContent>
        </Card>
      ) : (
        <Card>
          <CardContent className="pt-6">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>When</TableHead>
                  <TableHead>Action</TableHead>
                  <TableHead>Actor</TableHead>
                  <TableHead>Target</TableHead>
                  <TableHead>Tenant</TableHead>
                  <TableHead>IP</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {rows.map((row) => (
                  <TableRow
                    key={row.id}
                    className="focus-visible:ring-ring cursor-pointer focus-visible:ring-2 focus-visible:outline-none"
                    tabIndex={0}
                    role="button"
                    aria-label={`View audit event details: ${row.action}`}
                    onClick={() => setSelected(row)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault();
                        setSelected(row);
                      }
                    }}
                  >
                    <TableCell className="text-muted-foreground text-xs whitespace-nowrap">
                      {formatDate(row.createdAt)}
                    </TableCell>
                    <TableCell>
                      <ActionBadge action={row.action} />
                    </TableCell>
                    <TableCell className="text-xs">{row.actorUsername || '—'}</TableCell>
                    <TableCell className="text-muted-foreground max-w-[12rem] truncate text-xs">
                      {row.targetType ? `${row.targetType}:${row.targetId}` : '—'}
                    </TableCell>
                    <TableCell className="text-muted-foreground text-xs">{row.tenantSlug}</TableCell>
                    <TableCell className="text-muted-foreground font-mono text-xs">
                      {row.ipAddress || '—'}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            {data && <PaginationControls page={data} onPageChange={setPage} />}
          </CardContent>
        </Card>
      )}

      <Dialog open={!!selected} onOpenChange={(open) => !open && setSelected(null)}>
        <DialogContent className="sm:max-w-lg">
          {selected && (
            <>
              <DialogHeader>
                <DialogTitle className="flex items-center gap-2">
                  <ActionBadge action={selected.action} />
                </DialogTitle>
                <DialogDescription>{formatDate(selected.createdAt)}</DialogDescription>
              </DialogHeader>

              <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-sm">
                <Detail label="Actor" value={selected.actorUsername} />
                <Detail label="Actor ID" value={selected.actorUserId} />
                <Detail label="Target type" value={selected.targetType} />
                <Detail label="Target ID" value={selected.targetId} />
                <Detail label="Tenant" value={selected.tenantSlug} />
                <Detail label="IP address" value={selected.ipAddress} />
              </dl>

              {selected.metadata && Object.keys(selected.metadata).length > 0 && (
                <div>
                  <p className="text-muted-foreground mb-1 text-xs font-medium">Metadata</p>
                  <pre className="bg-muted max-h-56 overflow-auto rounded-md p-3 text-xs whitespace-pre-wrap">
                    {JSON.stringify(selected.metadata, null, 2)}
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

function Detail({ label, value }: { label: string; value?: string | null }) {
  return (
    <div>
      <dt className="text-muted-foreground text-xs">{label}</dt>
      <dd className="break-words">{value || '—'}</dd>
    </div>
  );
}
