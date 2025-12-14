'use client';

import { useEffect, useState } from 'react';
import { useAuth } from '@/hooks/use-auth';
import { useUsers, useRoles, useUserGroups, useAssignRole, useRemoveRole, useAssignGroup, useRemoveGroup } from '@/hooks/use-rbac';
import { Header } from '@/components/header';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from '@/components/ui/dialog';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Label } from '@/components/ui/label';
import { User, Role, UserGroup } from '@/types/rbac';
import { format } from 'date-fns';

export default function UsersPage() {
    const { isAuthenticated, isLoading: authLoading, requireAuth } = useAuth();
    const { data: users, isLoading: usersLoading } = useUsers();
    const { data: roles } = useRoles();
    const { data: groups } = useUserGroups();

    const assignRoleMutation = useAssignRole();
    const removeRoleMutation = useRemoveRole();

    const [selectedUser, setSelectedUser] = useState<User | null>(null);
    const [selectedRole, setSelectedRole] = useState<string>('');
    const [isRoleDialogOpen, setIsRoleDialogOpen] = useState(false);

    useEffect(() => {
        requireAuth();
    }, [requireAuth]);

    const handleAssignRole = async () => {
        if (!selectedUser || !selectedRole) return;
        await assignRoleMutation.mutateAsync({ userId: selectedUser.id, roleId: selectedRole });
        setIsRoleDialogOpen(false);
        setSelectedRole('');
    };

    const handleRemoveRole = async (userId: string, roleId: string) => {
        if (confirm('Are you sure you want to remove this role?')) {
            await removeRoleMutation.mutateAsync({ userId, roleId });
        }
    };

    // Helper to get role name from ID
    const getRoleName = (roleId: string) => {
        return roles?.find(r => r.id === roleId)?.name || roleId;
    };

    if (authLoading || usersLoading) {
        return (
            <div className="min-h-screen flex items-center justify-center bg-slate-900">
                <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-amber-500"></div>
            </div>
        );
    }

    if (!isAuthenticated) return null;

    return (
        <div className="min-h-screen bg-slate-900">
            <Header />
            <main className="container mx-auto px-4 py-8 max-w-7xl">
                <div className="mb-8 flex items-center justify-between">
                    <div>
                        <h1 className="text-3xl font-bold text-white mb-2">Users</h1>
                        <p className="text-slate-400">Manage users and their access roles</p>
                    </div>
                </div>

                <div className="grid gap-6">
                    <Card className="bg-slate-800/50 border-slate-700">
                        <CardHeader>
                            <CardTitle className="text-white">All Users</CardTitle>
                            <CardDescription>List of registered users in the system</CardDescription>
                        </CardHeader>
                        <CardContent>
                            <div className="overflow-x-auto">
                                <table className="w-full text-left text-sm text-slate-400">
                                    <thead className="bg-slate-800 text-xs uppercase text-slate-200">
                                        <tr>
                                            <th className="px-6 py-3">Username</th>
                                            <th className="px-6 py-3">Email</th>
                                            <th className="px-6 py-3">Roles</th>
                                            <th className="px-6 py-3">Groups</th>
                                            <th className="px-6 py-3">Joined</th>
                                            <th className="px-6 py-3 text-right">Actions</th>
                                        </tr>
                                    </thead>
                                    <tbody className="divide-y divide-slate-700">
                                        {users?.map((user) => (
                                            <tr key={user.id} className="hover:bg-slate-700/50">
                                                <td className="px-6 py-4 font-medium text-white">{user.username}</td>
                                                <td className="px-6 py-4">{user.email}</td>
                                                <td className="px-6 py-4">
                                                    <div className="flex flex-wrap gap-1">
                                                        {user.roleIds.map(roleId => (
                                                            <Badge key={roleId} variant="secondary" className="bg-purple-500/10 text-purple-400 border-purple-500/20 hover:bg-purple-500/20 flex gap-1 items-center">
                                                                {getRoleName(roleId)}
                                                                <button onClick={() => handleRemoveRole(user.id, roleId)} className="ml-1 hover:text-red-400">×</button>
                                                            </Badge>
                                                        ))}
                                                        {user.roleIds.length === 0 && <span className="text-xs italic text-slate-500">No roles</span>}
                                                    </div>
                                                </td>
                                                <td className="px-6 py-4">
                                                    <div className="flex flex-wrap gap-1">
                                                        {user.groupIds.map(groupId => (
                                                            <Badge key={groupId} variant="outline" className="text-xs">
                                                                {groups?.find(g => g.id === groupId)?.name || groupId}
                                                            </Badge>
                                                        ))}
                                                        {user.groupIds.length === 0 && <span className="text-xs italic text-slate-500">-</span>}
                                                    </div>
                                                </td>
                                                <td className="px-6 py-4">{format(new Date(user.createdAt), 'MMM d, yyyy')}</td>
                                                <td className="px-6 py-4 text-right">
                                                    <Button
                                                        variant="ghost"
                                                        size="sm"
                                                        className="text-amber-400 hover:text-amber-300 hover:bg-amber-500/10"
                                                        onClick={() => {
                                                            setSelectedUser(user);
                                                            setIsRoleDialogOpen(true);
                                                        }}
                                                    >
                                                        Manage Roles
                                                    </Button>
                                                </td>
                                            </tr>
                                        ))}
                                    </tbody>
                                </table>
                            </div>
                        </CardContent>
                    </Card>
                </div>

                {/* Assign Role Dialog */}
                <Dialog open={isRoleDialogOpen} onOpenChange={setIsRoleDialogOpen}>
                    <DialogContent className="bg-slate-800 border-slate-700 text-white">
                        <DialogHeader>
                            <DialogTitle>Assign Role</DialogTitle>
                            <DialogDescription>
                                Assign a role to <strong>{selectedUser?.username}</strong>.
                            </DialogDescription>
                        </DialogHeader>
                        <div className="grid gap-4 py-4">
                            <div className="grid gap-2">
                                <Label htmlFor="role">Select Role</Label>
                                <Select value={selectedRole} onValueChange={setSelectedRole}>
                                    <SelectTrigger className="bg-slate-900 border-slate-600">
                                        <SelectValue placeholder="Select role..." />
                                    </SelectTrigger>
                                    <SelectContent className="bg-slate-800 border-slate-700">
                                        {roles?.map(role => (
                                            <SelectItem key={role.id} value={role.id} className="text-white hover:bg-slate-700">
                                                {role.name}
                                            </SelectItem>
                                        ))}
                                    </SelectContent>
                                </Select>
                            </div>
                        </div>
                        <DialogFooter>
                            <Button variant="outline" onClick={() => setIsRoleDialogOpen(false)} className="border-slate-600 text-slate-300">Cancel</Button>
                            <Button onClick={handleAssignRole} disabled={!selectedRole || assignRoleMutation.isPending} className="bg-amber-500 hover:bg-amber-600 text-white">
                                {assignRoleMutation.isPending ? 'Assigning...' : 'Assign Role'}
                            </Button>
                        </DialogFooter>
                    </DialogContent>
                </Dialog>
            </main>
        </div>
    );
}
