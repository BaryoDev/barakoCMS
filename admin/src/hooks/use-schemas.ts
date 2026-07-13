import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { ContentTypeDefinition, CreateSchemaRequest } from '@/types/schema';

export function useSchemas() {
    return useQuery({
        queryKey: ['schemas'],
        queryFn: async () => {
            const response = await api.get<ContentTypeDefinition[]>('/api/schemas');
            return response.data;
        },
    });
}

// The backend has no single-schema endpoint; select from the cached list.
export function useSchema(name: string) {
    const query = useSchemas();
    return {
        ...query,
        data: query.data?.find((s) => s.name === name),
    };
}

// Content types are create-only on the API — no update or delete endpoints exist.
export function useCreateSchema() {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async (data: CreateSchemaRequest) => {
            const response = await api.post<{ id: string; name: string }>('/api/content-types', data);
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['schemas'] });
        },
    });
}
