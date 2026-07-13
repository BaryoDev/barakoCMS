'use client';

import { use } from 'react';
import Link from 'next/link';
import { useSchema } from '@/hooks/use-schemas';
import { FIELD_TYPES } from '@/types/schema';
import { PageHeader } from '@/components/patterns/page-header';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { EmptyState } from '@/components/patterns/empty-state';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { IconContent, IconContentTypes, IconInfo, IconPlus } from '@/components/icons';

export default function SchemaDetailPage({ params }: { params: Promise<{ name: string }> }) {
  const { name } = use(params);
  const { data: schema, isLoading } = useSchema(name);

  if (isLoading) return <TableSkeleton />;

  if (!schema) {
    return (
      <EmptyState
        icon={IconContentTypes}
        title="Content type not found"
        description={`No content type is named “${name}”. It may have been created under a different slug.`}
        action={
          <Button asChild variant="outline" size="sm">
            <Link href="/schemas">Back to content types</Link>
          </Button>
        }
      />
    );
  }

  const typeLabel = (type: string) => FIELD_TYPES.find((t) => t.value === type)?.label ?? type;

  return (
    <>
      <PageHeader
        title={schema.displayName}
        description={schema.description || `API name: ${schema.name}`}
        actions={
          <>
            <Button asChild variant="outline" size="sm">
              <Link href={`/content?type=${schema.name}`}>
                <IconContent />
                View entries
              </Link>
            </Button>
            <Button asChild size="sm">
              <Link href={`/content/new?type=${schema.name}`}>
                <IconPlus />
                New entry
              </Link>
            </Button>
          </>
        }
      />

      <div className="text-muted-foreground mb-4 flex items-start gap-2 rounded-lg border px-4 py-3 text-sm">
        <IconInfo className="mt-0.5 size-4 shrink-0" />
        <p>
          Content types are permanent: the API has no update or delete for them. To change the shape,
          create a new type and migrate entries.
        </p>
      </div>

      <div className="rounded-lg border">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Field</TableHead>
              <TableHead>API name</TableHead>
              <TableHead>Type</TableHead>
              <TableHead className="text-right">Required</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {schema.fields.map((field) => (
              <TableRow key={field.name}>
                <TableCell className="font-medium">{field.displayName}</TableCell>
                <TableCell className="text-muted-foreground font-mono text-xs">{field.name}</TableCell>
                <TableCell>
                  <Badge variant="secondary" className="font-normal">
                    {typeLabel(field.type)}
                  </Badge>
                </TableCell>
                <TableCell className="text-muted-foreground text-right text-sm">
                  {field.isRequired ? 'Yes' : '—'}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>
    </>
  );
}
