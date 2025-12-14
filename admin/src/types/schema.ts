// Types for Content Type Schema management

export interface FieldDefinition {
    name: string;
    displayName: string;
    type: 'text' | 'number' | 'boolean' | 'date' | 'richtext' | 'image';
    isRequired: boolean;
    validationRules?: Record<string, unknown>;
}

export interface ContentTypeDefinition {
    id?: string;
    name: string;
    displayName: string;
    description?: string;
    sensitivity?: SensitivityLevel;
    fields: FieldDefinition[];
    createdAt?: string;
    updatedAt?: string;
}

export type SensitivityLevel = 'public' | 'internal' | 'confidential';

export interface CreateSchemaRequest {
    name: string;
    displayName: string;
    description?: string;
    sensitivity?: SensitivityLevel;
    fields: FieldDefinition[];
}

export interface SchemaListResponse {
    schemas: ContentTypeDefinition[];
    total: number;
}

export const FIELD_TYPES = [
    { value: 'text', label: 'Text', icon: '📝', description: 'Single or multi-line text' },
    { value: 'number', label: 'Number', icon: '🔢', description: 'Integer or decimal numbers' },
    { value: 'boolean', label: 'Boolean', icon: '✓', description: 'True/False toggle' },
    { value: 'date', label: 'Date', icon: '📅', description: 'Date and time picker' },
    { value: 'richtext', label: 'Rich Text', icon: '📄', description: 'Formatted content editor' },
    { value: 'image', label: 'Image', icon: '🖼️', description: 'Image upload field' },
] as const;
