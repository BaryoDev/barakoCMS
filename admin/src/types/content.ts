// Types for Content items

export interface ContentItem {
    id: string;
    contentType: string;
    data: Record<string, unknown>;
    status: ContentStatus;
    createdAt: string;
    updatedAt: string;
    createdBy?: string;
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
    status?: ContentStatus;
    sensitivity?: SensitivityLevel;
}

export const SENSITIVITY_LABELS: Record<SensitivityLevel, { label: string; color: string }> = {
    [SensitivityLevel.Public]: { label: 'Public', color: 'text-green-400 bg-green-500/10 border-green-500/50' },
    [SensitivityLevel.Sensitive]: { label: 'Sensitive', color: 'text-purple-400 bg-purple-500/10 border-purple-500/50' },
    [SensitivityLevel.Hidden]: { label: 'Hidden', color: 'text-slate-400 bg-slate-500/10 border-slate-500/50' },
};

export const STATUS_LABELS: Record<ContentStatus, { label: string; color: string }> = {
    [ContentStatus.Draft]: { label: 'Draft', color: 'text-yellow-400 bg-yellow-500/10 border-yellow-500/50' },
    [ContentStatus.Published]: { label: 'Published', color: 'text-green-400 bg-green-500/10 border-green-500/50' },
    [ContentStatus.Archived]: { label: 'Archived', color: 'text-slate-400 bg-slate-500/10 border-slate-500/50' },
};
