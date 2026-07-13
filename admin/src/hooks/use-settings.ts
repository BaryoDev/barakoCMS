import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';

export interface SystemSetting {
    key: string;
    value: string;
    description: string;
    category: string;
    updatedAt: string;
}

export function useSettings() {
    return useQuery({
        queryKey: ['settings'],
        queryFn: async () => {
            const response = await api.get<{ settings: SystemSetting[] }>('/api/settings');
            return response.data.settings;
        },
    });
}

export function useUpdateSetting() {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async ({ key, value }: { key: string; value: string }) => {
            const response = await api.post<{ success: boolean; message: string }>('/api/settings', {
                key,
                value,
            });
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['settings'] });
        },
    });
}
