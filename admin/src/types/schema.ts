// Types for Content Type Schema management.
// Field types mirror the backend's enforced set (Core/Validation/FieldTypeValidator.cs).

export interface FieldDefinition {
    name: string; // PascalCase enforced by the backend
    displayName: string;
    type: FieldType;
    isRequired: boolean;
    defaultValue?: unknown;
    validationRules?: Record<string, unknown>;
}

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
