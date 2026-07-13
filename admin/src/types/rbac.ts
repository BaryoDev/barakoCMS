// Types mirroring the backend RBAC model (Models/Role.cs, ContentTypePermission.cs).
// Permissions are per-content-type CRUD rules; grants are additive across roles.

export type PermissionAction = 'create' | 'read' | 'update' | 'delete';

export interface PermissionRule {
    enabled: boolean;
    // Directus-style conditions, e.g. { "CreatedBy": { "_eq": "$CURRENT_USER" } }
    conditions?: Record<string, Record<string, unknown>> | null;
}

export interface ContentTypePermission {
    contentTypeSlug: string;
    create: PermissionRule;
    read: PermissionRule;
    update: PermissionRule;
    delete: PermissionRule;
}

export interface Role {
    id: string;
    name: string;
    description?: string;
    permissions: ContentTypePermission[];
    systemCapabilities: string[];
    createdAt?: string;
}

export interface RoleRequest {
    name: string;
    description?: string;
    permissions: ContentTypePermission[];
    systemCapabilities: string[];
}

export interface User {
    id: string;
    username: string;
    email: string;
    roleIds: string[];
    groupIds: string[];
    createdAt: string;
}

export interface UserGroup {
    id: string;
    name: string;
    description?: string;
    userIds: string[];
    parentGroupId?: string | null;
    childGroupIds?: string[];
}

// Seeded, non-deletable system roles (Data/DataSeeder.cs)
export const SYSTEM_ROLE_NAMES = ['SuperAdmin', 'Admin', 'HR', 'User'];

export function emptyRule(): PermissionRule {
    return { enabled: false };
}

export function emptyPermission(slug: string): ContentTypePermission {
    return {
        contentTypeSlug: slug,
        create: emptyRule(),
        read: emptyRule(),
        update: emptyRule(),
        delete: emptyRule(),
    };
}
