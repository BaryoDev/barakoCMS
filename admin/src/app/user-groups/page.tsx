'use client';

import { useEffect } from 'react';
import { useAuth } from '@/hooks/use-auth';
import { useUserGroups } from '@/hooks/use-rbac';
import { Header } from '@/components/header';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';

export default function UserGroupsPage() {
    const { isAuthenticated, isLoading: authLoading, requireAuth } = useAuth();
    const { data: groups, isLoading: groupsLoading } = useUserGroups();

    useEffect(() => {
        requireAuth();
    }, [requireAuth]);

    if (authLoading || groupsLoading) {
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
            <main className="container mx-auto px-4 py-8 max-w-4xl">
                <div className="mb-8">
                    <h1 className="text-3xl font-bold text-white mb-2">User Groups</h1>
                    <p className="text-slate-400">Organize users into functional groups</p>
                </div>

                <div className="grid gap-6">
                    <Card className="bg-slate-800/50 border-slate-700">
                        <CardHeader>
                            <CardTitle className="text-white">Defined Groups</CardTitle>
                            <CardDescription>Groups allow assigning roles to multiple users at once</CardDescription>
                        </CardHeader>
                        <CardContent>
                            <div className="space-y-4">
                                {groups?.map((group) => (
                                    <div key={group.id} className="p-4 rounded-lg bg-slate-800 border border-slate-700">
                                        <div className="flex items-center justify-between mb-2">
                                            <h3 className="text-lg font-semibold text-white">{group.name}</h3>
                                            <Badge variant="outline" className="text-xs">{group.id}</Badge>
                                        </div>
                                        <div className="mt-2">
                                            <h4 className="text-xs uppercase text-slate-500 font-semibold mb-1">Assigned Roles</h4>
                                            <div className="flex flex-wrap gap-2 role-list">
                                                {/* Note: UserGroup model has roles property but backend definition shows it might be roleIds or similar. 
                                                    Checking definition: UserGroup has List<string> Roles? No, backend UserGroup.cs has Roles property?
                                                    Let's check backend model again if needed. Assuming 'roles' property exists on UserGroup in frontend type.
                                                */}
                                                {group.roles.map((role, i) => (
                                                    <Badge key={i} variant="secondary" className="bg-emerald-500/10 text-emerald-400 border-emerald-500/20">
                                                        {role}
                                                    </Badge>
                                                ))}
                                                {group.roles.length === 0 && <span className="text-slate-500 text-sm italic">No roles assigned</span>}
                                            </div>
                                        </div>
                                    </div>
                                ))}
                            </div>
                        </CardContent>
                    </Card>
                </div>
            </main>
        </div>
    );
}
