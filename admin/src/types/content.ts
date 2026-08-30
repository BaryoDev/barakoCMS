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

// Names, not numbers, matching the API from 4.0. These used to be 0/1/2, transcribed from the
// server's enum, so inserting a member there silently renumbered everything here. The switch is not
// only a rename: ContentStatus.Draft was 0, which is falsy, and 'Draft' is not, so any truthiness
// check written against the old values means the opposite now.
export enum ContentStatus {
    Draft = 'Draft',
    Published = 'Published',
    Archived = 'Archived',
}

export enum SensitivityLevel {
    Public = 'Public',
    Sensitive = 'Sensitive',
    Hidden = 'Hidden',
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
    lastModifiedBy?: string;
    versionId: string;
    // UTC, with a zone. There used to be an updatedAt here carrying the same instant without one,
    // which new Date() read as local time.
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
