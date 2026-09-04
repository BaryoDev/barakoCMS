'use client';

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, type Paginated } from '@/lib/api';
import { SensitivityLevel, type ContentTypeDefinition, type FieldDefinition } from '@/types/schema';

/**
 * The operators the server accepts. Mirrors the `Ops` table in QueryRunner, and it is the whole set:
 * a query definition holds no expressions, so there is nothing else to offer.
 */
export type FilterOp = 'eq' | 'ne' | 'lt' | 'lte' | 'gt' | 'gte' | 'contains';

export const FILTER_OPS: { value: FilterOp; label: string }[] = [
    { value: 'eq', label: 'is' },
    { value: 'ne', label: 'is not' },
    { value: 'lt', label: 'is less than' },
    { value: 'lte', label: 'is at most' },
    { value: 'gt', label: 'is greater than' },
    { value: 'gte', label: 'is at least' },
    { value: 'contains', label: 'contains' },
];

/** One typed comparison. Never an expression. Mirrors Models/QueryDefinition.cs. */
export interface QueryFilter {
    field: string;
    op: FilterOp;
    value: string;
}

export interface QueryDefinition {
    id: string;
    name: string;
    slug: string;
    contentType: string;
    filters: QueryFilter[];
    sortField?: string | null;
    descending: boolean;
    limit: number;
    /** The only fields that leave. An allowlist the operator names, not a default. */
    fields: string[];
    createdAt: string;
    updatedAt: string;
}

/** What POST /api/queries takes. It upserts on slug, so this is both create and update. */
export interface SaveQueryInput {
    name: string;
    slug: string;
    contentType: string;
    filters: QueryFilter[];
    sortField?: string | null;
    descending: boolean;
    limit: number;
    fields: string[];
}

export interface QueryPreview {
    ok: boolean;
    /** Why the query did not run. Present when `ok` is false, and the row list is then empty. */
    refusal?: string | null;
    count: number;
    rows: Record<string, unknown>[];
}

/** Ceilings the server enforces. Copied from QueryDefinition and QueryRunner. */
export const MAX_LIMIT = 1000;
export const DEFAULT_LIMIT = 100;
export const MAX_FILTERS = 10;

/** The largest page the list endpoint will return, from PaginatedRequest.MaxPageSize. */
const LIST_PAGE_SIZE = 100;

export function useQueryDefinitions() {
    return useQuery({
        queryKey: ['queries'],
        queryFn: async () => {
            const response = await api.get<Paginated<QueryDefinition>>('/api/queries', {
                params: { pageSize: LIST_PAGE_SIZE },
            });
            // The envelope is kept rather than unwrapped: the page is capped at 100, and a tenant
            // with more queries than that needs to be told the list is partial instead of quietly
            // shown the first hundred as if they were all of them.
            return response.data;
        },
    });
}

/**
 * One saved query, read from the server rather than picked out of the cached list.
 *
 * The editor works from this because the list is a capped page and can be stale, and saving an
 * editor seeded from a stale row would write back whatever it was holding.
 */
export function useQueryDefinition(slug: string | null) {
    return useQuery({
        queryKey: ['queries', slug],
        queryFn: async () =>
            (await api.get<QueryDefinition>(`/api/queries/${encodeURIComponent(slug!)}`)).data,
        enabled: Boolean(slug),
    });
}

export function useSaveQuery() {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async (input: SaveQueryInput) =>
            (await api.post<QueryDefinition>('/api/queries', input)).data,
        onSuccess: (saved) => {
            queryClient.invalidateQueries({ queryKey: ['queries'] });
            queryClient.setQueryData(['queries', saved.slug], saved);
            // The rows in the cache came from the definition that was there before this save, and
            // the panel showing them says they are what the query returns right now. Without this
            // the screen answers that with the pre-edit rows and makes no request to find out,
            // because a preview is held with staleTime Infinity.
            queryClient.invalidateQueries({ queryKey: ['query-preview', saved.slug] });
        },
    });
}

export function useDeleteQuery() {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async (slug: string) => {
            await api.delete(`/api/queries/${encodeURIComponent(slug)}`);
        },
        onSuccess: (_result, slug) => {
            // Prefix matching, so this covers the deleted query's own detail entry too.
            queryClient.invalidateQueries({ queryKey: ['queries'] });
            // Removed rather than invalidated: the query is gone, so these rows belong to nothing.
            // A slug is free to be taken again, and the new query would otherwise open on the old
            // one's rows.
            queryClient.removeQueries({ queryKey: ['query-preview', slug] });
        },
    });
}

/**
 * Runs the stored definition and returns its rows.
 *
 * A POST, because running a query is an operation rather than a resource, but modelled as a query
 * here because it changes nothing: it reads the saved definition and returns rows. That buys the
 * cache, so the rows survive the editor remounting around them, and `refetch` is what the Preview
 * button presses on a second run.
 *
 * It reads the *saved* copy, which is why the screen saves before it can show the effect of an edit.
 */
export function useQueryPreview(slug: string | null) {
    return useQuery({
        queryKey: ['query-preview', slug],
        queryFn: async () =>
            (
                await api.post<QueryPreview>(
                    `/api/queries/${encodeURIComponent(slug!)}/preview`,
                    {},
                )
            ).data,
        enabled: Boolean(slug),
        // A preview is a snapshot the operator asked for at a moment. Refetching it when the window
        // regains focus would swap the rows under them with no press of the button.
        refetchOnWindowFocus: false,
        staleTime: Infinity,
    });
}

/**
 * The server's slug rule, character for character: QueryGate.IsSlug.
 *
 * Checked here so a bad slug is a message under the field rather than a 400 after a round trip. The
 * server still decides; this only saves the trip.
 */
export function isQuerySlug(value: string): boolean {
    return /^[a-z0-9][a-z0-9-]{0,62}$/.test(value);
}

/** Turns a name into a slug the server will accept, for the create form's suggestion. */
export function slugify(name: string): string {
    return name
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, '-')
        .replace(/^-+|-+$/g, '')
        .slice(0, 63)
        // The slice can cut mid-word and leave a trailing hyphen, so the trim runs again after it.
        .replace(/-+$/g, '');
}

/**
 * The fields a query may filter on, sort by and return.
 *
 * Public only, matching QueryRunner. It is not a display convenience: filtering on a field the
 * result cannot show is an oracle, so offering a Sensitive field here would offer something the
 * server is going to refuse anyway. An absent sensitivity is Public, which is the model's default.
 */
export function projectableFields(schema: ContentTypeDefinition | undefined): FieldDefinition[] {
    if (!schema) return [];
    return schema.fields.filter(
        (field) => (field.sensitivity ?? SensitivityLevel.Public) === SensitivityLevel.Public,
    );
}

/**
 * Why this draft cannot be saved, or null.
 *
 * A pre-flight copy of QueryRunner.ValidateAsync's rules, limited to the ones this screen can check
 * without the server: it knows the schema it drew the form from, so it can say "name a field" before
 * the POST. It is not the gate. The server re-validates on save and again on every run, because a
 * field that was Public when a query was written can be raised to Sensitive afterwards.
 */
export function refusalFor(draft: SaveQueryInput, allowed: FieldDefinition[]): string | null {
    const names = new Set(allowed.map((field) => field.name.toLowerCase()));

    if (!draft.name.trim()) return 'Name is required.';
    if (!isQuerySlug(draft.slug)) {
        return 'Slug must start with a letter or digit and hold only lowercase letters, digits and hyphens.';
    }
    if (!draft.contentType) return 'Choose a content type.';

    if (draft.filters.length > MAX_FILTERS) {
        return `Too many filters. At most ${MAX_FILTERS} are allowed.`;
    }
    if (draft.filters.some((filter) => !filter.field)) return 'Every filter needs a field.';

    const unknownFilter = draft.filters.find((filter) => !names.has(filter.field.toLowerCase()));
    if (unknownFilter) return `'${unknownFilter.field}' cannot be filtered on '${draft.contentType}'.`;

    if (draft.sortField && !names.has(draft.sortField.toLowerCase())) {
        return `'${draft.sortField}' cannot be sorted on '${draft.contentType}'.`;
    }

    if (draft.fields.length === 0) {
        return 'Choose at least one field to return. A query with no projection would send whatever the schema grows.';
    }

    const unknownField = draft.fields.find((field) => !names.has(field.toLowerCase()));
    if (unknownField) return `'${unknownField}' cannot be returned from '${draft.contentType}'.`;

    if (!Number.isInteger(draft.limit) || draft.limit < 1 || draft.limit > MAX_LIMIT) {
        return `Limit must be a whole number between 1 and ${MAX_LIMIT}.`;
    }

    return null;
}

/**
 * Whether the editor holds edits the saved copy does not have.
 *
 * The preview runs the stored definition, so an operator who changes a filter and presses Preview
 * would otherwise see the old rows and believe they were the new ones.
 */
export function hasUnsavedEdits(draft: SaveQueryInput, saved: QueryDefinition | undefined): boolean {
    if (!saved) return true;

    const same =
        draft.name.trim() === saved.name &&
        draft.slug === saved.slug &&
        draft.contentType === saved.contentType &&
        (draft.sortField ?? '') === (saved.sortField ?? '') &&
        draft.descending === saved.descending &&
        draft.limit === saved.limit &&
        draft.fields.length === saved.fields.length &&
        draft.fields.every((field, i) => field === saved.fields[i]) &&
        draft.filters.length === saved.filters.length &&
        draft.filters.every(
            (filter, i) =>
                filter.field === saved.filters[i].field &&
                filter.op === saved.filters[i].op &&
                filter.value === saved.filters[i].value,
        );

    return !same;
}

/**
 * The columns a preview table shows, and the key each one reads from a row.
 *
 * Ordered by the projection rather than by whatever the first row happened to carry, so the table
 * matches the field list the operator built. The key is looked up case-insensitively because the
 * runner projects under the schema's spelling while a record can hold "price" under a field named
 * "Price", and a column keyed on the wrong case renders every cell blank.
 *
 * With no projection to go on, the row keys stand in, in the order they were first seen. That is the
 * case where the definition is not to hand (its list page was capped, say) and a table of rows is
 * still better than nothing.
 */
export function previewColumns(
    fields: string[],
    rows: Record<string, unknown>[],
): { label: string; key: string }[] {
    const keys = new Map<string, string>();
    for (const row of rows) {
        for (const key of Object.keys(row)) {
            if (!keys.has(key.toLowerCase())) keys.set(key.toLowerCase(), key);
        }
    }

    if (fields.length === 0) {
        return [...keys.values()].map((key) => ({ label: key, key }));
    }

    return fields.map((field) => ({
        label: field,
        key: keys.get(field.toLowerCase()) ?? field,
    }));
}

/**
 * One cell of a preview row.
 *
 * A projected value is whatever the entry stored, so it can be a number, a boolean, a list or an
 * object. An empty string is returned for a missing value rather than "undefined", because a blank
 * cell is the honest rendering of a field this row does not carry.
 */
export function cellText(value: unknown): string {
    if (value === null || value === undefined) return '';
    if (typeof value === 'string') return value;
    if (typeof value === 'number' || typeof value === 'boolean') return String(value);
    return JSON.stringify(value);
}
