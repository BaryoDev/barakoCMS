'use client';

import { Suspense, useState } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { useSchemas } from '@/hooks/use-schemas';
import { useContents } from '@/hooks/use-contents';
import { PageHeader } from '@/components/patterns/page-header';
import { EmptyState } from '@/components/patterns/empty-state';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { PaginationControls } from '@/components/patterns/pagination-controls';
import { Button } from '@/components/ui/button';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { IconContent, IconPlus } from '@/components/icons';
import { formatDistanceToNow } from 'date-fns';

const ALL_TYPES = 'all';

function ContentListInner() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const contentType = searchParams.get('type') ?? undefined;
  const [page, setPage] = useState(1);

  const { data: schemas } = useSchemas();
  const { data: contents, isLoading } = useContents({ page, pageSize: 20, contentType });

  const setType = (value: string) => {
    setPage(1);
    router.replace(value === ALL_TYPES ? '/content' : `/content?type=${value}`);
  };

  const entryTitle = (data: Record<string, unknown>, id: string) => {
    const first = Object.values(data).find((v) => typeof v === 'string' && v);
    return (first as string) ?? id;
  };

  return (
    <>
      <PageHeader
        title="Entries"
        description="Everything written in your CMS, filterable by content type."
        actions={
          <Button asChild size="sm">
            <Link href={contentType ? `/content/new?type=${contentType}` : '/content/new'}>
              <IconPlus />
              New entry
            </Link>
          </Button>
        }
      />

      <div className="mb-4">
        <Select value={contentType ?? ALL_TYPES} onValueChange={setType}>
          <SelectTrigger className="w-56">
            <SelectValue placeholder="All content types" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL_TYPES}>All content types</SelectItem>
            {schemas?.map((s) => (
              <SelectItem key={s.name} value={s.name}>
                {s.displayName}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>

      {isLoading ? (
        <TableSkeleton />
      ) : !contents?.items.length ? (
        <EmptyState
          icon={IconContent}
          title={contentType ? `No ${contentType} entries yet` : 'No entries yet'}
          description="Entries hold your actual content — each one follows the fields of its content type."
          action={
            <Button asChild size="sm">
              <Link href={contentType ? `/content/new?type=${contentType}` : '/content/new'}>
                <IconPlus />
                New entry
              </Link>
            </Button>
          }
        />
      ) : (
        <>
          <div className="rounded-lg border">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Entry</TableHead>
                  <TableHead>Type</TableHead>
                  <TableHead className="hidden sm:table-cell">Updated</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {contents.items.map((item) => (
                  <TableRow
                    key={item.id}
                    className="cursor-pointer"
                    onClick={() => router.push(`/content/${item.id}`)}
                  >
                    <TableCell className="max-w-md">
                      <span className="block truncate font-medium">{entryTitle(item.data, item.id)}</span>
                    </TableCell>
                    <TableCell className="text-muted-foreground font-mono text-xs">{item.contentType}</TableCell>
                    <TableCell className="text-muted-foreground hidden text-sm sm:table-cell">
                      {formatDistanceToNow(new Date(item.updatedAt), { addSuffix: true })}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
          <PaginationControls page={contents} onPageChange={setPage} />
        </>
      )}
    </>
  );
}

export default function ContentListPage() {
  return (
    <Suspense fallback={<TableSkeleton />}>
      <ContentListInner />
    </Suspense>
  );
}
