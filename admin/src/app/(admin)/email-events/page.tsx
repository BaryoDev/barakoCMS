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
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { useEmailEvents } from '@/hooks/use-email-events';

const TYPES = [
  { value: 'all', label: 'All events' },
  { value: 'bounced', label: 'Bounced' },
  { value: 'complained', label: 'Complained' },
  { value: 'delivery_delayed', label: 'Delayed' },
];

function typeTone(type: string): { label: string; cls: string } {
  switch (type) {
    case 'bounced':
      return { label: 'Bounced', cls: 'bg-rose-100 text-rose-700 dark:bg-rose-950 dark:text-rose-300' };
    case 'complained':
      return { label: 'Complained', cls: 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300' };
    case 'delivery_delayed':
      return { label: 'Delayed', cls: 'bg-sky-100 text-sky-700 dark:bg-sky-950 dark:text-sky-300' };
    default:
      return { label: type || '—', cls: '' };
  }
}

export default function EmailEventsPage() {
  const [type, setType] = useState('all');
  const { data, isLoading, isError } = useEmailEvents(type === 'all' ? undefined : type);

  return (
    <>
      <PageHeader
        title="Email events"
        description="Delivery problems Resend reported — bounces, complaints and delays. Newest first."
        actions={
          <Select value={type} onValueChange={setType}>
            <SelectTrigger size="sm" className="w-40">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {TYPES.map((t) => (
                <SelectItem key={t.value} value={t.value}>
                  {t.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        }
      />

      {isError ? (
        <Card>
          <CardHeader>
            <CardTitle className="text-sm font-medium">Email module not available</CardTitle>
          </CardHeader>
          <CardContent className="text-muted-foreground text-sm">
            No email-events endpoint responded. This needs the Resend email module (BarakoCMS.Email.Resend) installed and configured.
          </CardContent>
        </Card>
      ) : (
        <Card>
          <CardContent className="p-0">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Recipient</TableHead>
                  <TableHead>Type</TableHead>
                  <TableHead>Reason</TableHead>
                  <TableHead className="text-right">When</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {isLoading &&
                  Array.from({ length: 6 }).map((_, i) => (
                    <TableRow key={i}>
                      <TableCell colSpan={4}>
                        <Skeleton className="h-5 w-full" />
                      </TableCell>
                    </TableRow>
                  ))}
                {!isLoading && data && data.length === 0 && (
                  <TableRow>
                    <TableCell colSpan={4} className="text-muted-foreground py-8 text-center text-sm">
                      No email problems recorded. 🎉
                    </TableCell>
                  </TableRow>
                )}
                {!isLoading &&
                  data?.map((e) => {
                    const t = typeTone(e.type);
                    return (
                      <TableRow key={e.id}>
                        <TableCell className="font-mono text-xs">{e.email}</TableCell>
                        <TableCell>
                          <Badge className={t.cls} variant="secondary">
                            {t.label}
                          </Badge>
                        </TableCell>
                        <TableCell className="text-muted-foreground max-w-md truncate text-sm">
                          {e.reason || '—'}
                        </TableCell>
                        <TableCell className="text-muted-foreground text-right text-xs whitespace-nowrap">
                          {new Date(e.at).toLocaleString()}
                        </TableCell>
                      </TableRow>
                    );
                  })}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      )}
    </>
  );
}
