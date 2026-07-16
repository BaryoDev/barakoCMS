// Types for Content Type Schema management.
// Field types mirror the backend's enforced set (Core/Validation/FieldTypeValidator.cs).

import { SensitivityLevel } from './content';

export { SensitivityLevel };

export interface FieldDefinition {
    name: string; // PascalCase enforced by the backend
    displayName: string;
    type: FieldType;
    isRequired: boolean;
    defaultValue?: unknown;
    validationRules?: Record<string, unknown>;
    // Field-level sensitivity. When not Public, the field is masked for readers who are not
    // SuperAdmin and not in visibleToRoles (falling back to a default policy when empty).
    sensitivity?: SensitivityLevel;
    visibleToRoles?: string[];
    mask?: FieldMask;
}

// Mirrors barakoCMS Models.FieldMask (numeric enum, serialized as numbers).
export enum FieldMask {
    Default = 0, // Remove for Hidden, Redact for Sensitive
    Remove = 1, // drop the field
    Redact = 2, // replace with ***
    Last4 = 3, // keep only the last 4 characters
}

export const FIELD_MASKS: { value: FieldMask; label: string }[] = [
    { value: FieldMask.Default, label: 'Default (remove if Hidden, *** if Sensitive)' },
    { value: FieldMask.Remove, label: 'Remove the field entirely' },
    { value: FieldMask.Redact, label: 'Redact to ***' },
    { value: FieldMask.Last4, label: 'Show last 4 only' },
];

export type FieldType = 'string' | 'int' | 'bool' | 'datetime' | 'decimal' | 'array' | 'object';

export interface ContentTypeDefinition {
    id?: string;
    name: string;
    displayName: string;
    description?: string;
    fields: FieldDefinition[];
    createdAt?: string;
    updatedAt?: string;
}

export interface CreateSchemaRequest {
    name: string;
    displayName: string;
    description?: string;
    fields: FieldDefinition[];
}

export const FIELD_TYPES: { value: FieldType; label: string; description: string }[] = [
    { value: 'string', label: 'Text', description: 'A line or block of text' },
    { value: 'int', label: 'Whole number', description: 'Counts and quantities' },
    { value: 'decimal', label: 'Decimal number', description: 'Prices, ratings, measurements' },
    { value: 'bool', label: 'Yes / No', description: 'A true-or-false toggle' },
    { value: 'datetime', label: 'Date & time', description: 'A point in time' },
    { value: 'array', label: 'List', description: 'Multiple values in one field' },
    { value: 'object', label: 'Nested object', description: 'Structured JSON data' },
];
