'use client';

import { useMemo, useState } from 'react';
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
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { usePwaInstalls } from '@/hooks/use-pwa';

const FILTERS = [
  { value: 'all', label: 'All devices' },
  { value: 'installed', label: 'Installed only' },
  { value: 'anonymous', label: 'Anonymous only' },
];

export default function PwaPage() {
  const [filter, setFilter] = useState('all');
  const { data, isLoading, isError } = usePwaInstalls();

  const stats = useMemo(() => {
    if (!data) return { total: 0, installed: 0, signedIn: 0 };
    return {
      total: data.length,
      installed: data.filter((d) => d.installed).length,
      signedIn: data.filter((d) => d.username != null).length,
    };
  }, [data]);

  const rows = useMemo(() => {
    if (!data) return [];
    if (filter === 'installed') return data.filter((d) => d.installed);
    if (filter === 'anonymous') return data.filter((d) => d.username == null);
    return data;
  }, [data, filter]);

  return (
    <>
      <PageHeader
        title="PWA installs"
        description="Devices that have run the app, and who installed it to their home screen."
        actions={
          <Select value={filter} onValueChange={setFilter}>
            <SelectTrigger size="sm" className="w-40">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {FILTERS.map((f) => (
                <SelectItem key={f.value} value={f.value}>
                  {f.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        }
      />

      {isError ? (
        <Card>
          <CardHeader>
            <CardTitle className="text-sm font-medium">PWA tracking not available</CardTitle>
          </CardHeader>
          <CardContent className="text-muted-foreground text-sm">
            No PWA installs endpoint responded — the BarakoCMS.Pwa module isn&apos;t installed here.
          </CardContent>
        </Card>
      ) : (
        <>
          {data && data.length > 0 && (
            <div className="mb-4 grid grid-cols-3 gap-3">
              {[
                { label: 'Total devices', value: stats.total },
                { label: 'Installed', value: stats.installed },
                { label: 'Signed-in users', value: stats.signedIn },
              ].map((s) => (
                <Card key={s.label}>
                  <CardContent className="py-4">
                    <div className="text-muted-foreground text-xs">{s.label}</div>
                    <div className="mt-1 text-2xl font-semibold tabular-nums">{s.value}</div>
                  </CardContent>
                </Card>
              ))}
            </div>
          )}

          <Card>
            <CardContent className="p-0">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>User</TableHead>
                    <TableHead>Installed</TableHead>
                    <TableHead>Platform</TableHead>
                    <TableHead className="text-right">Launches</TableHead>
                    <TableHead>Installed on</TableHead>
                    <TableHead>Last seen</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {isLoading &&
                    Array.from({ length: 6 }).map((_, i) => (
                      <TableRow key={i}>
                        <TableCell colSpan={6}>
                          <Skeleton className="h-5 w-full" />
                        </TableCell>
                      </TableRow>
                    ))}
                  {!isLoading && rows.length === 0 && (
                    <TableRow>
                      <TableCell colSpan={6} className="text-muted-foreground py-8 text-center text-sm">
                        No devices have reported yet.
                      </TableCell>
                    </TableRow>
                  )}
                  {!isLoading &&
                    rows.map((d, i) => (
                      <TableRow key={`${d.userId ?? 'anon'}-${d.firstSeenAt}-${i}`}>
                        <TableCell>
                          {d.username ? (
                            <span className="text-sm">{d.username}</span>
                          ) : (
                            <span className="text-muted-foreground text-sm">(anonymous)</span>
                          )}
                          {d.tenant && (
                            <div className="text-muted-foreground font-mono text-xs">{d.tenant}</div>
                          )}
                        </TableCell>
                        <TableCell>
                          {d.installed ? (
                            <Badge
                              className="bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300"
                              variant="secondary"
                            >
                              Installed
                            </Badge>
                          ) : (
                            <Badge variant="secondary">Browser</Badge>
                          )}
                        </TableCell>
                        <TableCell className="text-muted-foreground text-sm">
                          {d.platform ?? '—'}
                        </TableCell>
                        <TableCell className="text-right text-sm tabular-nums">
                          {d.launchCount}
                        </TableCell>
                        <TableCell className="text-muted-foreground text-sm whitespace-nowrap">
                          {d.installedAt ? new Date(d.installedAt).toLocaleDateString() : '—'}
                        </TableCell>
                        <TableCell className="text-muted-foreground text-xs whitespace-nowrap">
                          {new Date(d.lastSeenAt).toLocaleString()}
                        </TableCell>
                      </TableRow>
                    ))}
                </TableBody>
              </Table>
            </CardContent>
          </Card>
        </>
      )}
    </>
  );
}
