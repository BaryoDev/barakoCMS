'use client';

import { useMutation } from '@tanstack/react-query';
import { api } from '@/lib/api';

/** One parsed cell. `kind` is the type the parser recognised, not the type of the target field. */
export interface PreviewCell {
    kind: string;
    value: string;
}

export interface SheetPreview {
    rowCount: number;
    columnCount: number;
    /** First row with two or more non-blank cells, or -1 when the parser could not pick one. */
    suggestedHeaderRow: number;
    /** True when the sheet is longer than the preview, so `rows` is not all of it. */
    truncated: boolean;
    rows: PreviewCell[][];
}

export interface BulkCreateReport {
    created: number;
    failed: number;
    errors: { row: number; messages: string[] }[];
}

/**
 * Parses an upload into a preview grid. Nothing is stored.
 */
export function useAnalyzeSheet() {
    return useMutation({
        mutationFn: async (file: File) => {
            const form = new FormData();
            form.append('file', file);
            const response = await api.post<SheetPreview>('/api/import/analyze', form);
            return response.data;
        },
    });
}

export function useBulkCreate() {
    return useMutation({
        mutationFn: async (body: {
            contentType: string;
            records: Record<string, unknown>[];
            continueOnError: boolean;
            status: string;
        }) => {
            const response = await api.post<BulkCreateReport>('/api/import/content', body);
            return response.data;
        },
    });
}

/**
 * Turns the preview grid into records, using the header row for names and the mapping for targets.
 *
 * A column mapped to the empty string is left out rather than sent as a blank field: a spreadsheet
 * usually carries a column nobody wants, and sending it would either fail validation or create a
 * field the content type never declared.
 */
export function toRecords(
    preview: SheetPreview,
    headerRow: number,
    mapping: Record<number, string>,
): Record<string, unknown>[] {
    const records: Record<string, unknown>[] = [];

    for (let r = headerRow + 1; r < preview.rows.length; r += 1) {
        const row = preview.rows[r];
        const record: Record<string, unknown> = {};

        for (let c = 0; c < row.length; c += 1) {
            const field = mapping[c];
            if (!field) continue;
            record[field] = row[c].value;
        }

        // A row that mapped to nothing is a blank line in the sheet, not an entry to create.
        if (Object.keys(record).length > 0) records.push(record);
    }

    return records;
}
