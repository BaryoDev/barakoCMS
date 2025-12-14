'use client';

import { useEffect } from 'react';
import { useAuth } from '@/hooks/use-auth';
import { useRoles } from '@/hooks/use-rbac';
import { Header } from '@/components/header';
import Link from 'next/link';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';

export default function RolesPage() {
    const { isAuthenticated, isLoading: authLoading, requireAuth } = useAuth();
    const { data: roles, isLoading: rolesLoading } = useRoles();

    useEffect(() => {
        requireAuth();
    }, [requireAuth]);

    if (authLoading || rolesLoading) {
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
                <div className="flex items-center justify-between mb-8">
                    <div>
                        <h1 className="text-3xl font-bold text-white mb-2">Roles</h1>
                        <p className="text-slate-400">System roles and permissions</p>
                    </div>
                    <Link href="/roles/new">
                        <Button className="bg-gradient-to-r from-amber-500 to-orange-600 hover:from-amber-600 hover:to-orange-700 text-white">
                            + New Role
                        </Button>
                    </Link>
                </div>

                <div className="grid gap-6">
                    <Card className="bg-slate-800/50 border-slate-700">
                        <CardHeader>
                            <CardTitle className="text-white">Defined Roles</CardTitle>
                            <CardDescription>Roles control what users can do in the system</CardDescription>
                        </CardHeader>
                        <CardContent>
                            <div className="space-y-4">
                                {roles?.map((role) => (
                                    <div key={role.id} className="p-4 rounded-lg bg-slate-800 border border-slate-700">
                                        <div className="flex items-center justify-between mb-2">
                                            <h3 className="text-lg font-semibold text-white">{role.name}</h3>
                                            <Badge variant="outline" className="text-xs">{role.id}</Badge>
                                        </div>
                                        <div className="flex flex-wrap gap-2">
                                            {role.permissions.map((perm: any, i) => {
                                                if (typeof perm === 'string') return <Badge key={i} variant="secondary">{perm}</Badge>;

                                                // Calculate actions string
                                                const actions = [];
                                                if (perm.create?.enabled) actions.push('C');
                                                if (perm.read?.enabled) actions.push('R');
                                                if (perm.update?.enabled) actions.push('U');
                                                if (perm.delete?.enabled) actions.push('D');

                                                return (
                                                    <Badge key={i} variant="secondary" className="bg-blue-500/10 text-blue-400 border-blue-500/20">
                                                        {perm.contentTypeSlug} ({actions.join('') || 'None'})
                                                    </Badge>
                                                );
                                            })}
                                            {(!role.permissions || role.permissions.length === 0) && <span className="text-slate-500 text-sm italic">No specific permissions</span>}
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
