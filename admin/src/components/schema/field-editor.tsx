'use client';

import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from '@/components/ui/select';
import {
    Dialog,
    DialogContent,
    DialogDescription,
    DialogFooter,
    DialogHeader,
    DialogTitle,
} from '@/components/ui/dialog';
import { Badge } from '@/components/ui/badge';
import { EmptyState } from '@/components/patterns/empty-state';
import {
    IconChevronDown,
    IconContentTypes,
    IconPen,
    IconPlus,
    IconTrash,
} from '@/components/icons';
import {
    FIELD_MASKS,
    FIELD_TYPES,
    FieldMask,
    SensitivityLevel,
    type FieldDefinition,
    type FieldType,
} from '@/types/schema';
import { SENSITIVITY_META } from '@/types/content';

interface FieldEditorProps {
    fields: FieldDefinition[];
    onChange: (fields: FieldDefinition[]) => void;
}

// The backend requires PascalCase field names (FieldTypeValidator).
function toPascalCase(input: string): string {
    return input
        .replace(/[^A-Za-z0-9\s_-]/g, '')
        .split(/[\s_-]+/)
        .filter(Boolean)
        .map((word) => word[0].toUpperCase() + word.slice(1))
        .join('');
}

const PASCAL_CASE = /^[A-Z][A-Za-z0-9]*$/;

const EMPTY_FIELD: FieldDefinition = {
    name: '',
    displayName: '',
    type: 'string',
    isRequired: false,
    sensitivity: SensitivityLevel.Public,
    visibleToRoles: [],
    mask: FieldMask.Default,
};

export function FieldEditor({ fields, onChange }: FieldEditorProps) {
    const [isDialogOpen, setIsDialogOpen] = useState(false);
    const [editingIndex, setEditingIndex] = useState<number | null>(null);
    const [form, setForm] = useState<FieldDefinition>(EMPTY_FIELD);

    const nameIsValid = PASCAL_CASE.test(form.name);
    const nameIsDuplicate = fields.some(
        (f, i) => f.name === form.name && i !== editingIndex
    );
    const canSave = form.name && form.displayName && nameIsValid && !nameIsDuplicate;

    const openNew = () => {
        setForm(EMPTY_FIELD);
        setEditingIndex(null);
        setIsDialogOpen(true);
    };

    const openEdit = (index: number) => {
        setForm(fields[index]);
        setEditingIndex(index);
        setIsDialogOpen(true);
    };

    const save = () => {
        if (!canSave) return;
        const next = [...fields];
        if (editingIndex !== null) next[editingIndex] = form;
        else next.push(form);
        onChange(next);
        setIsDialogOpen(false);
    };

    const remove = (index: number) => {
        onChange(fields.filter((_, i) => i !== index));
    };

    const move = (index: number, delta: -1 | 1) => {
        const target = index + delta;
        if (target < 0 || target >= fields.length) return;
        const next = [...fields];
        [next[index], next[target]] = [next[target], next[index]];
        onChange(next);
    };

    const typeLabel = (type: FieldType) => FIELD_TYPES.find((t) => t.value === type)?.label ?? type;

    return (
        <div className="space-y-3">
            <div className="flex items-center justify-between">
                <h3 className="text-sm font-medium">Fields</h3>
                <Button type="button" variant="outline" size="sm" onClick={openNew}>
                    <IconPlus />
                    Add field
                </Button>
            </div>

            {fields.length === 0 ? (
                <EmptyState
                    icon={IconContentTypes}
                    title="No fields yet"
                    description="Every entry of this type will have the fields you define here."
                    action={
                        <Button type="button" variant="outline" size="sm" onClick={openNew}>
                            <IconPlus />
                            Add field
                        </Button>
                    }
                />
            ) : (
                <ul className="divide-y rounded-lg border">
                    {fields.map((field, index) => (
                        <li key={field.name} className="flex items-center gap-3 px-4 py-3">
                            <div className="flex flex-col">
                                <button
                                    type="button"
                                    onClick={() => move(index, -1)}
                                    disabled={index === 0}
                                    aria-label={`Move ${field.displayName} up`}
                                    className="text-muted-foreground hover:text-foreground disabled:opacity-30"
                                >
                                    <IconChevronDown className="size-3 rotate-180" />
                                </button>
                                <button
                                    type="button"
                                    onClick={() => move(index, 1)}
                                    disabled={index === fields.length - 1}
                                    aria-label={`Move ${field.displayName} down`}
                                    className="text-muted-foreground hover:text-foreground disabled:opacity-30"
                                >
                                    <IconChevronDown className="size-3" />
                                </button>
                            </div>
                            <div className="min-w-0 flex-1">
                                <div className="flex items-center gap-2">
                                    <span className="truncate text-sm font-medium">{field.displayName}</span>
                                    {field.isRequired && (
                                        <Badge variant="secondary" className="text-xs">
                                            Required
                                        </Badge>
                                    )}
                                    {field.sensitivity !== undefined && field.sensitivity !== SensitivityLevel.Public && (
                                        <Badge variant="outline" className="text-xs">
                                            {SENSITIVITY_META[field.sensitivity].label}
                                        </Badge>
                                    )}
                                </div>
                                <p className="text-muted-foreground text-xs">
                                    <code className="font-mono">{field.name}</code> · {typeLabel(field.type)}
                                </p>
                            </div>
                            <Button
                                type="button"
                                variant="ghost"
                                size="icon"
                                onClick={() => openEdit(index)}
                                aria-label={`Edit ${field.displayName}`}
                            >
                                <IconPen className="size-3.5" />
                            </Button>
                            <Button
                                type="button"
                                variant="ghost"
                                size="icon"
                                onClick={() => remove(index)}
                                aria-label={`Remove ${field.displayName}`}
                                className="text-destructive hover:text-destructive"
                            >
                                <IconTrash className="size-3.5" />
                            </Button>
                        </li>
                    ))}
                </ul>
            )}

            <Dialog open={isDialogOpen} onOpenChange={setIsDialogOpen}>
                <DialogContent>
                    <DialogHeader>
                        <DialogTitle>{editingIndex !== null ? 'Edit field' : 'Add field'}</DialogTitle>
                        <DialogDescription>
                            The field name is how the API stores the value; the display name is what editors see.
                        </DialogDescription>
                    </DialogHeader>
                    <div className="space-y-4 py-2">
                        <div className="space-y-2">
                            <Label htmlFor="field-display-name">Display name</Label>
                            <Input
                                id="field-display-name"
                                value={form.displayName}
                                placeholder="Publish date"
                                onChange={(e) =>
                                    setForm((f) => ({
                                        ...f,
                                        displayName: e.target.value,
                                        // Keep the API name in sync until the user edits it directly.
                                        name:
                                            editingIndex === null && (f.name === '' || f.name === toPascalCase(f.displayName))
                                                ? toPascalCase(e.target.value)
                                                : f.name,
                                    }))
                                }
                            />
                        </div>
                        <div className="space-y-2">
                            <Label htmlFor="field-name">Field name (API)</Label>
                            <Input
                                id="field-name"
                                value={form.name}
                                placeholder="PublishDate"
                                className="font-mono"
                                onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
                            />
                            {form.name && !nameIsValid && (
                                <p className="text-destructive text-xs">
                                    Use PascalCase — start with a capital letter, letters and numbers only.
                                </p>
                            )}
                            {nameIsDuplicate && (
                                <p className="text-destructive text-xs">A field with this name already exists.</p>
                            )}
                        </div>
                        <div className="space-y-2">
                            <Label>Type</Label>
                            <Select
                                value={form.type}
                                onValueChange={(value) => setForm((f) => ({ ...f, type: value as FieldType }))}
                            >
                                <SelectTrigger className="w-full">
                                    <SelectValue />
                                </SelectTrigger>
                                <SelectContent>
                                    {FIELD_TYPES.map((type) => (
                                        <SelectItem key={type.value} value={type.value}>
                                            <span className="font-medium">{type.label}</span>
                                            <span className="text-muted-foreground ml-1.5 text-xs">{type.description}</span>
                                        </SelectItem>
                                    ))}
                                </SelectContent>
                            </Select>
                        </div>
                        <div className="flex items-center justify-between rounded-lg border px-4 py-3">
                            <Label htmlFor="field-required">Required</Label>
                            <Switch
                                id="field-required"
                                checked={form.isRequired}
                                onCheckedChange={(checked) => setForm((f) => ({ ...f, isRequired: checked }))}
                            />
                        </div>

                        <div className="space-y-2">
                            <Label>Sensitivity</Label>
                            <Select
                                value={String(form.sensitivity ?? SensitivityLevel.Public)}
                                onValueChange={(value) =>
                                    setForm((f) => ({ ...f, sensitivity: Number(value) as SensitivityLevel }))
                                }
                            >
                                <SelectTrigger className="w-full">
                                    <SelectValue />
                                </SelectTrigger>
                                <SelectContent>
                                    {[SensitivityLevel.Public, SensitivityLevel.Sensitive, SensitivityLevel.Hidden].map(
                                        (level) => (
                                            <SelectItem key={level} value={String(level)}>
                                                <span className="font-medium">{SENSITIVITY_META[level].label}</span>
                                                <span className="text-muted-foreground ml-1.5 text-xs">
                                                    {SENSITIVITY_META[level].description}
                                                </span>
                                            </SelectItem>
                                        )
                                    )}
                                </SelectContent>
                            </Select>
                        </div>

                        {form.sensitivity !== undefined && form.sensitivity !== SensitivityLevel.Public && (
                            <>
                                <div className="space-y-2">
                                    <Label htmlFor="field-roles">Visible to roles</Label>
                                    <Input
                                        id="field-roles"
                                        value={(form.visibleToRoles ?? []).join(', ')}
                                        placeholder="Admin, HR"
                                        onChange={(e) =>
                                            setForm((f) => ({
                                                ...f,
                                                visibleToRoles: e.target.value
                                                    .split(',')
                                                    .map((r) => r.trim())
                                                    .filter(Boolean),
                                            }))
                                        }
                                    />
                                    <p className="text-muted-foreground text-xs">
                                        Comma-separated. SuperAdmin always sees every field. Leave empty for the default
                                        (Sensitive → HR; Hidden → SuperAdmin only).
                                    </p>
                                </div>
                                <div className="space-y-2">
                                    <Label>Mask</Label>
                                    <Select
                                        value={String(form.mask ?? FieldMask.Default)}
                                        onValueChange={(value) =>
                                            setForm((f) => ({ ...f, mask: Number(value) as FieldMask }))
                                        }
                                    >
                                        <SelectTrigger className="w-full">
                                            <SelectValue />
                                        </SelectTrigger>
                                        <SelectContent>
                                            {FIELD_MASKS.map((m) => (
                                                <SelectItem key={m.value} value={String(m.value)}>
                                                    {m.label}
                                                </SelectItem>
                                            ))}
                                        </SelectContent>
                                    </Select>
                                </div>
                            </>
                        )}
                    </div>
                    <DialogFooter>
                        <Button type="button" variant="outline" onClick={() => setIsDialogOpen(false)}>
                            Cancel
                        </Button>
                        <Button type="button" onClick={save} disabled={!canSave}>
                            {editingIndex !== null ? 'Save field' : 'Add field'}
                        </Button>
                    </DialogFooter>
                </DialogContent>
            </Dialog>
        </div>
    );
}