import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api, type Paginated, type PageParams } from '@/lib/api';
import type { User, Role, RoleRequest } from '@/types/rbac';

// Users

export function useUsers(params: PageParams = {}) {
    return useQuery({
        queryKey: ['users', params],
        queryFn: async () => {
            const response = await api.get<Paginated<User>>('/api/users', { params });
            return response.data;
        },
    });
}

// Roles

export function useRoles(params: PageParams = {}) {
    return useQuery({
        queryKey: ['roles', params],
        queryFn: async () => {
            const response = await api.get<Paginated<Role>>('/api/roles', { params });
            return response.data;
        },
    });
}

export function useRole(id: string) {
    return useQuery({
        queryKey: ['roles', 'detail', id],
        queryFn: async () => {
            const response = await api.get<Role>(`/api/roles/${id}`);
            return response.data;
        },
        enabled: !!id,
    });
}

export function useCreateRole() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async (data: RoleRequest) => {
            const response = await api.post<{ id: string; message: string }>('/api/roles', data);
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['roles'] });
        },
    });
}

export function useUpdateRole() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async ({ id, data }: { id: string; data: RoleRequest }) => {
            const response = await api.put<{ message: string }>(`/api/roles/${id}`, { id, ...data });
            return response.data;
        },
        onSuccess: (_data, { id }) => {
            queryClient.invalidateQueries({ queryKey: ['roles'] });
            queryClient.invalidateQueries({ queryKey: ['roles', 'detail', id] });
        },
    });
}

// System roles refuse deletion (403); roles still assigned to users conflict (409).
export function useDeleteRole() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async (id: string) => {
            await api.delete(`/api/roles/${id}`);
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['roles'] });
        },
    });
}

// User ↔ role / group assignment

export function useAssignRole() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async ({ userId, roleId }: { userId: string; roleId: string }) => {
            const response = await api.post(`/api/users/${userId}/roles`, { userId, roleId });
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['users'] });
        },
    });
}

export function useRemoveRole() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async ({ userId, roleId }: { userId: string; roleId: string }) => {
            await api.delete(`/api/users/${userId}/roles/${roleId}`);
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['users'] });
        },
    });
}

export function useAssignGroup() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async ({ userId, groupId }: { userId: string; groupId: string }) => {
            const response = await api.post(`/api/users/${userId}/groups`, { userId, groupId });
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['users'] });
            queryClient.invalidateQueries({ queryKey: ['user-groups'] });
        },
    });
}

export function useRemoveGroup() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async ({ userId, groupId }: { userId: string; groupId: string }) => {
            await api.delete(`/api/users/${userId}/groups/${groupId}`);
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['users'] });
            queryClient.invalidateQueries({ queryKey: ['user-groups'] });
        },
    });
}
