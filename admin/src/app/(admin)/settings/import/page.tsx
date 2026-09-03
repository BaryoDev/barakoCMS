'use client';

import { useMemo, useRef, useState } from 'react';
import { toast } from 'sonner';
import {
    toRecords,
    useAnalyzeSheet,
    useBulkCreate,
    type BulkCreateReport,
    type SheetPreview,
} from '@/hooks/use-import';
import { useSchemas } from '@/hooks/use-schemas';
import { apiErrorMessage } from '@/lib/api';
import { PageHeader } from '@/components/patterns/page-header';
import { Button } from '@/components/ui/button';
import { IconTable, IconWarning } from '@/components/icons';

const CARD = 'bg-card rounded-xl border p-6 shadow-[var(--shadow-card)]';

/** The mapping value that means "do not import this column". */
const SKIP = '';

function ReportSummary({ report }: { report: BulkCreateReport }) {
    return (
        <div className="mt-4 rounded-lg border p-4">
            <p className="text-[13px] font-bold">
                {report.failed === 0
                    ? `Imported ${report.created} ${report.created === 1 ? 'entry' : 'entries'}`
                    : `Imported ${report.created}, refused ${report.failed}`}
            </p>

            {report.errors.length > 0 && (
                <>
                    <p className="text-muted-foreground mt-3 text-[13px]">
                        A refused row is named by its position in the sheet, counting the header, so you can
                        find it without guessing.
                    </p>
                    <ul className="mt-2 space-y-1 text-[13px]">
                        {report.errors.map((error) => (
                            <li key={error.row} className="flex gap-2">
                                <span className="font-mono tabular-nums font-bold">Row {error.row}</span>
                                <span className="text-muted-foreground">{error.messages.join('; ')}</span>
                            </li>
                        ))}
                    </ul>
                </>
            )}
        </div>
    );
}

export default function ImportPage() {
    const fileInput = useRef<HTMLInputElement>(null);
    const [preview, setPreview] = useState<SheetPreview | null>(null);
    const [headerRow, setHeaderRow] = useState(0);
    const [contentType, setContentType] = useState('');
    const [mapping, setMapping] = useState<Record<number, string>>({});
    const [report, setReport] = useState<BulkCreateReport | null>(null);

    const schemas = useSchemas();
    const analyze = useAnalyzeSheet();
    const bulkCreate = useBulkCreate();

    const fields = useMemo(
        () => schemas.data?.find((s) => s.name === contentType)?.fields ?? [],
        [schemas.data, contentType],
    );

    const headers = preview?.rows[headerRow] ?? [];

    // The rows that would actually be created, computed the same way the import will compute them,
    // so the count under the button is the count the server gets rather than a guess from rowCount.
    const records = useMemo(
        () => (preview ? toRecords(preview, headerRow, mapping) : []),
        [preview, headerRow, mapping],
    );

    const mapped = Object.values(mapping).filter((field) => field !== SKIP).length;

    async function onFile(file: File) {
        setReport(null);
        try {
            const result = await analyze.mutateAsync(file);
            setPreview(result);
            setHeaderRow(result.suggestedHeaderRow >= 0 ? result.suggestedHeaderRow : 0);
            setMapping({});
        } catch (error) {
            toast.error(apiErrorMessage(error));
        }
    }

    async function onImport() {
        if (!preview) return;

        try {
            const result = await bulkCreate.mutateAsync({
                contentType,
                records,
                // Every row is attempted and the refusals are reported, rather than the first bad row
                // ending the import and leaving the editor to guess how much of the sheet landed.
                continueOnError: true,
                status: 'Draft',
            });
            setReport(result);
            toast.success(`Imported ${result.created} of ${records.length}`);
        } catch (error) {
            toast.error(apiErrorMessage(error));
        }
    }

    return (
        <div className="space-y-6">
            <PageHeader
                title="Import a spreadsheet"
                description="Turn an .xlsx or CSV into entries. Nothing is stored until you import."
            />

            <section className={CARD}>
                <h2 className="text-[15px] font-bold">1. Choose a file</h2>
                <p className="text-muted-foreground mt-1 text-[13px]">
                    Parsed here and shown back to you. A large sheet is previewed in part and imported in
                    full.
                </p>

                <input
                    ref={fileInput}
                    type="file"
                    accept=".xlsx,.csv,text/csv"
                    aria-label="Spreadsheet to import"
                    className="sr-only"
                    onChange={(e) => {
                        const file = e.target.files?.[0];
                        if (file) void onFile(file);
                        e.target.value = '';
                    }}
                />

                <Button
                    className="mt-4"
                    variant="outline"
                    disabled={analyze.isPending}
                    onClick={() => fileInput.current?.click()}
                >
                    <IconTable className="size-4" />
                    {analyze.isPending ? 'Reading...' : 'Choose a file'}
                </Button>
            </section>

            {preview && (
                <>
                    <section className={CARD}>
                        <h2 className="text-[15px] font-bold">2. Say which row holds the headings</h2>
                        <p className="text-muted-foreground mt-1 text-[13px]">
                            {preview.rowCount} {preview.rowCount === 1 ? 'row' : 'rows'}, {preview.columnCount}{' '}
                            {preview.columnCount === 1 ? 'column' : 'columns'}.
                            {preview.truncated && ' Only the first part is previewed.'}
                        </p>

                        <div className="mt-4 overflow-x-auto">
                            <table className="w-full text-[13px]">
                                <tbody>
                                    {preview.rows.slice(0, 8).map((row, index) => (
                                        <tr key={index} className={index === headerRow ? 'bg-accent' : undefined}>
                                            <td className="w-16 py-1 pr-3 text-right">
                                                {/* A radio rather than a click on the row: the choice is
                                                    one row out of several, and a row that only responds
                                                    to a mouse cannot be chosen from the keyboard. */}
                                                <span className="flex items-center justify-end gap-2">
                                                    <input
                                                        type="radio"
                                                        name="headerRow"
                                                        aria-label={`Use row ${index + 1} as the headings`}
                                                        checked={index === headerRow}
                                                        onChange={() => setHeaderRow(index)}
                                                    />
                                                    <span className="text-muted-foreground font-mono tabular-nums">
                                                        {index + 1}
                                                    </span>
                                                </span>
                                            </td>
                                            {row.map((cell, column) => (
                                                <td key={column} className="max-w-40 truncate py-1 pr-4">
                                                    {cell.value}
                                                </td>
                                            ))}
                                        </tr>
                                    ))}
                                </tbody>
                            </table>
                        </div>
                    </section>

                    <section className={CARD}>
                        <h2 className="text-[15px] font-bold">3. Match the columns to fields</h2>

                        <label className="mt-4 block text-[13px] font-bold" htmlFor="contentType">
                            Content type
                        </label>
                        <select
                            id="contentType"
                            className="mt-1 rounded-md border px-3 py-2 text-[13px]"
                            value={contentType}
                            onChange={(e) => {
                                setContentType(e.target.value);
                                // The old mapping named fields on a different type, so keeping it would
                                // send field names the new type has never heard of.
                                setMapping({});
                            }}
                        >
                            <option value="">Choose a type</option>
                            {schemas.data?.map((schema) => (
                                <option key={schema.name} value={schema.name}>
                                    {schema.displayName || schema.name}
                                </option>
                            ))}
                        </select>

                        {contentType && (
                            <div className="mt-4 space-y-2">
                                {headers.map((cell, column) => (
                                    <div key={column} className="flex items-center gap-3">
                                        <span className="w-40 truncate text-[13px] font-bold">
                                            {cell.value || `Column ${column + 1}`}
                                        </span>
                                        <select
                                            className="rounded-md border px-3 py-1.5 text-[13px]"
                                            aria-label={`Field for ${cell.value || `column ${column + 1}`}`}
                                            value={mapping[column] ?? SKIP}
                                            onChange={(e) =>
                                                setMapping((current) => ({ ...current, [column]: e.target.value }))
                                            }
                                        >
                                            <option value={SKIP}>Do not import</option>
                                            {fields.map((field) => (
                                                <option key={field.name} value={field.name}>
                                                    {field.displayName || field.name}
                                                </option>
                                            ))}
                                        </select>
                                    </div>
                                ))}
                            </div>
                        )}
                    </section>

                    <section className={CARD}>
                        <h2 className="text-[15px] font-bold">4. Import</h2>
                        <p className="text-muted-foreground mt-1 text-[13px]">
                            Entries are created as drafts, so nothing an import gets wrong is published.
                        </p>

                        {mapped === 0 && contentType && (
                            <p className="mt-3 flex items-start gap-2 text-[13px]">
                                <IconWarning className="mt-0.5 size-4 shrink-0" />
                                <span>
                                    No column is matched to a field yet, so an import would create empty entries.
                                </span>
                            </p>
                        )}

                        <Button
                            className="mt-4"
                            disabled={!contentType || mapped === 0 || records.length === 0 || bulkCreate.isPending}
                            onClick={() => void onImport()}
                        >
                            {bulkCreate.isPending
                                ? 'Importing...'
                                : `Import ${records.length} ${records.length === 1 ? 'entry' : 'entries'}`}
                        </Button>

                        {report && <ReportSummary report={report} />}
                    </section>
                </>
            )}
        </div>
    );
}
