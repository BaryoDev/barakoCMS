import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { User, Role, UserGroup, AssignRoleRequest, AssignGroupRequest, CreateRoleRequest } from '@/types/rbac';

// Users
export function useUsers() {
    return useQuery({
        queryKey: ['users'],
        queryFn: async () => {
            const response = await api.get<User[]>('/api/users');
            return response.data;
        },
    });
}

// Roles
export function useRoles() {
    return useQuery({
        queryKey: ['roles'],
        queryFn: async () => {
            const response = await api.get<Role[]>('/api/roles');
            return response.data;
        },
    });
}

// User Groups
export function useUserGroups() {
    return useQuery({
        queryKey: ['user-groups'],
        queryFn: async () => {
            const response = await api.get<UserGroup[]>('/api/user-groups');
            return response.data;
        },
    });
}

// Create Role
export function useCreateRole() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async (data: any) => {
            // Transform permission strings to backend objects
            const permissionStrings = data.permissions as string[];
            const transformedPermissions: any[] = [];
            const permissionsMap = new Map<string, any>();

            permissionStrings.forEach(perm => {
                // Format: contents:{slug}:{action}
                const parts = perm.split(':');
                if (parts.length < 3) return;

                const slug = parts[1];
                const action = parts[2]; // create, read, update, delete

                if (!permissionsMap.has(slug)) {
                    permissionsMap.set(slug, {
                        contentTypeSlug: slug,
                        create: { enabled: false },
                        read: { enabled: false },
                        update: { enabled: false },
                        delete: { enabled: false }
                    });
                }

                const entry = permissionsMap.get(slug);
                if (action === 'create') entry.create.enabled = true;
                if (action === 'read') entry.read.enabled = true;
                if (action === 'update') entry.update.enabled = true;
                if (action === 'delete') entry.delete.enabled = true;
            });

            const payload = {
                ...data,
                permissions: Array.from(permissionsMap.values())
            };

            const response = await api.post<Role>('/api/roles', payload);
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['roles'] });
        },
    });
}

// Update Role
export function useUpdateRole() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async ({ id, data }: { id: string; data: CreateRoleRequest }) => {
            const response = await api.put<Role>(`/api/roles/${id}`, data);
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['roles'] });
        },
    });
}

// Assign Role
export function useAssignRole() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async ({ userId, roleId }: AssignRoleRequest) => {
            const response = await api.post(`/api/users/${userId}/roles`, {
                userId,
                roleId
            });
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['users'] });
        },
    });
}

// Remove Role
export function useRemoveRole() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async ({ userId, roleId }: { userId: string, roleId: string }) => {
            await api.delete(`/api/users/${userId}/roles/${roleId}`);
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['users'] });
        },
    });
}

// Assign Group
export function useAssignGroup() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async ({ userId, groupId }: AssignGroupRequest) => {
            const response = await api.post(`/api/users/${userId}/groups`, {
                userId,
                groupId
            });
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['users'] });
        },
    });
}

// Remove Group
export function useRemoveGroup() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async ({ userId, groupId }: { userId: string, groupId: string }) => {
            await api.delete(`/api/users/${userId}/groups/${groupId}`);
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['users'] });
        },
    });
}
