'use client';

import { Button } from '@/components/ui/button';
import { IconChevronLeft, IconChevronRight } from '@/components/icons';
import type { Paginated } from '@/lib/api';

interface PaginationControlsProps {
  page: Paginated<unknown>;
  onPageChange: (page: number) => void;
}

export function PaginationControls({ page, onPageChange }: PaginationControlsProps) {
  if (page.totalPages <= 1) return null;

  const start = (page.page - 1) * page.pageSize + 1;
  const end = Math.min(page.page * page.pageSize, page.totalItems);

  return (
    <div className="flex items-center justify-between gap-4 pt-4">
      {/* Counts are machine-produced, so they are mono and tabular: the range stops shifting width
          as the page changes. The words around them are not. */}
      <p className="text-muted-foreground text-[13px]">
        <span className="text-secondary-foreground font-mono font-bold tabular-nums">
          {start} to {end}
        </span>{' '}
        of <span className="font-mono tabular-nums">{page.totalItems}</span>
      </p>
      <div className="flex items-center gap-2">
        <Button
          variant="outline"
          size="sm"
          disabled={!page.hasPreviousPage}
          onClick={() => onPageChange(page.page - 1)}
        >
          <IconChevronLeft className="size-3" />
          Previous
        </Button>
        <Button
          variant="outline"
          size="sm"
          disabled={!page.hasNextPage}
          onClick={() => onPageChange(page.page + 1)}
        >
          Next
          <IconChevronRight className="size-3" />
        </Button>
      </div>
    </div>
  );
}
