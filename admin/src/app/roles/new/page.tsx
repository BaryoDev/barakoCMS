'use client';

import { useEffect, useState } from 'react';
import { useRouter } from 'next/navigation';
import Link from 'next/link';
import { useAuth } from '@/hooks/use-auth';
import { useCreateRole } from '@/hooks/use-rbac';
import { useSchemas } from '@/hooks/use-schemas';
import { Header } from '@/components/header';
import { PermissionMatrix } from '@/components/rbac/permission-matrix';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Separator } from '@/components/ui/separator';

export default function NewRolePage() {
    const router = useRouter();
    const { isAuthenticated, isLoading: authLoading, requireAuth } = useAuth();
    const createRoleMutation = useCreateRole();
    const { data: schemas, isLoading: schemasLoading } = useSchemas();

    const [name, setName] = useState('');
    const [permissions, setPermissions] = useState<string[]>([]);
    const [error, setError] = useState<string | null>(null);

    useEffect(() => {
        requireAuth();
    }, [requireAuth]);

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        setError(null);

        if (!name) {
            setError('Role Name is required');
            return;
        }

        try {
            await createRoleMutation.mutateAsync({
                name,
                permissions,
            });
            router.push('/roles');
        } catch (err: unknown) {
            if (err && typeof err === 'object' && 'response' in err) {
                const axiosError = err as { response?: { data?: { message?: string } } };
                setError(axiosError.response?.data?.message || 'Failed to create role');
            } else {
                setError('Failed to create role');
            }
        }
    };

    if (authLoading || schemasLoading) {
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
                    <Link href="/roles" className="text-slate-400 hover:text-white text-sm flex items-center gap-2 mb-4">
                        ← Back to Roles
                    </Link>
                    <h1 className="text-3xl font-bold text-white mb-2">New Role</h1>
                    <p className="text-slate-400">Define a new system role and its permissions</p>
                </div>

                <form onSubmit={handleSubmit}>
                    <Card className="bg-slate-800/50 border-slate-700 mb-6">
                        <CardHeader>
                            <CardTitle className="text-white">Role Details</CardTitle>
                            <CardDescription>Basic information about the role</CardDescription>
                        </CardHeader>
                        <CardContent className="space-y-4">
                            <div className="space-y-2">
                                <Label htmlFor="name" className="text-slate-200">Role Name</Label>
                                <Input
                                    id="name"
                                    value={name}
                                    onChange={(e) => setName(e.target.value)}
                                    placeholder="e.g., Editor"
                                    className="bg-slate-800 border-slate-700 text-white"
                                />
                            </div>
                        </CardContent>
                    </Card>

                    <Card className="bg-slate-800/50 border-slate-700 mb-6">
                        <CardHeader>
                            <CardTitle className="text-white">Permissions</CardTitle>
                            <CardDescription>
                                Set granular access rights for each content type
                            </CardDescription>
                        </CardHeader>
                        <CardContent>
                            {schemas && (
                                <PermissionMatrix
                                    schemas={schemas}
                                    selectedPermissions={permissions}
                                    onChange={setPermissions}
                                />
                            )}
                            {(!schemas || schemas.length === 0) && (
                                <p className="text-slate-500 italic">No content types defined yet. Create a content type first to assign permissions.</p>
                            )}
                        </CardContent>
                    </Card>

                    {error && (
                        <div className="p-4 rounded-lg bg-red-500/10 border border-red-500/20 text-red-400 text-sm mb-6">
                            {error}
                        </div>
                    )}

                    <Separator className="my-6 bg-slate-700" />

                    <div className="flex items-center justify-between">
                        <Link href="/roles">
                            <Button variant="outline" type="button" className="border-slate-700 text-slate-300">
                                Cancel
                            </Button>
                        </Link>
                        <Button
                            type="submit"
                            className="bg-gradient-to-r from-amber-500 to-orange-600 hover:from-amber-600 hover:to-orange-700 text-white"
                            disabled={createRoleMutation.isPending}
                        >
                            {createRoleMutation.isPending ? 'Creating...' : 'Create Role'}
                        </Button>
                    </div>
                </form>
            </main>
        </div>
    );
}
