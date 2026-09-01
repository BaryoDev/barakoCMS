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
    // Null when nothing is armed. Both UTC with a zone, so new Date() reads them correctly wherever
    // the browser is.
    scheduledPublishAt?: string | null;
    scheduledUnpublishAt?: string | null;
}

export interface ScheduleContentRequest {
    scheduledPublishAt: string | null;
    scheduledUnpublishAt: string | null;
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

/**
 * The badge for a status, without inventing one the server did not send.
 *
 * Falling back to Draft looks harmless and is not: a row the server said nothing about renders as a
 * genuine Draft, with a warning badge, indistinguishable from a real one. Nobody can tell from the
 * screen that the field was missing.
 *
 * It is reachable rather than theoretical. The admin is its own deployable and picks its API at
 * runtime from window._env_, so it can point at an older server, and the content list only started
 * returning `status` in 4.0. A 4.0 admin against any currently released API would label every row
 * Draft. Showing the raw value is worse-looking and better: it says the two are out of step, which is
 * exactly the drift these changes exist to stop hiding.
 */
export function statusMeta(status: string | undefined): { label: string; tone: 'muted' | 'success' | 'warning' } {
    return STATUS_META[status as ContentStatus] ?? { label: status ?? 'Unknown', tone: 'muted' };
}

export const SENSITIVITY_META: Record<SensitivityLevel, { label: string; description: string }> = {
    [SensitivityLevel.Public]: { label: 'Public', description: 'Visible to every reader' },
    [SensitivityLevel.Sensitive]: { label: 'Sensitive', description: 'Data hidden except from SuperAdmin and HR' },
    [SensitivityLevel.Hidden]: { label: 'Hidden', description: 'Data hidden except from SuperAdmin' },
};
