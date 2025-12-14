'use client';

import { useEffect, useState, use } from 'react';
import { useRouter } from 'next/navigation';
import Link from 'next/link';
import { useAuth } from '@/hooks/use-auth';
import { useSchema, useUpdateSchema } from '@/hooks/use-schemas';
import { Header } from '@/components/header';
import { FieldEditor } from '@/components/schema/field-editor';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Separator } from '@/components/ui/separator';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import type { FieldDefinition, SensitivityLevel } from '@/types/schema';
import { toast } from 'sonner';

export default function EditSchemaPage({ params }: { params: Promise<{ name: string }> }) {
    const { name: schemaName } = use(params);
    const router = useRouter();
    const { isAuthenticated, isLoading: authLoading, requireAuth } = useAuth();
    const { data: schema, isLoading: schemaLoading, error: loadError } = useSchema(schemaName);
    const updateSchema = useUpdateSchema();

    const [displayName, setDisplayName] = useState('');
    const [description, setDescription] = useState('');
    const [sensitivity, setSensitivity] = useState<SensitivityLevel>('public');
    const [fields, setFields] = useState<FieldDefinition[]>([]);
    const [error, setError] = useState<string | null>(null);

    useEffect(() => {
        requireAuth();
    }, [requireAuth]);

    useEffect(() => {
        if (schema) {
            setDisplayName(schema.displayName);
            setDescription(schema.description || '');
            setSensitivity(schema.sensitivity || 'public');
            setFields(schema.fields || []);
        }
    }, [schema]);

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        setError(null);

        if (!displayName) {
            setError('Display Name is required');
            return;
        }

        if (fields.length === 0) {
            setError('At least one field is required');
            return;
        }

        try {
            await updateSchema.mutateAsync({
                name: schemaName,
                data: {
                    name: schemaName, // Name usually cannot be changed easily
                    displayName,
                    description,
                    sensitivity,
                    fields,
                },
            });
            toast.success('Schema updated successfully');
            router.push('/schemas');
        } catch (err: unknown) {
            if (err && typeof err === 'object' && 'response' in err) {
                const axiosError = err as { response?: { data?: { message?: string } } };
                setError(axiosError.response?.data?.message || 'Failed to update schema');
            } else {
                setError('Failed to update schema');
            }
        }
    };

    if (authLoading || schemaLoading) {
        return (
            <div className="min-h-screen bg-slate-900">
                <Header />
                <main className="container mx-auto px-4 py-8 max-w-4xl">
                    <div className="space-y-6">
                        <Skeleton className="h-8 w-64 bg-slate-800" />
                        <Card className="bg-slate-800/50 border-slate-700">
                            <CardHeader>
                                <Skeleton className="h-6 w-48 bg-slate-800 mb-2" />
                                <Skeleton className="h-4 w-96 bg-slate-800" />
                            </CardHeader>
                            <CardContent className="space-y-4">
                                <div className="grid grid-cols-2 gap-4">
                                    <Skeleton className="h-10 bg-slate-800" />
                                    <Skeleton className="h-10 bg-slate-800" />
                                </div>
                            </CardContent>
                        </Card>
                    </div>
                </main>
            </div>
        );
    }

    if (!isAuthenticated) {
        return null;
    }

    if (loadError) {
        return (
            <div className="min-h-screen bg-slate-900">
                <Header />
                <main className="container mx-auto px-4 py-8 max-w-4xl text-center">
                    <h2 className="text-xl text-red-400 mb-4">Failed to load schema</h2>
                    <Link href="/schemas">
                        <Button variant="outline">Back to Schemas</Button>
                    </Link>
                </main>
            </div>
        );
    }

    return (
        <div className="min-h-screen bg-slate-900">
            <Header />

            <main className="container mx-auto px-4 py-8 max-w-4xl">
                <div className="mb-8">
                    <Link href="/schemas" className="text-slate-400 hover:text-white text-sm flex items-center gap-2 mb-4">
                        ← Back to Content Types
                    </Link>
                    <h1 className="text-3xl font-bold text-white mb-2">Edit Content Type: {schemaName}</h1>
                    <p className="text-slate-400">Modify the structure of this content type</p>
                </div>

                <form onSubmit={handleSubmit}>
                    <Card className="bg-slate-800/50 border-slate-700 mb-6">
                        <CardHeader>
                            <CardTitle className="text-white">Basic Information</CardTitle>
                            <CardDescription className="text-slate-400">
                                Update the display name and description
                            </CardDescription>
                        </CardHeader>
                        <CardContent className="space-y-4">
                            <div className="grid grid-cols-2 gap-4">
                                <div className="space-y-2">
                                    <Label htmlFor="displayName" className="text-slate-200">Display Name</Label>
                                    <Input
                                        id="displayName"
                                        value={displayName}
                                        onChange={(e) => setDisplayName(e.target.value)}
                                        placeholder="e.g., Blog Post"
                                        className="bg-slate-800 border-slate-700 text-white"
                                    />
                                </div>
                                <div className="space-y-2">
                                    <Label htmlFor="name" className="text-slate-200">API Slug (Read Only)</Label>
                                    <Input
                                        id="name"
                                        value={schemaName}
                                        disabled
                                        className="bg-slate-800/50 border-slate-700 text-slate-400 font-mono cursor-not-allowed"
                                    />
                                </div>
                            </div>
                            <div className="space-y-2">
                                <Label htmlFor="description" className="text-slate-200">Description (optional)</Label>
                                <Input
                                    id="description"
                                    value={description}
                                    onChange={(e) => setDescription(e.target.value)}
                                    placeholder="A brief description of this content type"
                                    className="bg-slate-800 border-slate-700 text-white"
                                />
                            </div>
                            <div className="space-y-2">
                                <Label htmlFor="sensitivity" className="text-slate-200">Sensitivity Level</Label>
                                <Select value={sensitivity} onValueChange={(val) => setSensitivity(val as SensitivityLevel)}>
                                    <SelectTrigger className="bg-slate-800 border-slate-700 text-white">
                                        <SelectValue placeholder="Select sensitivity" />
                                    </SelectTrigger>
                                    <SelectContent className="bg-slate-800 border-slate-700">
                                        <SelectItem value="public" className="text-white">Public (Visible to everyone)</SelectItem>
                                        <SelectItem value="internal" className="text-white">Internal (Logged-in users only)</SelectItem>
                                        <SelectItem value="confidential" className="text-white">Confidential (Specific roles only)</SelectItem>
                                    </SelectContent>
                                </Select>
                            </div>
                        </CardContent>
                    </Card>

                    <Card className="bg-slate-800/50 border-slate-700 mb-6">
                        <CardHeader>
                            <CardTitle className="text-white">Schema Fields</CardTitle>
                            <CardDescription className="text-slate-400">
                                Update the fields for this content type
                            </CardDescription>
                        </CardHeader>
                        <CardContent>
                            <FieldEditor fields={fields} onChange={setFields} />
                        </CardContent>
                    </Card>

                    {error && (
                        <div className="p-4 rounded-lg bg-red-500/10 border border-red-500/20 text-red-400 text-sm mb-6">
                            {error}
                        </div>
                    )}

                    <Separator className="my-6 bg-slate-700" />

                    <div className="flex items-center justify-between">
                        <Link href="/schemas">
                            <Button variant="outline" type="button" className="border-slate-700 text-slate-300">
                                Cancel
                            </Button>
                        </Link>
                        <Button
                            type="submit"
                            className="bg-gradient-to-r from-amber-500 to-orange-600 hover:from-amber-600 hover:to-orange-700 text-white"
                            disabled={updateSchema.isPending}
                        >
                            {updateSchema.isPending ? 'Saving...' : 'Save Changes'}
                        </Button>
                    </div>
                </form>
            </main >
        </div >
    );
}
