'use client';

import { use } from 'react';
import { useRouter } from 'next/navigation';
import Link from 'next/link';
import { useAuth } from '@/hooks/use-auth';
import { useWorkflow } from '@/hooks/use-workflows';
import { Header } from '@/components/header';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';

export default function EditWorkflowPage({ params }: { params: Promise<{ id: string }> }) {
    // Correct Next.js 15 async params unwrapping
    const { id } = use(params);
    const { isAuthenticated, isLoading: authLoading, requireAuth } = useAuth();
    const { data: workflow, isLoading: workflowLoading } = useWorkflow(id);

    if (authLoading || workflowLoading) {
        return (
            <div className="min-h-screen flex items-center justify-center bg-slate-900">
                <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-amber-500"></div>
            </div>
        );
    }

    if (!workflow) {
        return (
            <div className="min-h-screen bg-slate-900 flex items-center justify-center text-white">
                Workflow not found
            </div>
        );
    }

    return (
        <div className="min-h-screen bg-slate-900">
            <Header />
            <main className="container mx-auto px-4 py-8 max-w-4xl">
                <div className="mb-8">
                    <Link href="/workflows" className="text-slate-400 hover:text-white text-sm flex items-center gap-2 mb-4">
                        ← Back to Workflows
                    </Link>
                    <h1 className="text-3xl font-bold text-white mb-2">Edit Workflow</h1>
                    <p className="text-slate-400">Editing {workflow.name}</p>
                </div>

                <Card className="bg-slate-800/50 border-slate-700">
                    <CardHeader>
                        <CardTitle className="text-white">Workflow Details</CardTitle>
                    </CardHeader>
                    <CardContent className="space-y-4">
                        <div>
                            <span className="text-slate-400 block text-sm">Target Content Type</span>
                            <span className="text-white font-medium">{workflow.contentType}</span>
                        </div>

                        <div>
                            <span className="text-slate-400 block text-sm mb-2">States</span>
                            <div className="flex gap-2">
                                {workflow.states.map(state => (
                                    <Badge key={state.name} variant="secondary" className="bg-slate-700">
                                        {state.displayName}
                                    </Badge>
                                ))}
                            </div>
                        </div>
                    </CardContent>
                </Card>

                <div className="mt-8 p-4 bg-amber-500/10 border border-amber-500/20 rounded-lg text-amber-200 text-sm">
                    Editing workflows is currently read-only in this demo.
                </div>
            </main>
        </div>
    );
}
