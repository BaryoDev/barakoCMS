'use client';

import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import type { FieldDefinition } from '@/types/schema';

interface DynamicFormProps {
    fields: FieldDefinition[];
    values: Record<string, unknown>;
    onChange: (values: Record<string, unknown>) => void;
    errors?: Record<string, string>;
}

export function DynamicForm({ fields, values, onChange, errors }: DynamicFormProps) {
    const handleFieldChange = (fieldName: string, value: unknown) => {
        onChange({ ...values, [fieldName]: value });
    };

    const renderField = (field: FieldDefinition) => {
        const value = values[field.name];
        const error = errors?.[field.name];

        switch (field.type) {
            case 'text':
            case 'richtext':
                return (
                    <div key={field.name} className="space-y-2">
                        <Label htmlFor={field.name} className="text-slate-200">
                            {field.displayName}
                            {field.isRequired && <span className="text-red-400 ml-1">*</span>}
                        </Label>
                        {field.type === 'richtext' ? (
                            <textarea
                                id={field.name}
                                value={(value as string) || ''}
                                onChange={(e) => handleFieldChange(field.name, e.target.value)}
                                className="w-full min-h-[150px] bg-slate-800 border border-slate-700 rounded-md px-3 py-2 text-white placeholder:text-slate-500 focus:border-amber-500 focus:ring-amber-500"
                                placeholder={`Enter ${field.displayName.toLowerCase()}`}
                            />
                        ) : (
                            <Input
                                id={field.name}
                                value={(value as string) || ''}
                                onChange={(e) => handleFieldChange(field.name, e.target.value)}
                                className="bg-slate-800 border-slate-700 text-white placeholder:text-slate-500"
                                placeholder={`Enter ${field.displayName.toLowerCase()}`}
                            />
                        )}
                        {error && <p className="text-red-400 text-sm">{error}</p>}
                    </div>
                );

            case 'number':
                return (
                    <div key={field.name} className="space-y-2">
                        <Label htmlFor={field.name} className="text-slate-200">
                            {field.displayName}
                            {field.isRequired && <span className="text-red-400 ml-1">*</span>}
                        </Label>
                        <Input
                            id={field.name}
                            type="number"
                            value={(value as number) ?? ''}
                            onChange={(e) => handleFieldChange(field.name, e.target.value ? parseFloat(e.target.value) : null)}
                            className="bg-slate-800 border-slate-700 text-white placeholder:text-slate-500"
                            placeholder={`Enter ${field.displayName.toLowerCase()}`}
                        />
                        {error && <p className="text-red-400 text-sm">{error}</p>}
                    </div>
                );

            case 'boolean':
                return (
                    <div key={field.name} className="space-y-2">
                        <div className="flex items-center gap-3">
                            <input
                                id={field.name}
                                type="checkbox"
                                checked={(value as boolean) || false}
                                onChange={(e) => handleFieldChange(field.name, e.target.checked)}
                                className="rounded border-slate-600 bg-slate-800 text-amber-500 focus:ring-amber-500"
                            />
                            <Label htmlFor={field.name} className="text-slate-200">
                                {field.displayName}
                                {field.isRequired && <span className="text-red-400 ml-1">*</span>}
                            </Label>
                        </div>
                        {error && <p className="text-red-400 text-sm">{error}</p>}
                    </div>
                );

            case 'date':
                return (
                    <div key={field.name} className="space-y-2">
                        <Label htmlFor={field.name} className="text-slate-200">
                            {field.displayName}
                            {field.isRequired && <span className="text-red-400 ml-1">*</span>}
                        </Label>
                        <Input
                            id={field.name}
                            type="datetime-local"
                            value={(value as string) || ''}
                            onChange={(e) => handleFieldChange(field.name, e.target.value)}
                            className="bg-slate-800 border-slate-700 text-white"
                        />
                        {error && <p className="text-red-400 text-sm">{error}</p>}
                    </div>
                );

            case 'image':
                return (
                    <div key={field.name} className="space-y-2">
                        <Label htmlFor={field.name} className="text-slate-200">
                            {field.displayName}
                            {field.isRequired && <span className="text-red-400 ml-1">*</span>}
                        </Label>
                        <Input
                            id={field.name}
                            value={(value as string) || ''}
                            onChange={(e) => handleFieldChange(field.name, e.target.value)}
                            className="bg-slate-800 border-slate-700 text-white placeholder:text-slate-500"
                            placeholder="Enter image URL"
                        />
                        {(value as string) && (
                            <div className="mt-2 rounded-lg overflow-hidden border border-slate-700 max-w-xs">
                                <img src={value as string} alt="Preview" className="w-full h-auto" />
                            </div>
                        )}
                        {error && <p className="text-red-400 text-sm">{error}</p>}
                    </div>
                );

            default:
                return (
                    <div key={field.name} className="space-y-2">
                        <Label htmlFor={field.name} className="text-slate-200">
                            {field.displayName}
                            {field.isRequired && <span className="text-red-400 ml-1">*</span>}
                        </Label>
                        <Input
                            id={field.name}
                            value={(value as string) || ''}
                            onChange={(e) => handleFieldChange(field.name, e.target.value)}
                            className="bg-slate-800 border-slate-700 text-white placeholder:text-slate-500"
                            placeholder={`Enter ${field.displayName.toLowerCase()}`}
                        />
                        {error && <p className="text-red-400 text-sm">{error}</p>}
                    </div>
                );
        }
    };

    if (fields.length === 0) {
        return (
            <div className="text-center py-8 text-slate-500">
                No fields defined for this content type.
            </div>
        );
    }

    return (
        <div className="space-y-6">
            {fields.map(renderField)}
        </div>
    );
}
