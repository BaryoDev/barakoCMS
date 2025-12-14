'use client';

import { useEffect } from 'react';
import { useAuth } from '@/hooks/use-auth';
import { Header } from '@/components/header';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { useKubernetesStatus, useHealthStatus, useMetrics } from '@/hooks/use-monitoring';

export default function HealthPage() {
    const { isAuthenticated, requireAuth } = useAuth();

    const { data: k8sStatus } = useKubernetesStatus();
    const { data: healthStatus } = useHealthStatus();
    const { data: metrics } = useMetrics();

    useEffect(() => {
        requireAuth();
    }, [requireAuth]);

    if (!isAuthenticated) return null;

    return (
        <div className="min-h-screen bg-slate-900">
            <Header />
            <main className="container mx-auto px-4 py-8 max-w-6xl">
                <h1 className="text-3xl font-bold text-white mb-6">System Health</h1>

                <div className="grid grid-cols-1 md:grid-cols-3 gap-6 mb-8">
                    <Card className="bg-slate-800/50 border-slate-700">
                        <CardHeader className="pb-2">
                            <CardTitle className="text-sm font-medium text-slate-400">Total Requests</CardTitle>
                        </CardHeader>
                        <CardContent>
                            <div className="text-2xl font-bold text-white">
                                {metrics?.totalRequests.toLocaleString() ?? '...'}
                            </div>
                            <p className="text-xs text-slate-400 mt-1">Since last restart</p>
                        </CardContent>
                    </Card>
                    <Card className="bg-slate-800/50 border-slate-700">
                        <CardHeader className="pb-2">
                            <CardTitle className="text-sm font-medium text-slate-400">Error Rate</CardTitle>
                        </CardHeader>
                        <CardContent>
                            <div className="text-2xl font-bold text-white">
                                {metrics ? `${(metrics.errorRate * 100).toFixed(2)}%` : '...'}
                            </div>
                            <p className="text-xs text-green-400 mt-1">
                                {metrics && metrics.errorRate < 0.01 ? 'Within SLA' : 'Monitoring'}
                            </p>
                        </CardContent>
                    </Card>
                    <Card className="bg-slate-800/50 border-slate-700">
                        <CardHeader className="pb-2">
                            <CardTitle className="text-sm font-medium text-slate-400">Avg Response Time</CardTitle>
                        </CardHeader>
                        <CardContent>
                            <div className="text-2xl font-bold text-white">
                                {metrics ? `${metrics.averageResponseTime.toFixed(0)}ms` : '...'}
                            </div>
                            <p className="text-xs text-slate-400 mt-1">Last {metrics?.totalRequests ?? 0} requests</p>
                        </CardContent>
                    </Card>
                </div>

                <h2 className="text-xl font-bold text-white mb-4">Infrastructure Components</h2>
                <Card className="bg-slate-800/50 border-slate-700 mb-8">
                    <CardContent className="p-0">
                        <table className="w-full text-left text-sm text-slate-400">
                            <thead className="bg-slate-800 text-xs uppercase text-slate-200">
                                <tr>
                                    <th className="px-6 py-3">Component</th>
                                    <th className="px-6 py-3">Status</th>
                                    <th className="px-6 py-3">Duration</th>
                                    <th className="px-6 py-3">Tags</th>
                                </tr>
                            </thead>
                            <tbody className="divide-y divide-slate-700">
                                {healthStatus && Object.entries(healthStatus.entries).map(([name, entry]) => (
                                    <tr key={name} className="hover:bg-slate-800/50">
                                        <td className="px-6 py-4 font-mono text-white">{name}</td>
                                        <td className="px-6 py-4">
                                            <Badge variant="outline" className={
                                                entry.status === 'Healthy' ? 'text-green-400 border-green-500/30' :
                                                    entry.status === 'Degraded' ? 'text-yellow-400 border-yellow-500/30' :
                                                        'text-red-400 border-red-500/30'
                                            }>
                                                {entry.status}
                                            </Badge>
                                        </td>
                                        <td className="px-6 py-4">{entry.duration}</td>
                                        <td className="px-6 py-4">{entry.tags?.join(', ') || '-'}</td>
                                    </tr>
                                ))}
                                {!healthStatus && (
                                    <tr>
                                        <td colSpan={4} className="px-6 py-8 text-center text-slate-500 italic">
                                            Loading health status...
                                        </td>
                                    </tr>
                                )}
                            </tbody>
                        </table>
                    </CardContent>
                </Card>

                <h2 className="text-xl font-bold text-white mb-4">Kubernetes Status</h2>
                <Card className="bg-slate-800/50 border-slate-700">
                    <CardContent className="p-6">
                        {k8sStatus?.isConnected ? (
                            <div>
                                <div className="flex items-center gap-2 mb-4 text-emerald-400">
                                    <div className="h-2.5 w-2.5 rounded-full bg-emerald-500 animate-pulse" />
                                    <span className="font-semibold uppercase text-xs tracking-wider">Connected to Cluster</span>
                                </div>
                                <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                                    <div className="p-4 bg-slate-900/50 rounded-lg border border-slate-700">
                                        <div className="text-xs text-slate-500 uppercase mb-1">Nodes</div>
                                        <div className="text-xl text-white font-mono">{k8sStatus.nodes?.length || 0}</div>
                                    </div>
                                    <div className="p-4 bg-slate-900/50 rounded-lg border border-slate-700">
                                        <div className="text-xs text-slate-500 uppercase mb-1">Deployments</div>
                                        <div className="text-xl text-white font-mono">{k8sStatus.deployments?.length || 0}</div>
                                    </div>
                                </div>
                            </div>
                        ) : (
                            <div className="text-center py-4">
                                <p className="text-slate-500 italic mb-2">Kubernetes monitoring is disabled or not available in this environment.</p>
                                <Badge variant="outline" className="text-slate-400 border-slate-700">Disconnected</Badge>
                            </div>
                        )}
                    </CardContent>
                </Card>
            </main>
        </div>
    );
}
