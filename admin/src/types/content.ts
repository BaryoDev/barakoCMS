// Types for Content items (event-sourced on the backend).

export interface ContentListItem {
    id: string;
    contentType: string;
    data: Record<string, unknown>;
    createdAt: string;
    updatedAt: string;
}

export interface ContentDetail extends ContentListItem {
    status: ContentStatus;
    sensitivity: SensitivityLevel;
    lastModifiedBy?: string;
    version: number; // echo back on update — the backend enforces optimistic concurrency (412)
}

export enum ContentStatus {
    Draft = 0,
    Published = 1,
    Archived = 2,
}

export enum SensitivityLevel {
    Public = 0,
    Sensitive = 1,
    Hidden = 2,
}

export interface CreateContentRequest {
    contentType: string;
    data: Record<string, unknown>;
    status: ContentStatus;
    sensitivity?: SensitivityLevel;
}

export interface UpdateContentRequest {
    data: Record<string, unknown>;
    status: ContentStatus;
    version: number;
}

export interface ContentVersion {
    id: string;
    data: Record<string, unknown>;
    updatedAt: string;
    lastModifiedBy?: string;
    versionId: string;
    timestamp: string;
}

export const STATUS_META: Record<ContentStatus, { label: string; tone: 'muted' | 'success' | 'warning' }> = {
    [ContentStatus.Draft]: { label: 'Draft', tone: 'warning' },
    [ContentStatus.Published]: { label: 'Published', tone: 'success' },
    [ContentStatus.Archived]: { label: 'Archived', tone: 'muted' },
};

export const SENSITIVITY_META: Record<SensitivityLevel, { label: string; description: string }> = {
    [SensitivityLevel.Public]: { label: 'Public', description: 'Visible to every reader' },
    [SensitivityLevel.Sensitive]: { label: 'Sensitive', description: 'Data hidden except from SuperAdmin and HR' },
    [SensitivityLevel.Hidden]: { label: 'Hidden', description: 'Data hidden except from SuperAdmin' },
};
