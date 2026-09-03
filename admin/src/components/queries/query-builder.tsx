'use client';

import { useMemo, useState } from 'react';
import { toast } from 'sonner';
import {
    DEFAULT_LIMIT,
    FILTER_OPS,
    MAX_FILTERS,
    MAX_LIMIT,
    hasUnsavedEdits,
    projectableFields,
    refusalFor,
    slugify,
    useSaveQuery,
    type FilterOp,
    type QueryDefinition,
    type QueryFilter,
    type SaveQueryInput,
} from '@/hooks/use-queries';
import { apiErrorMessage } from '@/lib/api';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Checkbox } from '@/components/ui/checkbox';
import { Switch } from '@/components/ui/switch';
import { IconPlay, IconPlus, IconTimes, IconTrash, IconWarning } from '@/components/icons';
import type { ContentTypeDefinition } from '@/types/schema';

const CARD = 'bg-card rounded-xl border p-6 shadow-[var(--shadow-card)]';

const SELECT =
    'h-9 rounded-md border border-input bg-transparent px-3 text-[13px] shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50';

interface QueryBuilderProps {
    /** The stored copy, or undefined for a query being created. */
    saved?: QueryDefinition;
    contentTypes: ContentTypeDefinition[];
    /** Called with the slug after a save lands, so the parent can open the stored copy. */
    onSaved: (slug: string) => void;
    /** Run the stored definition and show its rows. */
    onPreview: (slug: string) => void;
    previewing: boolean;
    onClose: () => void;
}

export function QueryBuilder({
    saved,
    contentTypes,
    onSaved,
    onPreview,
    previewing,
    onClose,
}: QueryBuilderProps) {
    const save = useSaveQuery();

    const [name, setName] = useState(saved?.name ?? '');
    const [slug, setSlug] = useState(saved?.slug ?? '');
    const [slugEdited, setSlugEdited] = useState(false);
    const [contentType, setContentType] = useState(saved?.contentType ?? '');
    const [filters, setFilters] = useState<QueryFilter[]>(saved?.filters ?? []);
    const [sortField, setSortField] = useState(saved?.sortField ?? '');
    const [descending, setDescending] = useState(saved?.descending ?? false);
    // Held as text so the box can be emptied while typing. An empty box parses to NaN, which the
    // draft's own check refuses, rather than silently becoming 0 or the default.
    const [limit, setLimit] = useState(String(saved?.limit ?? DEFAULT_LIMIT));
    const [fields, setFields] = useState<string[]>(saved?.fields ?? []);

    const schema = contentTypes.find((type) => type.name === contentType);
    const allowed = useMemo(() => projectableFields(schema), [schema]);

    const draft: SaveQueryInput = useMemo(
        () => ({
            name,
            slug,
            contentType,
            filters,
            sortField: sortField || null,
            descending,
            limit: Number.parseInt(limit, 10),
            fields,
        }),
        [name, slug, contentType, filters, sortField, descending, limit, fields],
    );

    const refusal = refusalFor(draft, allowed);
    const unsaved = hasUnsavedEdits(draft, saved);

    function onNameChange(value: string) {
        setName(value);
        // The slug follows the name only while creating and only until the operator types one. On a
        // stored query it is the identity, and the field is read only for that reason.
        if (!saved && !slugEdited) setSlug(slugify(value));
    }

    function onContentTypeChange(value: string) {
        setContentType(value);
        // The old filters, sort and projection named fields on a different type. Keeping them would
        // send names the new type has never declared, and the server would refuse the save with a
        // message about a field the form is no longer showing.
        setFilters([]);
        setSortField('');
        setFields([]);
    }

    function updateFilter(index: number, patch: Partial<QueryFilter>) {
        setFilters((current) =>
            current.map((filter, i) => (i === index ? { ...filter, ...patch } : filter)),
        );
    }

    function toggleField(field: string, checked: boolean) {
        // Appended in the order they are picked, because that order is the column order of both the
        // preview and the payload a workflow sends.
        setFields((current) =>
            checked ? [...current.filter((f) => f !== field), field] : current.filter((f) => f !== field),
        );
    }

    async function run(thenPreview: boolean) {
        if (refusal) return;

        let slugToPreview = saved?.slug;

        if (unsaved) {
            try {
                const result = await save.mutateAsync(draft);
                slugToPreview = result.slug;
                onSaved(result.slug);
                toast.success(`Saved "${result.name}".`);
            } catch (error) {
                toast.error(apiErrorMessage(error, 'Could not save the query.'));
                return;
            }
        }

        if (thenPreview && slugToPreview) onPreview(slugToPreview);
    }

    return (
        <section className={CARD}>
            <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                    <h2 className="text-[15px] font-bold">{saved ? saved.name : 'New query'}</h2>
                    <p className="text-muted-foreground mt-1 text-[13px]">
                        A content type, typed filters, a sort, a limit and the fields that leave. There is no
                        expression anywhere in it, on purpose.
                    </p>
                </div>
                <Button type="button" variant="ghost" size="icon" aria-label="Close the editor" onClick={onClose}>
                    <IconTimes className="size-4" />
                </Button>
            </div>

            <div className="mt-5 grid gap-4 sm:grid-cols-2">
                <div className="space-y-1.5">
                    <Label htmlFor="query-name">Name</Label>
                    <Input
                        id="query-name"
                        value={name}
                        placeholder="Active newsletter subscribers"
                        onChange={(e) => onNameChange(e.target.value)}
                    />
                </div>

                <div className="space-y-1.5">
                    <Label htmlFor="query-slug">Slug</Label>
                    <Input
                        id="query-slug"
                        value={slug}
                        readOnly={Boolean(saved)}
                        className="font-mono text-[13px]"
                        placeholder="active-newsletter-subscribers"
                        onChange={(e) => {
                            setSlugEdited(true);
                            setSlug(e.target.value);
                        }}
                    />
                    <p className="text-muted-foreground text-[12px]">
                        {saved
                            ? 'What a workflow action references. It cannot be changed, because changing it would leave the old query in place and create a second one.'
                            : 'What a workflow action will reference. Lowercase letters, digits and hyphens.'}
                    </p>
                </div>
            </div>

            <div className="mt-4 space-y-1.5">
                <Label htmlFor="query-content-type">Content type</Label>
                <select
                    id="query-content-type"
                    className={SELECT}
                    value={contentType}
                    onChange={(e) => onContentTypeChange(e.target.value)}
                >
                    <option value="">Choose a type</option>
                    {contentTypes.map((type) => (
                        <option key={type.name} value={type.name}>
                            {type.displayName || type.name}
                        </option>
                    ))}
                </select>
            </div>

            {contentType && allowed.length === 0 && (
                <p className="mt-4 flex items-start gap-2 text-[13px]">
                    <IconWarning className="mt-0.5 size-4 shrink-0" />
                    <span>
                        {contentType} has no fields marked Public, so there is nothing a query may filter on or
                        return. A query filtering on a field its rows cannot show would be a way to read that
                        field without ever printing it, which is why the server refuses it too.
                    </span>
                </p>
            )}

            {contentType && allowed.length > 0 && (
                <>
                    <fieldset className="mt-6">
                        <legend className="text-[13px] font-bold">Filters</legend>
                        <p className="text-muted-foreground mt-1 text-[13px]">
                            Every filter has to match. At most {MAX_FILTERS}, and only on a Public field.
                        </p>

                        <div className="mt-3 space-y-2">
                            {filters.map((filter, index) => (
                                // Position is the identity here: two filters can be identical, and keying on
                                // the contents would make React reuse the wrong row as one is edited.
                                <div key={index} className="flex flex-wrap items-center gap-2">
                                    <select
                                        className={SELECT}
                                        aria-label={`Field for filter ${index + 1}`}
                                        value={filter.field}
                                        onChange={(e) => updateFilter(index, { field: e.target.value })}
                                    >
                                        <option value="">Choose a field</option>
                                        {allowed.map((field) => (
                                            <option key={field.name} value={field.name}>
                                                {field.displayName || field.name}
                                            </option>
                                        ))}
                                    </select>

                                    <select
                                        className={SELECT}
                                        aria-label={`Operator for filter ${index + 1}`}
                                        value={filter.op}
                                        onChange={(e) =>
                                            updateFilter(index, { op: e.target.value as FilterOp })
                                        }
                                    >
                                        {FILTER_OPS.map((op) => (
                                            <option key={op.value} value={op.value}>
                                                {op.label}
                                            </option>
                                        ))}
                                    </select>

                                    <Input
                                        className="w-48 text-[13px]"
                                        aria-label={`Value for filter ${index + 1}`}
                                        value={filter.value}
                                        placeholder={
                                            allowed.find((f) => f.name === filter.field)?.type ?? 'value'
                                        }
                                        onChange={(e) => updateFilter(index, { value: e.target.value })}
                                    />

                                    <Button
                                        type="button"
                                        variant="ghost"
                                        size="icon"
                                        aria-label={`Remove filter ${index + 1}`}
                                        className="text-destructive hover:text-destructive"
                                        onClick={() => setFilters((c) => c.filter((_, i) => i !== index))}
                                    >
                                        <IconTrash className="size-3.5" />
                                    </Button>
                                </div>
                            ))}
                        </div>

                        <Button
                            type="button"
                            variant="outline"
                            size="sm"
                            className="mt-3"
                            disabled={filters.length >= MAX_FILTERS}
                            onClick={() =>
                                setFilters((current) => [...current, { field: '', op: 'eq', value: '' }])
                            }
                        >
                            <IconPlus className="size-3.5" />
                            Add a filter
                        </Button>
                    </fieldset>

                    <div className="mt-6 grid gap-4 sm:grid-cols-2">
                        <div className="space-y-1.5">
                            <Label htmlFor="query-sort">Sort by</Label>
                            <select
                                id="query-sort"
                                className={SELECT}
                                value={sortField ?? ''}
                                onChange={(e) => {
                                    setSortField(e.target.value);
                                    // Clearing the sort clears its direction too. Leaving a stale
                                    // "descending" on a query that no longer sorts would show as an
                                    // unsaved edit nothing on screen accounts for.
                                    if (!e.target.value) setDescending(false);
                                }}
                            >
                                <option value="">No sort</option>
                                {allowed.map((field) => (
                                    <option key={field.name} value={field.name}>
                                        {field.displayName || field.name}
                                    </option>
                                ))}
                            </select>
                            <div className="flex items-center gap-2 pt-1">
                                <Switch
                                    id="query-descending"
                                    checked={descending}
                                    disabled={!sortField}
                                    onCheckedChange={setDescending}
                                />
                                <Label htmlFor="query-descending" className="text-[13px] font-normal">
                                    Highest or latest first
                                </Label>
                            </div>
                        </div>

                        <div className="space-y-1.5">
                            <Label htmlFor="query-limit">Limit</Label>
                            <Input
                                id="query-limit"
                                type="number"
                                min={1}
                                max={MAX_LIMIT}
                                className="w-32"
                                value={limit}
                                onChange={(e) => setLimit(e.target.value)}
                            />
                            <p className="text-muted-foreground text-[12px]">
                                At most {MAX_LIMIT}. A workflow action with no bound is a way to email everyone
                                twice.
                            </p>
                        </div>
                    </div>

                    <fieldset className="mt-6">
                        <legend className="text-[13px] font-bold">Fields to return</legend>
                        <p className="text-muted-foreground mt-1 text-[13px]">
                            The only fields that leave, in the order you pick them. Nothing defaults to all of
                            them, so a field added to this type next year does not start appearing in payloads
                            nobody revisited.
                        </p>

                        <div className="mt-3 grid gap-2 sm:grid-cols-2">
                            {allowed.map((field) => {
                                const position = fields.indexOf(field.name);
                                return (
                                    <label
                                        key={field.name}
                                        htmlFor={`query-field-${field.name}`}
                                        className="flex items-start gap-2.5"
                                    >
                                        <Checkbox
                                            id={`query-field-${field.name}`}
                                            checked={position >= 0}
                                            onCheckedChange={(checked) =>
                                                toggleField(field.name, checked === true)
                                            }
                                        />
                                        <span className="text-[13px] leading-tight">
                                            <span className="font-medium">{field.displayName || field.name}</span>
                                            <span className="text-muted-foreground ml-1.5 font-mono text-[12px]">
                                                {field.name}
                                            </span>
                                            {position >= 0 && (
                                                <span className="text-muted-foreground ml-1.5 tabular-nums">
                                                    column {position + 1}
                                                </span>
                                            )}
                                        </span>
                                    </label>
                                );
                            })}
                        </div>
                    </fieldset>
                </>
            )}

            {refusal && (
                <p className="text-warning mt-6 flex items-start gap-2 text-[13px]" role="status">
                    <IconWarning aria-hidden className="mt-0.5 size-4 shrink-0" />
                    <span>{refusal}</span>
                </p>
            )}

            <div className="mt-6 flex flex-wrap items-center gap-2">
                <Button type="button" disabled={Boolean(refusal) || save.isPending || previewing} onClick={() => void run(true)}>
                    <IconPlay className="size-3.5" />
                    {save.isPending ? 'Saving...' : unsaved ? 'Save and preview' : 'Preview'}
                </Button>

                <Button
                    type="button"
                    variant="outline"
                    disabled={Boolean(refusal) || !unsaved || save.isPending}
                    onClick={() => void run(false)}
                >
                    Save
                </Button>

                {unsaved && saved && (
                    <span className="text-muted-foreground text-[12.5px]">
                        A preview runs the saved query, so these edits are saved first.
                    </span>
                )}
            </div>
        </section>
    );
}
