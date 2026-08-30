// Types for Content items (event-sourced on the backend).

export interface ContentListItem {
    id: string;
    contentType: string;
    data: Record<string, unknown>;
    createdAt: string;
    updatedAt: string;
    // The list omitted these until 4.0, so an entries table could not show whether a row was a
    // Draft without a second request per row.
    status: ContentStatus;
    sensitivity: SensitivityLevel;
}

export interface ContentDetail extends ContentListItem {
    lastModifiedBy?: string;
    version: number; // echo back on update, the backend enforces optimistic concurrency (412)
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

/** One event from a content stream. Not every event is a document version. */
export interface ContentVersion {
    id: string;
    /** Which kind of change this was. Decided server side, not the event class name. */
    changeType: 'Created' | 'Updated' | 'StatusChanged' | 'Scheduled' | 'SensitivityChanged' | string;
    /** Only Created and Updated carry a document. Absent on the rest. */
    data?: Record<string, unknown>;
    lastModifiedBy?: string;
    versionId: string;
    // UTC, with a zone. There used to be an updatedAt here carrying the same instant without one,
    // which new Date() read as local time.
    timestamp: string;
    status?: ContentStatus;
    scheduledPublishAt?: string;
    scheduledUnpublishAt?: string;
    sensitivity?: SensitivityLevel;
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
