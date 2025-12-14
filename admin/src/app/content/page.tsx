'use client';

import { useEffect, useState } from 'react';
import Link from 'next/link';
import { useAuth } from '@/hooks/use-auth';
import { useSchemas } from '@/hooks/use-schemas';
import { useContents } from '@/hooks/use-contents';
import { Header } from '@/components/header';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Badge } from '@/components/ui/badge';
import { Skeleton } from '@/components/ui/skeleton';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Input } from '@/components/ui/input';
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
import { STATUS_LABELS, ContentStatus } from '@/types/content';
import { FileText, Plus, ChevronRight, Search, Edit, Eye } from 'lucide-react';

export default function ContentPage() {
    const { isAuthenticated, isLoading: authLoading, requireAuth } = useAuth();
    const { data: schemas } = useSchemas();
    const [selectedType, setSelectedType] = useState<string>('ALL');
    const [searchQuery, setSearchQuery] = useState('');
    const { data: contents, isLoading } = useContents(selectedType === 'ALL' ? undefined : selectedType);

    useEffect(() => {
        requireAuth();
    }, [requireAuth]);

    // Filter contents by search query
    const filteredContents = contents?.filter(content => {
        if (!searchQuery) return true;
        return content.id.toLowerCase().includes(searchQuery.toLowerCase()) ||
            content.contentType.toLowerCase().includes(searchQuery.toLowerCase());
    });

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
                                <BreadcrumbPage className="text-white font-medium">Content</BreadcrumbPage>
                            </BreadcrumbItem>
                        </BreadcrumbList>
                    </Breadcrumb>

                    {/* Page Header */}
                    <div className="flex items-center justify-between mb-8">
                        <div>
                            <h1 className="text-3xl font-bold text-white mb-2 flex items-center gap-3">
                                <div className="p-2 bg-gradient-to-br from-amber-500/20 to-orange-500/20 rounded-lg">
                                    <FileText className="h-6 w-6 text-amber-400" />
                                </div>
                                Content
                            </h1>
                            <p className="text-slate-400">Manage your content entries</p>
                        </div>
                        <Link href="/content/new">
                            <Button className="bg-gradient-to-r from-amber-500 to-orange-600 hover:from-amber-600 hover:to-orange-700 text-white shadow-lg shadow-amber-500/20 transition-all duration-300 hover:shadow-amber-500/40 hover:scale-105">
                                <Plus className="mr-2 h-4 w-4" />
                                New Content
                            </Button>
                        </Link>
                    </div>

                    {/* Filters */}
                    <div className="flex gap-4 mb-6">
                        <div className="relative flex-1 max-w-md">
                            <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
                            <Input
                                placeholder="Search by ID or type..."
                                value={searchQuery}
                                onChange={(e) => setSearchQuery(e.target.value)}
                                className="pl-10 bg-slate-800 border-slate-700 text-white placeholder:text-slate-500 focus:border-amber-500 transition-colors"
                            />
                        </div>
                        <Select value={selectedType} onValueChange={setSelectedType}>
                            <SelectTrigger className="w-64 bg-slate-800 border-slate-700 text-white">
                                <SelectValue placeholder="All Content Types" />
                            </SelectTrigger>
                            <SelectContent className="bg-slate-800 border-slate-700">
                                <SelectItem value="ALL" className="text-white hover:bg-slate-700">All Content Types</SelectItem>
                                {schemas?.map((schema) => (
                                    <SelectItem key={schema.name} value={schema.name} className="text-white hover:bg-slate-700">
                                        {schema.displayName}
                                    </SelectItem>
                                ))}
                            </SelectContent>
                        </Select>
                    </div>

                    {isLoading ? (
                        // Skeleton Loading State
                        <Card className="bg-slate-800/50 border-slate-700 backdrop-blur-sm">
                            <Table>
                                <TableHeader>
                                    <TableRow className="border-slate-700">
                                        <TableHead className="text-slate-400">ID</TableHead>
                                        <TableHead className="text-slate-400">Type</TableHead>
                                        <TableHead className="text-slate-400">Status</TableHead>
                                        <TableHead className="text-slate-400">Created</TableHead>
                                        <TableHead className="text-slate-400 text-right">Actions</TableHead>
                                    </TableRow>
                                </TableHeader>
                                <TableBody>
                                    {[...Array(5)].map((_, i) => (
                                        <TableRow key={i} className="border-slate-700">
                                            <TableCell><Skeleton className="h-4 w-24 bg-slate-700" /></TableCell>
                                            <TableCell><Skeleton className="h-4 w-32 bg-slate-700" /></TableCell>
                                            <TableCell><Skeleton className="h-5 w-16 bg-slate-700 rounded-full" /></TableCell>
                                            <TableCell><Skeleton className="h-4 w-24 bg-slate-700" /></TableCell>
                                            <TableCell className="text-right"><Skeleton className="h-8 w-16 bg-slate-700 ml-auto" /></TableCell>
                                        </TableRow>
                                    ))}
                                </TableBody>
                            </Table>
                        </Card>
                    ) : filteredContents && filteredContents.length > 0 ? (
                        <>
                            {/* Desktop View */}
                            <Card className="hidden md:block bg-slate-800/50 border-slate-700 backdrop-blur-sm shadow-xl">
                                <Table>
                                    <TableHeader>
                                        <TableRow className="border-slate-700 hover:bg-slate-800/50">
                                            <TableHead className="text-slate-400">ID</TableHead>
                                            <TableHead className="text-slate-400">Type</TableHead>
                                            <TableHead className="text-slate-400">Status</TableHead>
                                            <TableHead className="text-slate-400">Created</TableHead>
                                            <TableHead className="text-slate-400 text-right">Actions</TableHead>
                                        </TableRow>
                                    </TableHeader>
                                    <TableBody>
                                        {filteredContents.map((content) => {
                                            const statusInfo = STATUS_LABELS[content.status as ContentStatus] || STATUS_LABELS[ContentStatus.Draft];
                                            return (
                                                <TableRow key={content.id} className="border-slate-700 hover:bg-slate-700/30 transition-colors duration-150">
                                                    <TableCell className="font-mono text-sm text-slate-300">
                                                        {content.id.substring(0, 8)}...
                                                    </TableCell>
                                                    <TableCell>
                                                        <Badge variant="secondary" className="bg-amber-500/10 text-amber-400 border border-amber-500/20">
                                                            {content.contentType}
                                                        </Badge>
                                                    </TableCell>
                                                    <TableCell>
                                                        <Badge variant="outline" className={statusInfo.color}>
                                                            {statusInfo.label}
                                                        </Badge>
                                                    </TableCell>
                                                    <TableCell className="text-slate-400">
                                                        {new Date(content.createdAt).toLocaleDateString()}
                                                    </TableCell>
                                                    <TableCell className="text-right">
                                                        <div className="flex items-center justify-end gap-1">
                                                            <Tooltip>
                                                                <TooltipTrigger asChild>
                                                                    <Link href={`/content/${content.id}`}>
                                                                        <Button variant="ghost" size="sm" className="text-blue-400 hover:text-blue-300 hover:bg-blue-500/10 transition-all duration-200">
                                                                            <Edit className="h-4 w-4" />
                                                                        </Button>
                                                                    </Link>
                                                                </TooltipTrigger>
                                                                <TooltipContent side="top" className="bg-slate-800 border-slate-700">
                                                                    <p>Edit content</p>
                                                                </TooltipContent>
                                                            </Tooltip>
                                                            <Tooltip>
                                                                <TooltipTrigger asChild>
                                                                    <Button variant="ghost" size="sm" className="text-slate-400 hover:text-white hover:bg-slate-700/50 transition-all duration-200">
                                                                        <Eye className="h-4 w-4" />
                                                                    </Button>
                                                                </TooltipTrigger>
                                                                <TooltipContent side="top" className="bg-slate-800 border-slate-700">
                                                                    <p>Preview</p>
                                                                </TooltipContent>
                                                            </Tooltip>
                                                        </div>
                                                    </TableCell>
                                                </TableRow>
                                            );
                                        })}
                                    </TableBody>
                                </Table>

                                {/* Pagination Placeholder */}
                                {filteredContents.length > 10 && (
                                    <CardContent className="border-t border-slate-700 py-4">
                                        <div className="flex items-center justify-between">
                                            <p className="text-sm text-slate-400">
                                                Showing {filteredContents.length} entries
                                            </p>
                                            <div className="flex gap-2">
                                                <Button variant="outline" size="sm" disabled className="border-slate-700 text-slate-400">
                                                    Previous
                                                </Button>
                                                <Button variant="outline" size="sm" disabled className="border-slate-700 text-slate-400">
                                                    Next
                                                </Button>
                                            </div>
                                        </div>
                                    </CardContent>
                                )}
                            </Card>

                            {/* Mobile View */}
                            <div className="md:hidden space-y-4">
                                {filteredContents.map((content) => {
                                    const statusInfo = STATUS_LABELS[content.status as ContentStatus] || STATUS_LABELS[ContentStatus.Draft];
                                    return (
                                        <Card key={content.id} className="bg-slate-800/50 border-slate-700 backdrop-blur-sm">
                                            <CardHeader className="pb-2">
                                                <div className="flex justify-between items-start">
                                                    <div>
                                                        <CardTitle className="text-slate-200 text-base font-mono">
                                                            {content.id.substring(0, 8)}...
                                                        </CardTitle>
                                                        <CardDescription className="text-slate-400 mt-1">
                                                            {new Date(content.createdAt).toLocaleDateString()}
                                                        </CardDescription>
                                                    </div>
                                                    <Badge variant="outline" className={statusInfo.color}>
                                                        {statusInfo.label}
                                                    </Badge>
                                                </div>
                                            </CardHeader>
                                            <CardContent className="pb-4">
                                                <div className="flex items-center gap-2 mb-4">
                                                    <Badge variant="secondary" className="bg-amber-500/10 text-amber-400 border border-amber-500/20">
                                                        {content.contentType}
                                                    </Badge>
                                                </div>
                                                <div className="flex justify-end gap-2">
                                                    <Link href={`/content/${content.id}`} className="flex-1">
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
                                    );
                                })}
                            </div>
                        </>
                    ) : (
                        // Enhanced Empty State
                        <Card className="bg-slate-800/50 border-slate-700 backdrop-blur-sm">
                            <CardHeader className="text-center py-16">
                                <div className="mx-auto w-20 h-20 bg-gradient-to-br from-amber-500/20 to-orange-600/20 rounded-2xl flex items-center justify-center mb-6">
                                    <FileText className="h-10 w-10 text-amber-400" />
                                </div>
                                <CardTitle className="text-white text-xl mb-2">No Content Yet</CardTitle>
                                <CardDescription className="text-slate-400 max-w-md mx-auto">
                                    {schemas && schemas.length > 0
                                        ? "Create your first content entry to get started. Your content will appear here."
                                        : "Create a Content Type first, then you can add content entries."}
                                </CardDescription>
                                <div className="pt-6">
                                    {schemas && schemas.length > 0 ? (
                                        <Link href="/content/new">
                                            <Button className="bg-gradient-to-r from-amber-500 to-orange-600 hover:from-amber-600 hover:to-orange-700 text-white shadow-lg shadow-amber-500/20 transition-all duration-300 hover:shadow-amber-500/40 hover:scale-105">
                                                <Plus className="mr-2 h-4 w-4" />
                                                Create Your First Content
                                            </Button>
                                        </Link>
                                    ) : (
                                        <Link href="/schemas/new">
                                            <Button className="bg-gradient-to-r from-blue-500 to-cyan-600 hover:from-blue-600 hover:to-cyan-700 text-white shadow-lg shadow-blue-500/20">
                                                <Plus className="mr-2 h-4 w-4" />
                                                Create Content Type First
                                            </Button>
                                        </Link>
                                    )}
                                </div>
                            </CardHeader>
                        </Card>
                    )}
                </main>
            </div>
        </TooltipProvider>
    );
}
