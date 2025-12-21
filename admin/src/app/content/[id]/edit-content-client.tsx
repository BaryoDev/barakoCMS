'use client';

import { useEffect, useState } from 'react';
import { useRouter } from 'next/navigation';
import Link from 'next/link';
import { useAuth } from '@/hooks/use-auth';
import { useSchemas } from '@/hooks/use-schemas';
import { useContent, useUpdateContent } from '@/hooks/use-contents';
import { Header } from '@/components/header';
import { DynamicForm } from '@/components/content/dynamic-form';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Separator } from '@/components/ui/separator';
import { Label } from '@/components/ui/label';
import { ContentStatus } from '@/types/content';
import type { ContentTypeDefinition } from '@/types/schema';

export default function EditContentClient({ id }: { id: string }) {
    const router = useRouter();
    const { isAuthenticated, isLoading: authLoading, requireAuth } = useAuth();
    const { data: schemas, isLoading: schemasLoading } = useSchemas();
    const { data: content, isLoading: contentLoading } = useContent(id);
    const updateContent = useUpdateContent();

    const [selectedSchema, setSelectedSchema] = useState<ContentTypeDefinition | null>(null);
    const [formData, setFormData] = useState<Record<string, unknown>>({});
    const [status, setStatus] = useState<ContentStatus>(ContentStatus.Draft);
    const [error, setError] = useState<string | null>(null);
    const [validationErrors, setValidationErrors] = useState<Record<string, string>>({});

    useEffect(() => {
        requireAuth();
    }, [requireAuth]);

    useEffect(() => {
        if (content && schemas) {
            const schema = schemas.find(s => s.name === content.contentType);
            if (schema) {
                setSelectedSchema(schema);
                setFormData(content.data || {});
                setStatus(content.status);
            }
        }
    }, [content, schemas]);

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

        if (!validateForm()) {
            return;
        }

        try {
            await updateContent.mutateAsync({
                id: id,
                data: {
                    data: formData,
                    status,
                },
            });
            router.push('/content');
        } catch (err: unknown) {
            if (err && typeof err === 'object' && 'response' in err) {
                const axiosError = err as { response?: { data?: { message?: string } } };
                setError(axiosError.response?.data?.message || 'Failed to update content');
            } else {
                setError('Failed to update content');
            }
        }
    };

    if (authLoading || schemasLoading || contentLoading) {
        return (
            <div className="min-h-screen flex items-center justify-center bg-slate-900">
                <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-amber-500"></div>
            </div>
        );
    }

    if (!isAuthenticated) {
        return null;
    }

    if (!content) {
        return (
            <div className="min-h-screen bg-slate-900 flex items-center justify-center text-white">
                Content not found.
            </div>
        );
    }

    return (
        <div className="min-h-screen bg-slate-900">
            <Header />

            <main className="container mx-auto px-4 py-8 max-w-4xl">
                <div className="mb-8">
                    <Link href="/content" className="text-slate-400 hover:text-white text-sm flex items-center gap-2 mb-4">
                        ← Back to Content
                    </Link>
                    <h1 className="text-3xl font-bold text-white mb-2">Edit Content</h1>
                    <p className="text-slate-400">Editing {content.contentType}</p>
                </div>

                <form onSubmit={handleSubmit}>
                    {/* Content Type Display (Read Only) */}
                    <Card className="bg-slate-800/50 border-slate-700 mb-6">
                        <CardHeader>
                            <CardTitle className="text-white">Content Type</CardTitle>
                        </CardHeader>
                        <CardContent>
                            <div className="p-3 bg-slate-800 rounded border border-slate-700 text-slate-300">
                                {selectedSchema?.displayName || content.contentType}
                            </div>
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
                            disabled={updateContent.isPending}
                        >
                            {updateContent.isPending ? 'Saving...' : 'Save Changes'}
                        </Button>
                    </div>
                </form >
            </main >
        </div >
    );
}
