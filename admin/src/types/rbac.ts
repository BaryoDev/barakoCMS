export interface Role {
    id: string; // Guid
    name: string;
    permissions: string[];
}

export interface UserGroup {
    id: string; // Guid
    name: string;
    roles: string[]; // List of Role Names or IDs depending on backend, checking... usually IDs in relations
}

export interface User {
    id: string; // Guid
    username: string;
    email: string;
    roleIds: string[]; // List<Guid>
    groupIds: string[]; // List<Guid>
    createdAt: string; // DateTime
}

export interface CreateRoleRequest {
    name: string;
    description?: string;
    permissions: any[]; // Changed from string[] to allow backend DTO structure
}

export interface AssignRoleRequest {
    userId: string;
    roleId: string;
}

export interface AssignGroupRequest {
    userId: string;
    groupId: string;
}
