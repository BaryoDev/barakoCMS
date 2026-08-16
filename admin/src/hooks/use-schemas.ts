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

// Public delivery is the one property of an existing content type that can be changed. Everything
// else about a type is create-only, which is why this is its own endpoint rather than part of a
// general update: turning on anonymous access to a whole type is a decision worth making on purpose.
export function useSetPublicDelivery(name: string) {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async (enabled: boolean) => {
            const response = await api.put<{ name: string; isPubliclyDeliverable: boolean }>(
                `/api/content-types/${encodeURIComponent(name)}/public-delivery`,
                { enabled },
            );
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['schemas'] });
        },
    });
}

// Content types are otherwise create-only on the API — no general update or delete endpoints exist.
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
