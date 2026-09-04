'use client';

import { useState } from 'react';
import { toast } from 'sonner';
import {
    cellText,
    previewColumns,
    useDeleteQuery,
    useQueryDefinition,
    useQueryDefinitions,
    useQueryPreview,
    type QueryDefinition,
    type QueryPreview,
} from '@/hooks/use-queries';
import { useSchemas } from '@/hooks/use-schemas';
import { apiErrorMessage } from '@/lib/api';
import { QueryBuilder } from '@/components/queries/query-builder';
import { PageHeader } from '@/components/patterns/page-header';
import { EmptyState } from '@/components/patterns/empty-state';
import { ErrorState } from '@/components/patterns/error-state';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { ConfirmDialog } from '@/components/patterns/confirm-dialog';
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
import { IconFilter, IconPlus, IconRefresh, IconTrash, IconWarning } from '@/components/icons';

const CARD = 'bg-card rounded-xl border p-6 shadow-[var(--shadow-card)]';

function PreviewPanel({
    slug,
    fields,
    preview,
    onRerun,
}: {
    slug: string;
    fields: string[];
    preview: { data?: QueryPreview; isFetching: boolean; isError: boolean };
    onRerun: () => void;
}) {
    const result = preview.data;
    const rows = result?.rows ?? [];
    const columns = previewColumns(fields, rows);

    return (
        <section className={CARD}>
            <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                    <h2 className="text-[15px] font-bold">Preview</h2>
                    <p className="text-muted-foreground mt-1 text-[13px]">
                        The rows <span className="font-mono">{slug}</span> returns right now, with only the
                        fields it projects. This is what a workflow action carrying it would send.
                    </p>
                </div>
                <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    disabled={preview.isFetching}
                    onClick={onRerun}
                >
                    <IconRefresh className="size-3.5" />
                    {preview.isFetching ? 'Running...' : 'Run again'}
                </Button>
            </div>

            {preview.isError ? (
                <div className="mt-4">
                    <ErrorState entity="the preview" onRetry={onRerun} />
                </div>
            ) : preview.isFetching && !result ? (
                <div className="mt-4">
                    <TableSkeleton />
                </div>
            ) : !result ? null : !result.ok ? (
                <p className="text-warning mt-4 flex items-start gap-2 text-[13px]" role="alert">
                    <IconWarning aria-hidden className="mt-0.5 size-4 shrink-0" />
                    {/* The server's own words. It re-checks the definition on every run, so a field
                        that was Public when this query was written and is Sensitive now shows up
                        here rather than in a payload. */}
                    <span>{result.refusal ?? 'The query would not run.'}</span>
                </p>
            ) : rows.length === 0 ? (
                <p className="text-muted-foreground mt-4 text-[13px]">
                    No rows match. That is an answer, not a failure: a workflow action carrying this query
                    would send nothing.
                </p>
            ) : (
                <>
                    <p className="text-muted-foreground mt-4 text-[13px]">
                        {result.count} {result.count === 1 ? 'row' : 'rows'}.
                    </p>
                    <div className="mt-3 overflow-x-auto rounded-lg border">
                        <Table>
                            <TableHeader>
                                <TableRow>
                                    <TableHead className="w-12 text-right">#</TableHead>
                                    {columns.map((column) => (
                                        <TableHead key={column.label}>{column.label}</TableHead>
                                    ))}
                                </TableRow>
                            </TableHeader>
                            <TableBody>
                                {rows.map((row, index) => (
                                    // Position is the only identity a projected row has: it carries the
                                    // fields the query named and nothing else, so there may be no id in it.
                                    <TableRow key={index}>
                                        <TableCell className="text-muted-foreground text-right font-mono text-xs tabular-nums">
                                            {index + 1}
                                        </TableCell>
                                        {columns.map((column) => (
                                            <TableCell key={column.label} className="max-w-64 truncate text-[13px]">
                                                {cellText(row[column.key])}
                                            </TableCell>
                                        ))}
                                    </TableRow>
                                ))}
                            </TableBody>
                        </Table>
                    </div>
                </>
            )}
        </section>
    );
}

export default function QueriesPage() {
    const list = useQueryDefinitions();
    const schemas = useSchemas();
    const remove = useDeleteQuery();

    const [editing, setEditing] = useState<string | null>(null);
    const [creating, setCreating] = useState(false);
    const [previewSlug, setPreviewSlug] = useState<string | null>(null);

    const detail = useQueryDefinition(editing);
    const preview = useQueryPreview(previewSlug);

    const items = list.data?.items ?? [];
    const capped = (list.data?.totalItems ?? 0) > items.length;

    // The projection of whichever query is previewed, so the columns follow the field order the
    // operator built rather than the key order of the first row.
    const previewFields =
        (detail.data?.slug === previewSlug ? detail.data.fields : undefined) ??
        items.find((item) => item.slug === previewSlug)?.fields ??
        [];

    function open(slug: string) {
        setCreating(false);
        setEditing(slug);
    }

    async function runPreview(slug: string) {
        if (previewSlug === slug) {
            // Same slug, so the cached rows are already on screen and setting state would change
            // nothing. Refetch is what actually re-runs it.
            await preview.refetch();
            return;
        }
        setPreviewSlug(slug);
    }

    async function onDelete(query: QueryDefinition) {
        try {
            await remove.mutateAsync(query.slug);
            if (editing === query.slug) setEditing(null);
            if (previewSlug === query.slug) setPreviewSlug(null);
            toast.success(`Deleted "${query.name}".`);
        } catch (error) {
            toast.error(apiErrorMessage(error, 'Could not delete the query.'));
        }
    }

    const newButton = (
        <Button
            size="sm"
            onClick={() => {
                setEditing(null);
                setCreating(true);
            }}
        >
            <IconPlus />
            New query
        </Button>
    );

    return (
        <div className="space-y-6">
            <PageHeader
                title="Queries"
                description="Saved ways of fetching the rows a workflow needs beyond the entry that triggered it. A content type, typed filters, a sort, a limit and the fields that leave."
                actions={newButton}
            />

            {list.isLoading ? (
                <TableSkeleton />
            ) : list.isError && !list.data ? (
                // Only when there is nothing to show. A refetch that fails after a save or a delete
                // leaves the last good page in the cache, and an error panel in place of it would
                // take the table and its delete buttons away over a request that changed nothing.
                <ErrorState entity="queries" onRetry={() => list.refetch()} />
            ) : items.length === 0 ? (
                <EmptyState
                    icon={IconFilter}
                    title="No queries yet"
                    description="A workflow action that emails every subscriber needs something to fetch that list. Build one here and see the rows before anything sends."
                    action={newButton}
                />
            ) : (
                <div className="rounded-lg border">
                    <Table>
                        <TableHeader>
                            <TableRow>
                                <TableHead className="w-10">
                                    <span className="sr-only">Open</span>
                                </TableHead>
                                <TableHead>Name</TableHead>
                                <TableHead>Slug</TableHead>
                                <TableHead>Content type</TableHead>
                                <TableHead>Filters</TableHead>
                                <TableHead>Returns</TableHead>
                                <TableHead className="text-right">Limit</TableHead>
                                <TableHead className="w-10" />
                            </TableRow>
                        </TableHeader>
                        <TableBody>
                            {items.map((item) => (
                                <TableRow key={item.id} className={editing === item.slug ? 'bg-accent' : undefined}>
                                    <TableCell>
                                        {/* A radio rather than a click on the row. One query out of several is
                                            open at a time, and a row that only answers a mouse cannot be
                                            reached from the keyboard at all. */}
                                        <input
                                            type="radio"
                                            name="openQuery"
                                            aria-label={`Open ${item.name}`}
                                            checked={editing === item.slug}
                                            onChange={() => open(item.slug)}
                                        />
                                    </TableCell>
                                    <TableCell className="font-medium">{item.name}</TableCell>
                                    <TableCell className="text-muted-foreground font-mono text-xs">
                                        {item.slug}
                                    </TableCell>
                                    <TableCell className="text-[13px]">{item.contentType}</TableCell>
                                    <TableCell className="text-muted-foreground text-[13px] tabular-nums">
                                        {item.filters.length}
                                    </TableCell>
                                    <TableCell>
                                        <div className="flex flex-wrap gap-1">
                                            {item.fields.map((field) => (
                                                <Badge key={field} variant="secondary" className="text-xs">
                                                    {field}
                                                </Badge>
                                            ))}
                                        </div>
                                    </TableCell>
                                    <TableCell className="text-right text-[13px] tabular-nums">
                                        {item.limit}
                                    </TableCell>
                                    <TableCell>
                                        <ConfirmDialog
                                            trigger={
                                                <Button
                                                    type="button"
                                                    variant="ghost"
                                                    size="icon"
                                                    aria-label={`Delete ${item.name}`}
                                                    className="text-destructive hover:text-destructive"
                                                >
                                                    <IconTrash className="size-3.5" />
                                                </Button>
                                            }
                                            title={`Delete "${item.name}"?`}
                                            description="Any workflow action that references this slug stops finding it, and nothing warns them before the next run."
                                            confirmLabel="Delete"
                                            destructive
                                            onConfirm={() => void onDelete(item)}
                                        />
                                    </TableCell>
                                </TableRow>
                            ))}
                        </TableBody>
                    </Table>
                </div>
            )}

            {capped && (
                <p className="text-muted-foreground text-[13px]">
                    Showing {items.length} of {list.data?.totalItems}. The list endpoint returns one page, so
                    the rest are not on screen.
                </p>
            )}

            {creating && (
                <QueryBuilder
                    key="new"
                    contentTypes={schemas.data ?? []}
                    onSaved={(slug) => open(slug)}
                    onPreview={(slug) => void runPreview(slug)}
                    previewing={preview.isFetching}
                    onClose={() => setCreating(false)}
                />
            )}

            {/* Only when there is nothing to show. react-query keeps the last good copy through a
                failed refetch, and rendering on `isError` alone would stack an error panel above a
                form that is working fine. */}
            {editing && detail.isError && !detail.data && (
                <ErrorState entity="that query" onRetry={() => detail.refetch()} />
            )}

            {editing && detail.data && (
                // Keyed by slug so choosing another query remounts the form rather than merging one
                // definition's state into another's fields.
                <QueryBuilder
                    key={detail.data.slug}
                    saved={detail.data}
                    contentTypes={schemas.data ?? []}
                    onSaved={(slug) => open(slug)}
                    onPreview={(slug) => void runPreview(slug)}
                    previewing={preview.isFetching}
                    onClose={() => setEditing(null)}
                />
            )}

            {previewSlug && (
                <PreviewPanel
                    slug={previewSlug}
                    fields={previewFields}
                    preview={preview}
                    onRerun={() => void preview.refetch()}
                />
            )}
        </div>
    );
}
