import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { ContentTypeDefinition, CreateSchemaRequest } from '@/types/schema';

// Fetch all schemas
export function useSchemas() {
    return useQuery({
        queryKey: ['schemas'],
        queryFn: async () => {
            const response = await api.get<ContentTypeDefinition[]>('/api/schemas');
            return response.data;
        },
    });
}

// Fetch single schema
export function useSchema(name: string) {
    return useQuery({
        queryKey: ['schemas', name],
        queryFn: async () => {
            const response = await api.get<ContentTypeDefinition>(`/api/schemas/${name}`);
            return response.data;
        },
        enabled: !!name,
    });
}

// Create schema
export function useCreateSchema() {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async (data: CreateSchemaRequest) => {
            const response = await api.post<ContentTypeDefinition>('/api/schemas', data);
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['schemas'] });
        },
    });
}

// Update schema
export function useUpdateSchema() {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async ({ name, data }: { name: string; data: CreateSchemaRequest }) => {
            const response = await api.put<ContentTypeDefinition>(`/api/schemas/${name}`, data);
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['schemas'] });
        },
    });
}

// Delete schema
export function useDeleteSchema() {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async (name: string) => {
            await api.delete(`/api/schemas/${name}`);
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['schemas'] });
        },
    });
}
