import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { UserGroup } from '@/types/rbac';

export function useUserGroups() {
    return useQuery({
        queryKey: ['user-groups'],
        queryFn: async () => {
            const response = await api.get<UserGroup[]>('/api/user-groups');
            return response.data;
        },
    });
}

export function useUserGroup(id: string) {
    return useQuery({
        queryKey: ['user-groups', 'detail', id],
        queryFn: async () => {
            const response = await api.get<UserGroup>(`/api/user-groups/${id}`);
            return response.data;
        },
        enabled: !!id,
    });
}

export function useCreateUserGroup() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async (data: { name: string; description?: string; userIds?: string[] }) => {
            const response = await api.post<{ id: string; message: string }>('/api/user-groups', data);
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['user-groups'] });
        },
    });
}

export function useUpdateUserGroup() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async ({ id, data }: { id: string; data: { name: string; description?: string } }) => {
            const response = await api.put<{ message: string }>(`/api/user-groups/${id}`, { id, ...data });
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['user-groups'] });
        },
    });
}

// Groups with members refuse deletion (409) — remove members first.
export function useDeleteUserGroup() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async (id: string) => {
            await api.delete(`/api/user-groups/${id}`);
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['user-groups'] });
        },
    });
}

export function useAddGroupMember() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async ({ groupId, userId }: { groupId: string; userId: string }) => {
            const response = await api.post(`/api/user-groups/${groupId}/users`, { groupId, userId });
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['user-groups'] });
            queryClient.invalidateQueries({ queryKey: ['users'] });
        },
    });
}

export function useRemoveGroupMember() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async ({ groupId, userId }: { groupId: string; userId: string }) => {
            await api.delete(`/api/user-groups/${groupId}/users/${userId}`);
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['user-groups'] });
            queryClient.invalidateQueries({ queryKey: ['users'] });
        },
    });
}
