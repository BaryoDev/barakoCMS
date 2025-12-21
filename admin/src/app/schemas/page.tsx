'use client';

import { useEffect } from 'react';
import Link from 'next/link';
import { useAuth } from '@/hooks/use-auth';
import { useSchemas } from '@/hooks/use-schemas';
import { Header } from '@/components/header';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
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
import { Layers, Plus, ChevronRight, Edit, Eye, Copy } from 'lucide-react';

export default function SchemasPage() {
    const { isAuthenticated, isLoading: authLoading, requireAuth } = useAuth();
    const { data: schemas, isLoading, error } = useSchemas();

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

    if (!isAuthenticated) {
        return null;
    }

    return (
        <TooltipProvider>
            <div className="min-h-screen bg-gradient-to-br from-slate-900 via-slate-900 to-slate-800">
                <Header />

                <main className="container mx-auto px-4 py-8 max-w-6xl">
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
                                <BreadcrumbPage className="text-white font-medium">Content Types</BreadcrumbPage>
                            </BreadcrumbItem>
                        </BreadcrumbList>
                    </Breadcrumb>

                    {/* Page Header */}
                    <div className="flex items-center justify-between mb-8">
                        <div>
                            <h1 className="text-3xl font-bold text-white mb-2 flex items-center gap-3">
                                <div className="p-2 bg-gradient-to-br from-cyan-500/20 to-blue-500/20 rounded-lg">
                                    <Layers className="h-6 w-6 text-cyan-400" />
                                </div>
                                Content Types
                            </h1>
                            <p className="text-slate-400">Define the structure of your content</p>
                        </div>
                        <Link href="/schemas/new">
                            <Button className="bg-gradient-to-r from-cyan-500 to-blue-600 hover:from-cyan-600 hover:to-blue-700 text-white shadow-lg shadow-cyan-500/20 transition-all duration-300 hover:shadow-cyan-500/40 hover:scale-105">
                                <Plus className="mr-2 h-4 w-4" />
                                New Content Type
                            </Button>
                        </Link>
                    </div>

                    {isLoading ? (
                        // Skeleton Loading State
                        <Card className="bg-slate-800/50 border-slate-700 backdrop-blur-sm">
                            <Table>
                                <TableHeader>
                                    <TableRow className="border-slate-700">
                                        <TableHead className="text-slate-400">Name</TableHead>
                                        <TableHead className="text-slate-400">Display Name</TableHead>
                                        <TableHead className="text-slate-400">Fields</TableHead>
                                        <TableHead className="text-slate-400">Status</TableHead>
                                        <TableHead className="text-slate-400 text-right">Actions</TableHead>
                                    </TableRow>
                                </TableHeader>
                                <TableBody>
                                    {[...Array(4)].map((_, i) => (
                                        <TableRow key={i} className="border-slate-700">
                                            <TableCell><Skeleton className="h-4 w-32 bg-slate-700" /></TableCell>
                                            <TableCell><Skeleton className="h-4 w-40 bg-slate-700" /></TableCell>
                                            <TableCell><Skeleton className="h-4 w-20 bg-slate-700" /></TableCell>
                                            <TableCell><Skeleton className="h-5 w-16 bg-slate-700 rounded-full" /></TableCell>
                                            <TableCell className="text-right"><Skeleton className="h-8 w-20 bg-slate-700 ml-auto" /></TableCell>
                                        </TableRow>
                                    ))}
                                </TableBody>
                            </Table>
                        </Card>
                    ) : error ? (
                        <Card className="bg-red-500/10 border-red-500/20 backdrop-blur-sm">
                            <CardContent className="py-12 text-center">
                                <div className="p-4 bg-red-500/10 rounded-full w-fit mx-auto mb-4">
                                    <Layers className="h-8 w-8 text-red-400" />
                                </div>
                                <h3 className="text-white font-medium mb-2">Failed to Load Content Types</h3>
                                <p className="text-red-400 text-sm">Please check your connection and try again.</p>
                            </CardContent>
                        </Card>
                    ) : schemas && schemas.length > 0 ? (
                        <>
                            {/* Desktop View */}
                            <Card className="hidden md:block bg-slate-800/50 border-slate-700 backdrop-blur-sm shadow-xl">
                                <Table>
                                    <TableHeader>
                                        <TableRow className="border-slate-700 hover:bg-slate-800/50">
                                            <TableHead className="text-slate-400">Name</TableHead>
                                            <TableHead className="text-slate-400">Display Name</TableHead>
                                            <TableHead className="text-slate-400">Fields</TableHead>
                                            <TableHead className="text-slate-400">Status</TableHead>
                                            <TableHead className="text-slate-400 text-right">Actions</TableHead>
                                        </TableRow>
                                    </TableHeader>
                                    <TableBody>
                                        {schemas.map((schema) => (
                                            <TableRow key={schema.name} className="border-slate-700 hover:bg-slate-700/30 transition-colors duration-150 group">
                                                <TableCell className="font-mono text-sm text-cyan-400">{schema.name}</TableCell>
                                                <TableCell className="text-white font-medium">{schema.displayName}</TableCell>
                                                <TableCell>
                                                    <Badge variant="secondary" className="bg-slate-700/50 text-slate-300 border border-slate-600">
                                                        {schema.fields?.length || 0} fields
                                                    </Badge>
                                                </TableCell>
                                                <TableCell>
                                                    <Badge variant="outline" className="border-emerald-500/50 text-emerald-400 bg-emerald-500/10">
                                                        Active
                                                    </Badge>
                                                </TableCell>
                                                <TableCell className="text-right">
                                                    <div className="flex items-center justify-end gap-1">
                                                        <Tooltip>
                                                            <TooltipTrigger asChild>
                                                                <Button variant="ghost" size="sm" className="text-slate-400 hover:text-white hover:bg-slate-700/50 transition-all duration-200">
                                                                    <Eye className="h-4 w-4" />
                                                                </Button>
                                                            </TooltipTrigger>
                                                            <TooltipContent side="top" className="bg-slate-800 border-slate-700">
                                                                <p>Preview schema</p>
                                                            </TooltipContent>
                                                        </Tooltip>
                                                        <Tooltip>
                                                            <TooltipTrigger asChild>
                                                                <Link href={`/schemas/${schema.name}`}>
                                                                    <Button variant="ghost" size="sm" className="text-blue-400 hover:text-blue-300 hover:bg-blue-500/10 transition-all duration-200">
                                                                        <Edit className="h-4 w-4" />
                                                                    </Button>
                                                                </Link>
                                                            </TooltipTrigger>
                                                            <TooltipContent side="top" className="bg-slate-800 border-slate-700">
                                                                <p>Edit schema</p>
                                                            </TooltipContent>
                                                        </Tooltip>
                                                        <Tooltip>
                                                            <TooltipTrigger asChild>
                                                                <Button variant="ghost" size="sm" className="text-slate-400 hover:text-white hover:bg-slate-700/50 transition-all duration-200">
                                                                    <Copy className="h-4 w-4" />
                                                                </Button>
                                                            </TooltipTrigger>
                                                            <TooltipContent side="top" className="bg-slate-800 border-slate-700">
                                                                <p>Duplicate</p>
                                                            </TooltipContent>
                                                        </Tooltip>
                                                    </div>
                                                </TableCell>
                                            </TableRow>
                                        ))}
                                    </TableBody>
                                </Table>
                            </Card>

                            {/* Mobile View */}
                            <div className="md:hidden space-y-4">
                                {schemas.map((schema) => (
                                    <Card key={schema.name} className="bg-slate-800/50 border-slate-700 backdrop-blur-sm">
                                        <CardHeader className="pb-2">
                                            <div className="flex justify-between items-start">
                                                <div>
                                                    <CardTitle className="text-slate-200 text-base font-medium">
                                                        {schema.displayName}
                                                    </CardTitle>
                                                    <CardDescription className="text-cyan-400 font-mono text-xs mt-1">
                                                        {schema.name}
                                                    </CardDescription>
                                                </div>
                                                <Badge variant="outline" className="border-emerald-500/50 text-emerald-400 bg-emerald-500/10">
                                                    Active
                                                </Badge>
                                            </div>
                                        </CardHeader>
                                        <CardContent className="pb-4">
                                            <div className="flex items-center gap-2 mb-4">
                                                <Badge variant="secondary" className="bg-slate-700/50 text-slate-300 border border-slate-600">
                                                    {schema.fields?.length || 0} fields
                                                </Badge>
                                            </div>
                                            <div className="flex justify-end gap-2">
                                                <Link href={`/schemas/${schema.name}`} className="flex-1">
                                                    <Button variant="outline" size="sm" className="w-full border-slate-600 text-slate-300 hover:text-white hover:bg-slate-700">
                                                        <Edit className="h-4 w-4 mr-2" />
                                                        Edit
                                                    </Button>
                                                </Link>
                                                <Button variant="outline" size="sm" className="flex-1 border-slate-600 text-slate-300 hover:text-white hover:bg-slate-700">
                                                    <Eye className="h-4 w-4 mr-2" />
                                                    Preview
                                                </Button>
                                            </div>
                                        </CardContent>
                                    </Card>
                                ))}
                            </div>
                        </>
                    ) : (
                        // Enhanced Empty State
                        <Card className="bg-slate-800/50 border-slate-700 border-dashed backdrop-blur-sm">
                            <CardHeader className="text-center py-16">
                                <div className="mx-auto w-20 h-20 bg-gradient-to-br from-cyan-500/20 to-blue-600/20 rounded-2xl flex items-center justify-center mb-6">
                                    <Layers className="h-10 w-10 text-cyan-400" />
                                </div>
                                <CardTitle className="text-white text-xl mb-2">No Content Types Yet</CardTitle>
                                <CardDescription className="text-slate-400 max-w-md mx-auto">
                                    Content Types define the structure of your content. Create fields, set validations, and start building.
                                </CardDescription>
                                <div className="pt-6">
                                    <Link href="/schemas/new">
                                        <Button className="bg-gradient-to-r from-cyan-500 to-blue-600 hover:from-cyan-600 hover:to-blue-700 text-white shadow-lg shadow-cyan-500/20 transition-all duration-300 hover:shadow-cyan-500/40 hover:scale-105">
                                            <Plus className="mr-2 h-4 w-4" />
                                            Create Your First Content Type
                                        </Button>
                                    </Link>
                                </div>
                            </CardHeader>
                        </Card>
                    )}
                </main>
            </div>
        </TooltipProvider>
    );
}
