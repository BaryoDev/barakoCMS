'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useSchemas } from '@/hooks/use-schemas';
import { PageHeader } from '@/components/patterns/page-header';
import { EmptyState } from '@/components/patterns/empty-state';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { IconContentTypes, IconPlus, IconSearch } from '@/components/icons';

export default function SchemasPage() {
  const { data: schemas, isLoading } = useSchemas();
  const [search, setSearch] = useState('');

  const filtered = schemas?.filter(
    (s) =>
      s.displayName.toLowerCase().includes(search.toLowerCase()) ||
      s.name.toLowerCase().includes(search.toLowerCase())
  );

  return (
    <>
      <PageHeader
        title="Content types"
        description="The shapes your content can take. Types are permanent once created — model carefully."
        actions={
          <Button asChild size="sm">
            <Link href="/schemas/new">
              <IconPlus />
              New content type
            </Link>
          </Button>
        }
      />

      {isLoading ? (
        <TableSkeleton />
      ) : !schemas?.length ? (
        <EmptyState
          icon={IconContentTypes}
          title="No content types yet"
          description="A content type defines the fields every entry of that type will have — like Post, Product, or Event."
          action={
            <Button asChild size="sm">
              <Link href="/schemas/new">
                <IconPlus />
                New content type
              </Link>
            </Button>
          }
        />
      ) : (
        <>
          <div className="relative mb-4 max-w-sm">
            <IconSearch className="text-muted-foreground absolute top-1/2 left-3 size-3.5 -translate-y-1/2" />
            <Input
              placeholder="Filter content types…"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              className="pl-9"
            />
          </div>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
            {filtered?.map((schema) => (
              <Link
                key={schema.name}
                href={`/schemas/${schema.name}`}
                className="group hover:border-ring/40 rounded-lg border p-4 transition-colors"
              >
                <div className="flex items-center gap-2.5">
                  <IconContentTypes className="text-primary size-4 shrink-0" />
                  <span className="truncate text-sm font-medium">{schema.displayName}</span>
                </div>
                <p className="text-muted-foreground mt-1.5 font-mono text-xs">{schema.name}</p>
                <p className="text-muted-foreground mt-2 text-xs">
                  {schema.fields.length} {schema.fields.length === 1 ? 'field' : 'fields'}
                  {schema.description ? ` · ${schema.description}` : ''}
                </p>
              </Link>
            ))}
            {filtered?.length === 0 && (
              <p className="text-muted-foreground col-span-full py-8 text-center text-sm">
                No content type matches “{search}”.
              </p>
            )}
          </div>
        </>
      )}
    </>
  );
}
