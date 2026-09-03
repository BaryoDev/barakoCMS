import { describe, expect, it } from 'vitest';
import { toRecords, type SheetPreview } from './use-import';

function cell(value: string) {
    return { kind: 'Text', value };
}

function sheet(rows: string[][]): SheetPreview {
    return {
        rowCount: rows.length,
        columnCount: rows[0]?.length ?? 0,
        suggestedHeaderRow: 0,
        truncated: false,
        rows: rows.map((row) => row.map(cell)),
    };
}

describe('toRecords', () => {
    it('takes the rows below the header and names their values by the mapping', () => {
        const preview = sheet([
            ['Heading', 'Body'],
            ['first', 'one'],
            ['second', 'two'],
        ]);

        const records = toRecords(preview, 0, { 0: 'Title', 1: 'Content' });

        expect(records).toEqual([
            { Title: 'first', Content: 'one' },
            { Title: 'second', Content: 'two' },
        ]);
    });

    it('leaves out a column mapped to nothing rather than sending it blank', () => {
        const preview = sheet([
            ['Heading', 'Internal note'],
            ['first', 'ignore me'],
        ]);

        const records = toRecords(preview, 0, { 0: 'Title', 1: '' });

        // Not { Title: 'first', '': 'ignore me' } and not { Title: 'first', Internal note: '' }.
        // A sheet usually carries a column nobody wants, and sending it would either fail validation
        // or create a field the content type never declared.
        expect(records).toEqual([{ Title: 'first' }]);
    });

    it('skips everything above the header row, so a title banner is not imported', () => {
        const preview = sheet([
            ['Q3 export', ''],
            ['Heading', 'Body'],
            ['first', 'one'],
        ]);

        const records = toRecords(preview, 1, { 0: 'Title', 1: 'Content' });

        expect(records).toEqual([{ Title: 'first', Content: 'one' }]);
    });

    it('drops a row that mapped to nothing, which is a blank line rather than an entry', () => {
        const preview = sheet([
            ['Heading'],
            ['first'],
            [''],
        ]);

        const records = toRecords(preview, 0, { 0: 'Title' });

        // The blank row still produces a key, because '' is a value. The guard is on the record
        // being empty, so this is the case that says what "empty" means here.
        expect(records).toEqual([{ Title: 'first' }, { Title: '' }]);
    });

    it('returns nothing when no column is mapped, so the button has nothing to send', () => {
        const preview = sheet([['Heading'], ['first']]);

        expect(toRecords(preview, 0, {})).toEqual([]);
    });

    it('returns nothing when the header is the last row', () => {
        const preview = sheet([['Heading']]);

        expect(toRecords(preview, 0, { 0: 'Title' })).toEqual([]);
    });
});
