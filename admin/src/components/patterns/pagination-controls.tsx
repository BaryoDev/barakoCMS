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
      <p className="text-muted-foreground text-sm">
        {start}–{end} of {page.totalItems}
      </p>
      <div className="flex items-center gap-2">
        <Button
          variant="outline"
          size="sm"
          disabled={!page.hasPreviousPage}
          onClick={() => onPageChange(page.page - 1)}
        >
          <IconChevronLeft className="size-3.5" />
          Previous
        </Button>
        <Button
          variant="outline"
          size="sm"
          disabled={!page.hasNextPage}
          onClick={() => onPageChange(page.page + 1)}
        >
          Next
          <IconChevronRight className="size-3.5" />
        </Button>
      </div>
    </div>
  );
}
