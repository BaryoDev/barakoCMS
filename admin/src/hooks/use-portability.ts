'use client';

import { useMutation } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { ContentTypeDefinition } from '@/types/schema';

/** One entry in a bundle. Carries no id: an import creates new records rather than restoring old ones. */
export interface ContentRecord {
    contentType: string;
    data: Record<string, unknown>;
    status: string;
}

export interface PortabilityBundle {
    contentTypes: ContentTypeDefinition[];
    contents: ContentRecord[];
}

export interface ImportReport {
    dryRun: boolean;
    contentTypesCreated: number;
    contentTypesUpdated: number;
    contentsCreated: number;
    /**
     * Records whose content type is in neither the store nor the bundle. They are still created,
     * but nothing treats their fields as public, so they never appear in public search.
     */
    contentsWithoutContentType: number;
}

export function useExportBundle() {
    return useMutation({
        mutationFn: async (types?: string[]) => {
            const response = await api.get<PortabilityBundle>('/api/portability/export', {
                params: types?.length ? { types: types.join(',') } : undefined,
            });
            return response.data;
        },
    });
}

export function useImportBundle() {
    return useMutation({
        mutationFn: async (input: { bundle: PortabilityBundle; dryRun: boolean }) => {
            const response = await api.post<ImportReport>('/api/portability/import', {
                dryRun: input.dryRun,
                contentTypes: input.bundle.contentTypes,
                contents: input.bundle.contents,
            });
            return response.data;
        },
    });
}
