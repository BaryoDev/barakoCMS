import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { ContentItem, CreateContentRequest, UpdateContentRequest } from '@/types/content';

// Fetch all content (optionally filtered by type)
export function useContents(contentType?: string) {
    return useQuery({
        queryKey: ['contents', contentType],
        queryFn: async () => {
            const params = contentType ? { contentType } : {};
            const response = await api.get<ContentItem[]>('/api/contents', { params });
            return response.data;
        },
    });
}

// Fetch single content item
export function useContent(id: string) {
    return useQuery({
        queryKey: ['contents', id],
        queryFn: async () => {
            const response = await api.get<ContentItem>(`/api/contents/${id}`);
            return response.data;
        },
        enabled: !!id,
    });
}

// Create content
export function useCreateContent() {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async (data: CreateContentRequest) => {
            const response = await api.post<ContentItem>('/api/contents', data);
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['contents'] });
        },
    });
}

// Update content
export function useUpdateContent() {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async ({ id, data }: { id: string; data: UpdateContentRequest }) => {
            const response = await api.put<ContentItem>(`/api/contents/${id}`, data);
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['contents'] });
        },
    });
}

// Delete content
export function useDeleteContent() {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async (id: string) => {
            await api.delete(`/api/contents/${id}`);
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['contents'] });
        },
    });
}
