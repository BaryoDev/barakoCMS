'use client';

import { useState } from 'react';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
import { Textarea } from '@/components/ui/textarea';
import { cn } from '@/lib/utils';
import type { FieldDefinition } from '@/types/schema';

interface DynamicFormProps {
    fields: FieldDefinition[];
    values: Record<string, unknown>;
    onChange: (values: Record<string, unknown>) => void;
    errors?: Record<string, string>;
}

// Renders a form control per field type. The accepted set is defined server-side
// in FieldTypeRegistry; each type here maps to a sensible input (native pickers
// for dates/times, typed inputs for email/url, a JSON editor for structured data).
export function DynamicForm({ fields, values, onChange, errors }: DynamicFormProps) {
    const setField = (name: string, value: unknown) => {
        onChange({ ...values, [name]: value });
    };

    if (fields.length === 0) {
        return (
            <p className="text-muted-foreground py-8 text-center text-sm">
                This content type has no fields yet. Add fields to its definition first.
            </p>
        );
    }

    return (
        <div className="space-y-5">
            {fields.map((field) => (
                <FieldControl
                    key={field.name}
                    field={field}
                    value={values[field.name]}
                    error={errors?.[field.name]}
                    onChange={(v) => setField(field.name, v)}
                />
            ))}
        </div>
    );
}

function FieldControl({
    field,
    value,
    error,
    onChange,
}: {
    field: FieldDefinition;
    value: unknown;
    error?: string;
    onChange: (value: unknown) => void;
}) {
    const label = (
        <Label htmlFor={field.name}>
            {field.displayName}
            {field.isRequired && <span className="text-destructive ml-0.5">*</span>}
        </Label>
    );

    switch (field.type) {
        case 'bool':
            return (
                <div className="flex items-center justify-between gap-4 rounded-lg border px-4 py-3">
                    {label}
                    <Switch
                        id={field.name}
                        checked={Boolean(value)}
                        onCheckedChange={onChange}
                    />
                </div>
            );

        case 'int':
        case 'decimal':
        case 'money':
            return (
                <div className="space-y-2">
                    {label}
                    <Input
                        id={field.name}
                        type="number"
                        step={field.type === 'int' ? 1 : 'any'}
                        inputMode={field.type === 'int' ? 'numeric' : 'decimal'}
                        value={value === null || value === undefined ? '' : String(value)}
                        onChange={(e) => {
                            const raw = e.target.value;
                            if (raw === '') return onChange(null);
                            onChange(field.type === 'int' ? parseInt(raw, 10) : parseFloat(raw));
                        }}
                        className="w-fit"
                    />
                    <FieldError message={error} />
                </div>
            );

        // Native pickers for the temporal types.
        case 'date':
        case 'datetime':
        case 'time':
            return (
                <div className="space-y-2">
                    {label}
                    <Input
                        id={field.name}
                        type={field.type === 'datetime' ? 'datetime-local' : field.type}
                        value={(value as string) || ''}
                        onChange={(e) => onChange(e.target.value)}
                        className="w-fit"
                    />
                    <FieldError message={error} />
                </div>
            );

        // Single-line inputs. email/url get the matching native keyboard + hint;
        // format is enforced server-side by FieldTypeRegistry.
        case 'email':
        case 'url':
        case 'slug':
        case 'uuid':
            return (
                <div className="space-y-2">
                    {label}
                    <Input
                        id={field.name}
                        type={field.type === 'email' ? 'email' : field.type === 'url' ? 'url' : 'text'}
                        inputMode={field.type === 'email' ? 'email' : field.type === 'url' ? 'url' : 'text'}
                        placeholder={PLACEHOLDERS[field.type]}
                        value={(value as string) || ''}
                        onChange={(e) => onChange(e.target.value)}
                        className={field.type === 'uuid' || field.type === 'slug' ? 'font-mono' : undefined}
                    />
                    <FieldError message={error} />
                </div>
            );

        // Longer free text. No rich editor bundled yet — a roomy textarea; the
        // value is stored/served as-is (HTML for richtext, Markdown for markdown).
        case 'richtext':
        case 'markdown':
            return (
                <div className="space-y-2">
                    {label}
                    <Textarea
                        id={field.name}
                        rows={6}
                        value={(value as string) || ''}
                        onChange={(e) => onChange(e.target.value)}
                        className={field.type === 'markdown' ? 'font-mono text-sm' : undefined}
                    />
                    <FieldError message={error} />
                </div>
            );

        case 'json':
        case 'array':
        case 'object':
            return (
                <JsonField
                    field={field}
                    label={label}
                    value={value}
                    error={error}
                    onChange={onChange}
                />
            );

        case 'string':
        default:
            return (
                <div className="space-y-2">
                    {label}
                    <Textarea
                        id={field.name}
                        rows={2}
                        value={(value as string) || ''}
                        onChange={(e) => onChange(e.target.value)}
                    />
                    <FieldError message={error} />
                </div>
            );
    }
}

const PLACEHOLDERS: Partial<Record<FieldDefinition['type'], string>> = {
    email: 'name@example.com',
    url: 'https://example.com',
    slug: 'my-post-title',
    uuid: '00000000-0000-0000-0000-000000000000',
};

function JsonField({
    field,
    label,
    value,
    error,
    onChange,
}: {
    field: FieldDefinition;
    label: React.ReactNode;
    value: unknown;
    error?: string;
    onChange: (value: unknown) => void;
}) {
    const [text, setText] = useState(() =>
        value === undefined || value === null
            ? field.type === 'array'
                ? '[]'
                : '{}'
            : JSON.stringify(value, null, 2)
    );
    const [parseError, setParseError] = useState<string | null>(null);

    return (
        <div className="space-y-2">
            {label}
            <Textarea
                id={field.name}
                rows={4}
                spellCheck={false}
                value={text}
                onChange={(e) => {
                    setText(e.target.value);
                    try {
                        const parsed = JSON.parse(e.target.value);
                        setParseError(null);
                        onChange(parsed);
                    } catch {
                        setParseError('Not valid JSON yet — the field keeps its last valid value until this parses.');
                    }
                }}
                className={cn('font-mono text-xs', parseError && 'border-warning')}
            />
            <p className="text-muted-foreground text-xs">
                {field.type === 'array' ? 'JSON list, e.g. ["one", "two"]' : 'JSON object, e.g. {"key": "value"}'}
            </p>
            <FieldError message={parseError ?? error} />
        </div>
    );
}

function FieldError({ message }: { message?: string | null }) {
    if (!message) return null;
    return <p className="text-destructive text-xs">{message}</p>;
}
