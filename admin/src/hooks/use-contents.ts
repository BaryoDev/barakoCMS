import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api, type Paginated, type PageParams } from '@/lib/api';
import type {
    ContentListItem,
    ContentDetail,
    ContentVersion,
    CreateContentRequest,
    UpdateContentRequest,
    ContentStatus,
    ScheduleContentRequest,
} from '@/types/content';

export function useContents(params: PageParams & { contentType?: string } = {}) {
    return useQuery({
        queryKey: ['contents', 'list', params],
        queryFn: async () => {
            const response = await api.get<Paginated<ContentListItem>>('/api/contents', { params });
            return response.data;
        },
    });
}

export function useContent(id: string) {
    return useQuery({
        queryKey: ['contents', 'detail', id],
        queryFn: async () => {
            const response = await api.get<ContentDetail>(`/api/contents/${id}`);
            return response.data;
        },
        enabled: !!id,
    });
}

export function useContentHistory(id: string, enabled = true) {
    return useQuery({
        queryKey: ['contents', 'history', id],
        queryFn: async () => {
            // History is a collection like any other and uses the paginated envelope. It read
            // `versions` here, which the endpoint stopped returning when the envelope landed, so the
            // panel has been rendering an empty list rather than failing visibly.
            const response = await api.get<Paginated<ContentVersion>>(`/api/contents/${id}/history`);
            return response.data.items;
        },
        enabled: !!id && enabled,
    });
}

export function useCreateContent() {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async (data: CreateContentRequest) => {
            const response = await api.post<{ id: string; version: number }>('/api/contents', data);
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['contents'] });
        },
    });
}

export function useUpdateContent() {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async ({ id, data }: { id: string; data: UpdateContentRequest }) => {
            const response = await api.put<{ id: string; version: number }>(`/api/contents/${id}`, {
                id,
                ...data,
            });
            return response.data;
        },
        onSuccess: (_data, { id }) => {
            queryClient.invalidateQueries({ queryKey: ['contents'] });
            queryClient.invalidateQueries({ queryKey: ['contents', 'detail', id] });
        },
    });
}

export function useScheduleContent() {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async ({ id, schedule }: { id: string; schedule: ScheduleContentRequest }) => {
            const response = await api.put<{ id: string }>(`/api/contents/${id}/schedule`, {
                id,
                ...schedule,
            });
            return response.data;
        },
        onSuccess: (_data, { id }) => {
            queryClient.invalidateQueries({ queryKey: ['contents'] });
            queryClient.invalidateQueries({ queryKey: ['contents', 'detail', id] });
        },
    });
}

export function useUpdateContentStatus() {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async ({ id, status }: { id: string; status: ContentStatus }) => {
            const response = await api.put<{ message: string }>(`/api/contents/${id}/status`, {
                id,
                newStatus: status,
            });
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['contents'] });
        },
    });
}

// SuperAdmin/Admin only.
export function useRollbackContent() {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async ({ id, versionId }: { id: string; versionId: string }) => {
            const response = await api.post(`/api/contents/${id}/rollback/${versionId}`, {});
            return response.data;
        },
        onSuccess: (_data, { id }) => {
            queryClient.invalidateQueries({ queryKey: ['contents'] });
            queryClient.invalidateQueries({ queryKey: ['contents', 'detail', id] });
            queryClient.invalidateQueries({ queryKey: ['contents', 'history', id] });
        },
    });
}
