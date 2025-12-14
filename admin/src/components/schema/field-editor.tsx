'use client';

import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from '@/components/ui/dialog';
import { FIELD_TYPES, type FieldDefinition } from '@/types/schema';

interface FieldEditorProps {
    fields: FieldDefinition[];
    onChange: (fields: FieldDefinition[]) => void;
}

export function FieldEditor({ fields, onChange }: FieldEditorProps) {
    const [isDialogOpen, setIsDialogOpen] = useState(false);
    const [editingIndex, setEditingIndex] = useState<number | null>(null);
    const [fieldForm, setFieldForm] = useState<FieldDefinition>({
        name: '',
        displayName: '',
        type: 'text',
        isRequired: false,
    });

    const resetForm = () => {
        setFieldForm({
            name: '',
            displayName: '',
            type: 'text',
            isRequired: false,
        });
        setEditingIndex(null);
    };

    const handleSaveField = () => {
        if (!fieldForm.name || !fieldForm.displayName) return;

        const newFields = [...fields];
        if (editingIndex !== null) {
            newFields[editingIndex] = fieldForm;
        } else {
            newFields.push(fieldForm);
        }
        onChange(newFields);
        setIsDialogOpen(false);
        resetForm();
    };

    const handleEditField = (index: number) => {
        setFieldForm(fields[index]);
        setEditingIndex(index);
        setIsDialogOpen(true);
    };

    const handleDeleteField = (index: number) => {
        const newFields = fields.filter((_, i) => i !== index);
        onChange(newFields);
    };

    const moveField = (index: number, direction: 'up' | 'down') => {
        const newFields = [...fields];
        const targetIndex = direction === 'up' ? index - 1 : index + 1;
        if (targetIndex < 0 || targetIndex >= newFields.length) return;
        [newFields[index], newFields[targetIndex]] = [newFields[targetIndex], newFields[index]];
        onChange(newFields);
    };

    const getFieldTypeInfo = (type: string) => {
        return FIELD_TYPES.find(t => t.value === type) || FIELD_TYPES[0];
    };

    return (
        <div className="space-y-4">
            <div className="flex items-center justify-between">
                <h3 className="text-lg font-semibold text-white">Fields</h3>
                <Dialog open={isDialogOpen} onOpenChange={(open) => { setIsDialogOpen(open); if (!open) resetForm(); }}>
                    <DialogTrigger asChild>
                        <Button variant="outline" className="border-amber-500/50 text-amber-400 hover:bg-amber-500/10">
                            + Add Field
                        </Button>
                    </DialogTrigger>
                    <DialogContent className="bg-slate-900 border-slate-700">
                        <DialogHeader>
                            <DialogTitle className="text-white">{editingIndex !== null ? 'Edit Field' : 'Add Field'}</DialogTitle>
                            <DialogDescription className="text-slate-400">
                                Configure the properties of this field.
                            </DialogDescription>
                        </DialogHeader>
                        <div className="space-y-4 py-4">
                            <div className="grid grid-cols-2 gap-4">
                                <div className="space-y-2">
                                    <Label htmlFor="field-name" className="text-slate-200">Field Name (slug)</Label>
                                    <Input
                                        id="field-name"
                                        value={fieldForm.name}
                                        onChange={(e) => setFieldForm({ ...fieldForm, name: e.target.value.toLowerCase().replace(/\s+/g, '_') })}
                                        placeholder="title"
                                        className="bg-slate-800 border-slate-700 text-white"
                                    />
                                </div>
                                <div className="space-y-2">
                                    <Label htmlFor="field-displayName" className="text-slate-200">Display Name</Label>
                                    <Input
                                        id="field-displayName"
                                        value={fieldForm.displayName}
                                        onChange={(e) => setFieldForm({ ...fieldForm, displayName: e.target.value })}
                                        placeholder="Title"
                                        className="bg-slate-800 border-slate-700 text-white"
                                    />
                                </div>
                            </div>
                            <div className="space-y-2">
                                <Label className="text-slate-200">Field Type</Label>
                                <Select value={fieldForm.type} onValueChange={(value) => setFieldForm({ ...fieldForm, type: value as FieldDefinition['type'] })}>
                                    <SelectTrigger className="bg-slate-800 border-slate-700 text-white">
                                        <SelectValue />
                                    </SelectTrigger>
                                    <SelectContent className="bg-slate-800 border-slate-700">
                                        {FIELD_TYPES.map((type) => (
                                            <SelectItem key={type.value} value={type.value} className="text-white hover:bg-slate-700">
                                                <span className="flex items-center gap-2">
                                                    <span>{type.icon}</span>
                                                    <span>{type.label}</span>
                                                </span>
                                            </SelectItem>
                                        ))}
                                    </SelectContent>
                                </Select>
                            </div>
                            <div className="flex items-center gap-2">
                                <input
                                    type="checkbox"
                                    id="isRequired"
                                    checked={fieldForm.isRequired}
                                    onChange={(e) => setFieldForm({ ...fieldForm, isRequired: e.target.checked })}
                                    className="rounded border-slate-600 bg-slate-800 text-amber-500"
                                />
                                <Label htmlFor="isRequired" className="text-slate-200">Required field</Label>
                            </div>
                        </div>
                        <DialogFooter>
                            <Button variant="outline" onClick={() => setIsDialogOpen(false)} className="border-slate-700 text-slate-300">
                                Cancel
                            </Button>
                            <Button onClick={handleSaveField} className="bg-amber-500 hover:bg-amber-600 text-white">
                                {editingIndex !== null ? 'Update' : 'Add'} Field
                            </Button>
                        </DialogFooter>
                    </DialogContent>
                </Dialog>
            </div>

            {fields.length === 0 ? (
                <Card className="bg-slate-800/30 border-slate-700 border-dashed">
                    <CardContent className="py-8 text-center">
                        <p className="text-slate-500">No fields defined. Click &quot;Add Field&quot; to get started.</p>
                    </CardContent>
                </Card>
            ) : (
                <div className="space-y-2">
                    {fields.map((field, index) => {
                        const typeInfo = getFieldTypeInfo(field.type);
                        return (
                            <Card key={index} className="bg-slate-800/50 border-slate-700 hover:border-slate-600 transition-colors">
                                <CardContent className="py-3 px-4 flex items-center justify-between">
                                    <div className="flex items-center gap-4">
                                        <div className="flex flex-col gap-1">
                                            <button
                                                onClick={() => moveField(index, 'up')}
                                                disabled={index === 0}
                                                className="text-slate-500 hover:text-white disabled:opacity-30 disabled:cursor-not-allowed"
                                            >
                                                ▲
                                            </button>
                                            <button
                                                onClick={() => moveField(index, 'down')}
                                                disabled={index === fields.length - 1}
                                                className="text-slate-500 hover:text-white disabled:opacity-30 disabled:cursor-not-allowed"
                                            >
                                                ▼
                                            </button>
                                        </div>
                                        <div className="w-10 h-10 bg-slate-700/50 rounded-lg flex items-center justify-center">
                                            <span className="text-lg">{typeInfo.icon}</span>
                                        </div>
                                        <div>
                                            <div className="flex items-center gap-2">
                                                <span className="text-white font-medium">{field.displayName}</span>
                                                {field.isRequired && (
                                                    <Badge variant="outline" className="border-red-500/50 text-red-400 bg-red-500/10 text-xs">
                                                        Required
                                                    </Badge>
                                                )}
                                            </div>
                                            <div className="flex items-center gap-2 text-sm text-slate-400">
                                                <code className="text-xs bg-slate-700/50 px-1.5 py-0.5 rounded">{field.name}</code>
                                                <span>•</span>
                                                <span>{typeInfo.label}</span>
                                            </div>
                                        </div>
                                    </div>
                                    <div className="flex items-center gap-2">
                                        <Button variant="ghost" size="sm" onClick={() => handleEditField(index)} className="text-slate-400 hover:text-white">
                                            Edit
                                        </Button>
                                        <Button variant="ghost" size="sm" onClick={() => handleDeleteField(index)} className="text-red-400 hover:text-red-300">
                                            Delete
                                        </Button>
                                    </div>
                                </CardContent>
                            </Card>
                        );
                    })}
                </div>
            )}
        </div>
    );
}
