'use client';

import { useEffect, useState } from 'react';
import { useRouter } from 'next/navigation';
import Link from 'next/link';
import { useAuth } from '@/hooks/use-auth';
import { useSchemas } from '@/hooks/use-schemas';
import { useCreateContent } from '@/hooks/use-contents';
import { Header } from '@/components/header';
import { DynamicForm } from '@/components/content/dynamic-form';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Separator } from '@/components/ui/separator';
import { Label } from '@/components/ui/label';
import { ContentStatus } from '@/types/content';
import type { ContentTypeDefinition } from '@/types/schema';

export default function NewContentPage() {
    const router = useRouter();
    const { isAuthenticated, isLoading: authLoading, requireAuth } = useAuth();
    const { data: schemas, isLoading: schemasLoading } = useSchemas();
    const createContent = useCreateContent();

    const [selectedType, setSelectedType] = useState<string>('');
    const [selectedSchema, setSelectedSchema] = useState<ContentTypeDefinition | null>(null);
    const [formData, setFormData] = useState<Record<string, unknown>>({});
    const [status, setStatus] = useState<ContentStatus>(ContentStatus.Draft);
    const [error, setError] = useState<string | null>(null);
    const [validationErrors, setValidationErrors] = useState<Record<string, string>>({});

    useEffect(() => {
        requireAuth();
    }, [requireAuth]);

    useEffect(() => {
        if (selectedType && schemas) {
            const schema = schemas.find(s => s.name === selectedType);
            setSelectedSchema(schema || null);
            setFormData({});
            setValidationErrors({});
        }
    }, [selectedType, schemas]);

    const validateForm = (): boolean => {
        if (!selectedSchema) return false;

        const errors: Record<string, string> = {};
        for (const field of selectedSchema.fields || []) {
            if (field.isRequired) {
                const value = formData[field.name];
                if (value === undefined || value === null || value === '') {
                    errors[field.name] = `${field.displayName} is required`;
                }
            }
        }

        setValidationErrors(errors);
        return Object.keys(errors).length === 0;
    };

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        setError(null);

        if (!selectedType) {
            setError('Please select a content type');
            return;
        }

        if (!validateForm()) {
            return;
        }

        try {
            await createContent.mutateAsync({
                contentType: selectedType,
                data: formData,
                status,
            });
            router.push('/content');
        } catch (err: unknown) {
            if (err && typeof err === 'object' && 'response' in err) {
                const axiosError = err as { response?: { data?: { message?: string } } };
                setError(axiosError.response?.data?.message || 'Failed to create content');
            } else {
                setError('Failed to create content');
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

    if (!isAuthenticated) {
        return null;
    }

    return (
        <div className="min-h-screen bg-slate-900">
            <Header />

            <main className="container mx-auto px-4 py-8 max-w-4xl">
                <div className="mb-8">
                    <Link href="/content" className="text-slate-400 hover:text-white text-sm flex items-center gap-2 mb-4">
                        ← Back to Content
                    </Link>
                    <h1 className="text-3xl font-bold text-white mb-2">New Content</h1>
                    <p className="text-slate-400">Create a new content entry</p>
                </div>

                <form onSubmit={handleSubmit}>
                    {/* Content Type Selection */}
                    <Card className="bg-slate-800/50 border-slate-700 mb-6">
                        <CardHeader>
                            <CardTitle className="text-white">Content Type</CardTitle>
                            <CardDescription className="text-slate-400">
                                Select the type of content you want to create
                            </CardDescription>
                        </CardHeader>
                        <CardContent>
                            <Select value={selectedType} onValueChange={setSelectedType}>
                                <SelectTrigger className="w-full bg-slate-800 border-slate-700 text-white">
                                    <SelectValue placeholder="Select a content type" />
                                </SelectTrigger>
                                <SelectContent className="bg-slate-800 border-slate-700">
                                    {schemas?.map((schema) => (
                                        <SelectItem key={schema.name} value={schema.name} className="text-white">
                                            {schema.displayName}
                                        </SelectItem>
                                    ))}
                                </SelectContent>
                            </Select>
                            {schemas?.length === 0 && (
                                <p className="text-slate-500 text-sm mt-2">
                                    No content types available. <Link href="/schemas/new" className="text-amber-400 hover:underline">Create one first</Link>.
                                </p>
                            )}
                        </CardContent>
                    </Card>

                    {/* Dynamic Form */}
                    {selectedSchema && (
                        <Card className="bg-slate-800/50 border-slate-700 mb-6">
                            <CardHeader>
                                <CardTitle className="text-white">{selectedSchema.displayName} Fields</CardTitle>
                                <CardDescription className="text-slate-400">
                                    Fill in the content fields
                                </CardDescription>
                            </CardHeader>
                            <CardContent>
                                <DynamicForm
                                    fields={selectedSchema.fields || []}
                                    values={formData}
                                    onChange={setFormData}
                                    errors={validationErrors}
                                />
                            </CardContent>
                        </Card>
                    )}

                    {/* Status */}
                    {selectedSchema && (
                        <Card className="bg-slate-800/50 border-slate-700 mb-6">
                            <CardHeader>
                                <CardTitle className="text-white">Publishing Options</CardTitle>
                            </CardHeader>
                            <CardContent>
                                <div className="space-y-2 mb-4">
                                    <Label className="text-slate-200">Status</Label>
                                    <Select value={status.toString()} onValueChange={(v) => setStatus(parseInt(v) as ContentStatus)}>
                                        <SelectTrigger className="w-48 bg-slate-800 border-slate-700 text-white">
                                            <SelectValue />
                                        </SelectTrigger>
                                        <SelectContent className="bg-slate-800 border-slate-700">
                                            <SelectItem value="0" className="text-white">Draft</SelectItem>
                                            <SelectItem value="1" className="text-white">Published</SelectItem>
                                        </SelectContent>
                                    </Select>
                                </div>
                                {/* Sensitivity removed - inherited from Content Type */}
                            </CardContent>
                        </Card>
                    )}

                    {error && (
                        <div className="p-4 rounded-lg bg-red-500/10 border border-red-500/20 text-red-400 text-sm mb-6">
                            {error}
                        </div>
                    )}

                    <Separator className="my-6 bg-slate-700" />

                    <div className="flex items-center justify-between">
                        <Link href="/content">
                            <Button variant="outline" type="button" className="border-slate-700 text-slate-300">
                                Cancel
                            </Button>
                        </Link>
                        <Button
                            type="submit"
                            className="bg-gradient-to-r from-amber-500 to-orange-600 hover:from-amber-600 hover:to-orange-700 text-white"
                            disabled={!selectedType || createContent.isPending}
                        >
                            {createContent.isPending ? 'Creating...' : 'Create Content'}
                        </Button>
                    </div>
                </form >
            </main >
        </div >
    );
}
