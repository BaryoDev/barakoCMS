import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';

export interface Backup {
    id: string;
    name: string;
    createdAt: string;
    size: string;
    type: string;
    status: string;
}

export interface CreateBackupResponse {
    id: string;
    message: string;
    path: string;
}

export function useBackups() {
    return useQuery({
        queryKey: ['backups'],
        queryFn: async () => {
            const response = await api.get<{ backups: Backup[] }>('/api/backups');
            return response.data.backups;
        },
    });
}

export function useCreateBackup() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async () => {
            const response = await api.post<CreateBackupResponse>('/api/backups', {});
            return response.data;
        },
        onSuccess: async () => {
            await queryClient.invalidateQueries({ queryKey: ['backups'] });
            await queryClient.refetchQueries({ queryKey: ['backups'] });
        },
    });
}

export function useRestoreBackup() {
    return useMutation({
        mutationFn: async (id: string) => {
            const response = await api.post<{ message: string }>(`/api/backups/${id}/restore`, {});
            return response.data;
        },
    });
}

export function useDeleteBackup() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async (id: string) => {
            const response = await api.delete<{ message: string }>(`/api/backups/${id}`);
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['backups'] });
        },
    });
}
