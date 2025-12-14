'use client';

import { useEffect, useState } from 'react';
import { useRouter } from 'next/navigation';
import Link from 'next/link';
import { useAuth } from '@/hooks/use-auth';
import { useCreateWorkflow } from '@/hooks/use-workflows';
import { useSchemas } from '@/hooks/use-schemas';
import { Header } from '@/components/header';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Separator } from '@/components/ui/separator';
import { Plus, Trash2 } from 'lucide-react';
import type { WorkflowState, WorkflowTransition } from '@/types/workflow';

export default function CreateWorkflowPage() {
    const router = useRouter();
    const { isAuthenticated, isLoading: authLoading, requireAuth } = useAuth();
    const { data: schemas } = useSchemas();
    const createWorkflow = useCreateWorkflow();

    const [name, setName] = useState('');
    const [contentType, setContentType] = useState('');

    // Default steps
    const [states, setStates] = useState<WorkflowState[]>([
        { name: 'draft', displayName: 'Draft', color: 'gray', isInitial: true },
        { name: 'published', displayName: 'Published', color: 'green', isFinal: true }
    ]);

    const [transitions, setTransitions] = useState<WorkflowTransition[]>([
        { fromState: 'draft', toState: 'published', trigger: 'publish', allowedRoles: ['admin'] }
    ]);

    const [error, setError] = useState<string | null>(null);

    useEffect(() => {
        requireAuth();
    }, [requireAuth]);

    const handleAddState = () => {
        setStates([...states, { name: '', displayName: '', color: 'blue' }]);
    };

    const handleRemoveState = (index: number) => {
        if (states.length <= 2) return; // Keep at least 2 states
        const newStates = [...states];
        newStates.splice(index, 1);
        setStates(newStates);
    };

    const handleStateChange = (index: number, field: keyof WorkflowState, value: string) => {
        const newStates = [...states];
        newStates[index] = { ...newStates[index], [field]: value };
        setStates(newStates);
    };

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        setError(null);

        if (!name || !contentType) {
            setError('Name and Content Type are required');
            return;
        }

        try {
            await createWorkflow.mutateAsync({
                name,
                contentType,
                states,
                transitions
            });
            router.push('/workflows');
        } catch (err: any) {
            setError(err?.response?.data?.message || 'Failed to create workflow');
        }
    };

    if (authLoading) return null;
    if (!isAuthenticated) return null;

    return (
        <div className="min-h-screen bg-slate-900">
            <Header />
            <main className="container mx-auto px-4 py-8 max-w-4xl">
                <div className="mb-8">
                    <Link href="/workflows" className="text-slate-400 hover:text-white text-sm flex items-center gap-2 mb-4">
                        ← Back to Workflows
                    </Link>
                    <h1 className="text-3xl font-bold text-white mb-2">Create Workflow</h1>
                    <p className="text-slate-400">Define approval process</p>
                </div>

                <form onSubmit={handleSubmit} className="space-y-6">
                    <Card className="bg-slate-800/50 border-slate-700">
                        <CardHeader>
                            <CardTitle className="text-white">Basic Info</CardTitle>
                        </CardHeader>
                        <CardContent className="space-y-4">
                            <div className="space-y-2">
                                <Label className="text-slate-200">Workflow Name</Label>
                                <Input
                                    value={name}
                                    onChange={(e) => setName(e.target.value)}
                                    placeholder="e.g. Blog Post Approval"
                                    className="bg-slate-900 border-slate-700 text-white"
                                />
                            </div>

                            <div className="space-y-2">
                                <Label className="text-slate-200">Content Type</Label>
                                <Select value={contentType} onValueChange={setContentType}>
                                    <SelectTrigger className="bg-slate-900 border-slate-700 text-white">
                                        <SelectValue placeholder="Select content type" />
                                    </SelectTrigger>
                                    <SelectContent className="bg-slate-800 border-slate-700">
                                        {schemas?.map(schema => (
                                            <SelectItem key={schema.name} value={schema.name} className="text-white">
                                                {schema.displayName}
                                            </SelectItem>
                                        ))}
                                    </SelectContent>
                                </Select>
                            </div>
                        </CardContent>
                    </Card>

                    <Card className="bg-slate-800/50 border-slate-700">
                        <CardHeader className="flex flex-row items-center justify-between">
                            <CardTitle className="text-white">States</CardTitle>
                            <Button type="button" onClick={handleAddState} variant="outline" size="sm" className="border-slate-600 text-slate-300">
                                <Plus className="h-4 w-4 mr-2" /> Add State
                            </Button>
                        </CardHeader>
                        <CardContent className="space-y-4">
                            {states.map((state, index) => (
                                <div key={index} className="flex gap-4 items-end bg-slate-900/50 p-4 rounded-lg border border-slate-700">
                                    <div className="flex-1 space-y-2">
                                        <Label className="text-xs text-slate-400">System Name</Label>
                                        <Input
                                            value={state.name}
                                            onChange={(e) => handleStateChange(index, 'name', e.target.value)}
                                            className="bg-slate-800 border-slate-700 text-white h-8 text-sm"
                                            placeholder="draft"
                                        />
                                    </div>
                                    <div className="flex-1 space-y-2">
                                        <Label className="text-xs text-slate-400">Display Name</Label>
                                        <Input
                                            value={state.displayName}
                                            onChange={(e) => handleStateChange(index, 'displayName', e.target.value)}
                                            className="bg-slate-800 border-slate-700 text-white h-8 text-sm"
                                            placeholder="Draft"
                                        />
                                    </div>
                                    <div className="w-10">
                                        {index > 1 && (
                                            <Button type="button" onClick={() => handleRemoveState(index)} variant="ghost" size="icon" className="text-red-400 hover:text-red-300 hover:bg-red-400/10">
                                                <Trash2 className="h-4 w-4" />
                                            </Button>
                                        )}
                                    </div>
                                </div>
                            ))}
                        </CardContent>
                    </Card>

                    {error && (
                        <div className="p-4 rounded-lg bg-red-500/10 border border-red-500/20 text-red-400 text-sm">
                            {error}
                        </div>
                    )}

                    <div className="flex justify-end gap-4">
                        <Link href="/workflows">
                            <Button variant="outline" type="button" className="border-slate-700 text-slate-300">
                                Cancel
                            </Button>
                        </Link>
                        <Button
                            type="submit"
                            className="bg-gradient-to-r from-amber-500 to-orange-600 hover:from-amber-600 hover:to-orange-700 text-white"
                            disabled={createWorkflow.isPending}
                        >
                            {createWorkflow.isPending ? 'Creating...' : 'Create Workflow'}
                        </Button>
                    </div>
                </form>
            </main>
        </div>
    );
}
