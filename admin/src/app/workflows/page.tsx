'use client';

import { useEffect } from 'react';
import Link from 'next/link';
import { useAuth } from '@/hooks/use-auth';
import { useWorkflows } from '@/hooks/use-workflows';
import { Header } from '@/components/header';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Skeleton } from '@/components/ui/skeleton';
import {
    Tooltip,
    TooltipContent,
    TooltipProvider,
    TooltipTrigger,
} from '@/components/ui/tooltip';
import {
    Breadcrumb,
    BreadcrumbItem,
    BreadcrumbLink,
    BreadcrumbList,
    BreadcrumbPage,
    BreadcrumbSeparator,
} from '@/components/ui/breadcrumb';
import { GitBranch, Plus, ChevronRight, Edit, Play, Zap, Settings } from 'lucide-react';

export default function WorkflowsPage() {
    const { isAuthenticated, isLoading: authLoading, requireAuth } = useAuth();
    const { data: workflows, isLoading } = useWorkflows();

    useEffect(() => {
        requireAuth();
    }, [requireAuth]);

    if (authLoading) {
        return (
            <div className="min-h-screen flex items-center justify-center bg-slate-900">
                <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-amber-500"></div>
            </div>
        );
    }

    if (!isAuthenticated) return null;

    return (
        <TooltipProvider>
            <div className="min-h-screen bg-gradient-to-br from-slate-900 via-slate-900 to-slate-800">
                <Header />
                <main className="container mx-auto px-4 py-8 max-w-5xl">
                    {/* Breadcrumb */}
                    <Breadcrumb className="mb-6">
                        <BreadcrumbList>
                            <BreadcrumbItem>
                                <BreadcrumbLink asChild>
                                    <Link href="/" className="text-slate-400 hover:text-white transition-colors">
                                        Dashboard
                                    </Link>
                                </BreadcrumbLink>
                            </BreadcrumbItem>
                            <BreadcrumbSeparator>
                                <ChevronRight className="h-4 w-4 text-slate-600" />
                            </BreadcrumbSeparator>
                            <BreadcrumbItem>
                                <BreadcrumbPage className="text-white font-medium">Workflows</BreadcrumbPage>
                            </BreadcrumbItem>
                        </BreadcrumbList>
                    </Breadcrumb>

                    {/* Page Header */}
                    <div className="flex items-center justify-between mb-8">
                        <div>
                            <h1 className="text-3xl font-bold text-white mb-2 flex items-center gap-3">
                                <div className="p-2 bg-gradient-to-br from-purple-500/20 to-pink-500/20 rounded-lg">
                                    <GitBranch className="h-6 w-6 text-purple-400" />
                                </div>
                                Workflows
                            </h1>
                            <p className="text-slate-400">Automate content lifecycle and approval processes</p>
                        </div>
                        <Link href="/workflows/new">
                            <Button className="bg-gradient-to-r from-purple-500 to-pink-600 hover:from-purple-600 hover:to-pink-700 text-white shadow-lg shadow-purple-500/20 transition-all duration-300 hover:shadow-purple-500/40 hover:scale-105">
                                <Plus className="mr-2 h-4 w-4" />
                                New Workflow
                            </Button>
                        </Link>
                    </div>

                    <div className="grid gap-6">
                        {isLoading ? (
                            // Skeleton Loading State
                            [...Array(3)].map((_, i) => (
                                <Card key={i} className="bg-slate-800/50 border-slate-700">
                                    <CardHeader>
                                        <div className="flex justify-between items-start">
                                            <div className="space-y-2">
                                                <Skeleton className="h-6 w-48 bg-slate-700" />
                                                <Skeleton className="h-4 w-32 bg-slate-700" />
                                            </div>
                                            <Skeleton className="h-8 w-16 bg-slate-700" />
                                        </div>
                                    </CardHeader>
                                    <CardContent>
                                        <div className="flex gap-2">
                                            <Skeleton className="h-5 w-20 bg-slate-700 rounded-full" />
                                            <Skeleton className="h-5 w-20 bg-slate-700 rounded-full" />
                                            <Skeleton className="h-5 w-20 bg-slate-700 rounded-full" />
                                        </div>
                                    </CardContent>
                                </Card>
                            ))
                        ) : workflows && workflows.length > 0 ? (
                            workflows.map((workflow) => (
                                <Card
                                    key={workflow.id}
                                    className="bg-slate-800/50 border-slate-700 backdrop-blur-sm shadow-xl hover:border-slate-600 transition-all duration-300 hover:shadow-2xl group"
                                >
                                    <CardHeader>
                                        <div className="flex justify-between items-start">
                                            <div>
                                                <CardTitle className="text-white text-xl mb-2 flex items-center gap-2">
                                                    <Zap className="h-5 w-5 text-purple-400" />
                                                    {workflow.name}
                                                </CardTitle>
                                                <CardDescription className="text-slate-400 flex items-center gap-2">
                                                    Applies to:
                                                    <Badge variant="outline" className="text-amber-400 border-amber-500/30 bg-amber-500/10">
                                                        {workflow.contentType}
                                                    </Badge>
                                                </CardDescription>
                                            </div>
                                            <div className="flex gap-1 opacity-0 group-hover:opacity-100 transition-opacity duration-200">
                                                <Tooltip>
                                                    <TooltipTrigger asChild>
                                                        <Button variant="ghost" size="sm" className="text-blue-400 hover:text-blue-300 hover:bg-blue-500/10">
                                                            <Play className="h-4 w-4" />
                                                        </Button>
                                                    </TooltipTrigger>
                                                    <TooltipContent side="top" className="bg-slate-800 border-slate-700">
                                                        <p>Test workflow</p>
                                                    </TooltipContent>
                                                </Tooltip>
                                                <Tooltip>
                                                    <TooltipTrigger asChild>
                                                        <Link href={`/workflows/${workflow.id}`}>
                                                            <Button variant="ghost" size="sm" className="text-slate-400 hover:text-white hover:bg-slate-700/50">
                                                                <Edit className="h-4 w-4" />
                                                            </Button>
                                                        </Link>
                                                    </TooltipTrigger>
                                                    <TooltipContent side="top" className="bg-slate-800 border-slate-700">
                                                        <p>Edit workflow</p>
                                                    </TooltipContent>
                                                </Tooltip>
                                                <Tooltip>
                                                    <TooltipTrigger asChild>
                                                        <Button variant="ghost" size="sm" className="text-slate-400 hover:text-white hover:bg-slate-700/50">
                                                            <Settings className="h-4 w-4" />
                                                        </Button>
                                                    </TooltipTrigger>
                                                    <TooltipContent side="top" className="bg-slate-800 border-slate-700">
                                                        <p>Settings</p>
                                                    </TooltipContent>
                                                </Tooltip>
                                            </div>
                                        </div>
                                    </CardHeader>
                                    <CardContent>
                                        <div className="flex items-center gap-4 text-sm text-slate-300">
                                            <div className="flex items-center gap-2 flex-wrap">
                                                <span className="text-slate-500">States:</span>
                                                {workflow.states?.map((state, index) => (
                                                    <div key={state.name} className="flex items-center">
                                                        <Badge variant="secondary" className="bg-slate-700/50 text-slate-300 border border-slate-600">
                                                            {state.displayName}
                                                        </Badge>
                                                        {index < (workflow.states?.length || 0) - 1 && (
                                                            <ChevronRight className="h-4 w-4 text-slate-600 mx-1" />
                                                        )}
                                                    </div>
                                                ))}
                                            </div>
                                        </div>
                                    </CardContent>
                                </Card>
                            ))
                        ) : (
                            // Enhanced Empty State
                            <Card className="bg-slate-800/50 border-slate-700 border-dashed backdrop-blur-sm">
                                <CardHeader className="text-center py-16">
                                    <div className="mx-auto w-20 h-20 bg-gradient-to-br from-purple-500/20 to-pink-600/20 rounded-2xl flex items-center justify-center mb-6">
                                        <GitBranch className="h-10 w-10 text-purple-400" />
                                    </div>
                                    <CardTitle className="text-white text-xl mb-2">No Workflows Yet</CardTitle>
                                    <CardDescription className="text-slate-400 max-w-md mx-auto">
                                        Workflows automate your content lifecycle. Create triggers, define states, and let the system handle approvals.
                                    </CardDescription>
                                    <div className="pt-6">
                                        <Link href="/workflows/new">
                                            <Button className="bg-gradient-to-r from-purple-500 to-pink-600 hover:from-purple-600 hover:to-pink-700 text-white shadow-lg shadow-purple-500/20 transition-all duration-300 hover:shadow-purple-500/40 hover:scale-105">
                                                <Plus className="mr-2 h-4 w-4" />
                                                Create Your First Workflow
                                            </Button>
                                        </Link>
                                    </div>
                                </CardHeader>
                            </Card>
                        )}
                    </div>
                </main>
            </div>
        </TooltipProvider>
    );
}
