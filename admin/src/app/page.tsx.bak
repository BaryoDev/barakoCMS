'use client';

import { useEffect } from 'react';
import Link from 'next/link';
import { useAuth } from '@/hooks/use-auth';
import { useSchemas } from '@/hooks/use-schemas';
import { useContents } from '@/hooks/use-contents';
import { useKubernetesStatus, useHealthStatus } from '@/hooks/use-monitoring';
import { Header } from '@/components/header';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Skeleton } from '@/components/ui/skeleton';
import {
  Layers, FileText, GitBranch, Activity, Server, Database,
  ArrowRight, Zap, Shield, ChevronRight
} from 'lucide-react';
import { SystemMetrics } from '@/components/system-metrics';

export default function DashboardPage() {
  const { isAuthenticated, isLoading, requireAuth } = useAuth();
  const { data: schemas, isLoading: schemasLoading } = useSchemas();
  const { data: contents, isLoading: contentsLoading } = useContents();

  useEffect(() => {
    requireAuth();
  }, [requireAuth]);

  const { data: k8sStatus } = useKubernetesStatus();
  const { data: healthStatus } = useHealthStatus();

  // Map backend health components to UI list
  const healthChecks = healthStatus ? Object.entries(healthStatus.entries).map(([name, entry]) => ({
    id: name,
    name: name,
    status: entry.status,
    icon: name === 'Database' ? Database : Server, // Simple icon mapping
  })) : [];

  // Add API Server if not explicitly in entries (as it's the one serving the request)
  if (healthStatus && !healthStatus.entries['API Server']) {
    healthChecks.unshift({
      id: 'API',
      name: 'API Server',
      status: healthStatus.status,
      icon: Server
    });
  }

  const systemHealth = {
    status: healthStatus?.status.toLowerCase() || 'unknown',
    color: healthStatus?.status === 'Healthy' ? 'text-emerald-400' : 'text-amber-400'
  };

  if (isLoading) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-slate-900">
        <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-amber-500"></div>
      </div>
    );
  }

  if (!isAuthenticated) return null;

  return (
    <div className="min-h-screen bg-gradient-to-br from-slate-900 via-slate-900 to-slate-800">
      <Header />

      <main className="container mx-auto px-4 py-8 max-w-6xl">
        {/* Welcome Section */}
        <div className="mb-10">
          <h1 className="text-4xl font-bold text-white mb-2 flex items-center gap-3">
            <div className="p-2 bg-gradient-to-br from-amber-500/20 to-orange-500/20 rounded-xl">
              <Zap className="h-8 w-8 text-amber-400" />
            </div>
            Dashboard
          </h1>
          <p className="text-slate-400 text-lg">Welcome to BarakoCMS Admin Dashboard</p>
        </div>

        {/* Quick Stats */}
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-6 mb-10">
          <Card className="bg-slate-800/50 border-slate-700 backdrop-blur-sm hover:border-slate-600 transition-all duration-300 group">
            <CardHeader className="pb-2">
              <div className="flex items-center justify-between">
                <CardDescription className="text-slate-400">Content Types</CardDescription>
                <div className="p-2 bg-cyan-500/10 rounded-lg group-hover:bg-cyan-500/20 transition-colors">
                  <Layers className="h-4 w-4 text-cyan-400" />
                </div>
              </div>
              {schemasLoading ? (
                <Skeleton className="h-9 w-16 bg-slate-700 mt-1" />
              ) : (
                <CardTitle className="text-3xl text-white">{schemas?.length ?? 0}</CardTitle>
              )}
            </CardHeader>
            <CardContent>
              <p className="text-sm text-slate-500">Active schemas</p>
            </CardContent>
          </Card>

          <Card className="bg-slate-800/50 border-slate-700 backdrop-blur-sm hover:border-slate-600 transition-all duration-300 group">
            <CardHeader className="pb-2">
              <div className="flex items-center justify-between">
                <CardDescription className="text-slate-400">Content Items</CardDescription>
                <div className="p-2 bg-amber-500/10 rounded-lg group-hover:bg-amber-500/20 transition-colors">
                  <FileText className="h-4 w-4 text-amber-400" />
                </div>
              </div>
              {contentsLoading ? (
                <Skeleton className="h-9 w-16 bg-slate-700 mt-1" />
              ) : (
                <CardTitle className="text-3xl text-white">{contents?.length ?? 0}</CardTitle>
              )}
            </CardHeader>
            <CardContent>
              <p className="text-sm text-slate-500">Total entries</p>
            </CardContent>
          </Card>

          <Card className="bg-slate-800/50 border-slate-700 backdrop-blur-sm hover:border-slate-600 transition-all duration-300 group">
            <CardHeader className="pb-2">
              <div className="flex items-center justify-between">
                <CardDescription className="text-slate-400">Infrastructure</CardDescription>
                <div className="p-2 bg-blue-500/10 rounded-lg group-hover:bg-blue-500/20 transition-colors">
                  <Server className="h-4 w-4 text-blue-400" />
                </div>
              </div>
              <CardTitle className="text-3xl text-white flex items-center gap-2">
                {k8sStatus?.isConnected ? (
                  <>
                    <span className="text-emerald-400">●</span>
                    <span className="text-lg font-normal text-slate-300">Online</span>
                  </>
                ) : (
                  <>
                    <span className="text-slate-500">○</span>
                    <span className="text-lg font-normal text-slate-500">Offline</span>
                  </>
                )}
              </CardTitle>
            </CardHeader>
            <CardContent>
              <p className="text-sm text-slate-500">
                {k8sStatus?.isConnected
                  ? `${k8sStatus.nodes?.length || 0} nodes`
                  : 'Not connected'}
              </p>
            </CardContent>
          </Card>

          <Card className="bg-slate-800/50 border-slate-700 backdrop-blur-sm hover:border-slate-600 transition-all duration-300 group">
            <CardHeader className="pb-2">
              <div className="flex items-center justify-between">
                <CardDescription className="text-slate-400">System Health</CardDescription>
                <div className="p-2 bg-emerald-500/10 rounded-lg group-hover:bg-emerald-500/20 transition-colors">
                  <Activity className="h-4 w-4 text-emerald-400" />
                </div>
              </div>
              <CardTitle className="text-3xl flex items-center gap-2">
                <span className={systemHealth.color}>●</span>
                <span className="text-lg font-normal text-slate-300 capitalize">{systemHealth.status}</span>
              </CardTitle>
            </CardHeader>
            <CardContent>
              <p className="text-sm text-slate-500">All systems operational</p>
            </CardContent>
          </Card>
        </div>

        {/* Health Checks Detail */}
        <Card className="bg-slate-800/50 border-slate-700 backdrop-blur-sm mb-10 shadow-xl">
          <CardHeader>
            <CardTitle className="text-white flex items-center gap-2">
              <Shield className="h-5 w-5 text-emerald-400" />
              Health Checks
            </CardTitle>
            <CardDescription className="text-slate-400">Real-time system monitoring</CardDescription>
          </CardHeader>
          <CardContent>
            <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
              {healthChecks.map((hc) => {
                const Icon = hc.icon;
                return (
                  <div
                    key={hc.id}
                    className="bg-slate-900/50 rounded-xl p-5 border border-slate-700 hover:border-slate-600 transition-all duration-300 hover:shadow-lg"
                  >
                    <div className="flex items-center justify-between mb-3">
                      <div className="flex items-center gap-3">
                        <div className="p-2 bg-slate-800 rounded-lg">
                          <Icon className="h-5 w-5 text-slate-400" />
                        </div>
                        <span className="text-white font-medium">{hc.name}</span>
                      </div>
                      <Badge
                        variant="outline"
                        className={
                          hc.status === 'Healthy'
                            ? 'text-emerald-400 bg-emerald-500/10 border-emerald-500/50'
                            : hc.status === 'Degraded'
                              ? 'text-amber-400 bg-amber-500/10 border-amber-500/50'
                              : 'text-red-400 bg-red-500/10 border-red-500/50'
                        }
                      >
                        {hc.status}
                      </Badge>
                    </div>
                    <div className="flex items-center gap-2">
                      <div className="h-1.5 flex-1 bg-slate-700 rounded-full overflow-hidden">
                        <div
                          className={`h-full rounded-full ${hc.status === 'Healthy' ? 'bg-emerald-500' : 'bg-amber-500'
                            }`}
                          style={{ width: '100%' }}
                        />
                      </div>
                      <span className="text-xs text-slate-500">100%</span>
                    </div>
                  </div>
                );
              })}
            </div>
          </CardContent>
        </Card>

        {/* Quick Actions */}
        <div className="mb-6">
          <h2 className="text-lg font-semibold text-white mb-4 flex items-center gap-2">
            <ChevronRight className="h-5 w-5 text-amber-400" />
            Quick Actions
          </h2>
        </div>
        <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
          <Link href="/schemas" className="group">
            <Card className="bg-gradient-to-br from-cyan-500/10 to-blue-600/10 border-cyan-500/20 hover:border-cyan-500/50 transition-all duration-300 cursor-pointer h-full hover:shadow-xl hover:shadow-cyan-500/10 hover:-translate-y-1">
              <CardHeader>
                <CardTitle className="text-white flex items-center gap-3">
                  <div className="p-2 bg-cyan-500/20 rounded-lg group-hover:bg-cyan-500/30 transition-colors">
                    <Layers className="h-5 w-5 text-cyan-400" />
                  </div>
                  Content Types
                  <ArrowRight className="h-4 w-4 ml-auto text-cyan-400 opacity-0 group-hover:opacity-100 transition-opacity" />
                </CardTitle>
                <CardDescription className="text-slate-400">
                  Create and manage content schemas
                </CardDescription>
              </CardHeader>
            </Card>
          </Link>

          <Link href="/content" className="group">
            <Card className="bg-gradient-to-br from-amber-500/10 to-orange-600/10 border-amber-500/20 hover:border-amber-500/50 transition-all duration-300 cursor-pointer h-full hover:shadow-xl hover:shadow-amber-500/10 hover:-translate-y-1">
              <CardHeader>
                <CardTitle className="text-white flex items-center gap-3">
                  <div className="p-2 bg-amber-500/20 rounded-lg group-hover:bg-amber-500/30 transition-colors">
                    <FileText className="h-5 w-5 text-amber-400" />
                  </div>
                  Content
                  <ArrowRight className="h-4 w-4 ml-auto text-amber-400 opacity-0 group-hover:opacity-100 transition-opacity" />
                </CardTitle>
                <CardDescription className="text-slate-400">
                  Manage your content entries
                </CardDescription>
              </CardHeader>
            </Card>
          </Link>

          <Link href="/workflows" className="group">
            <Card className="bg-gradient-to-br from-purple-500/10 to-pink-600/10 border-purple-500/20 hover:border-purple-500/50 transition-all duration-300 cursor-pointer h-full hover:shadow-xl hover:shadow-purple-500/10 hover:-translate-y-1">
              <CardHeader>
                <CardTitle className="text-white flex items-center gap-3">
                  <div className="p-2 bg-purple-500/20 rounded-lg group-hover:bg-purple-500/30 transition-colors">
                    <GitBranch className="h-5 w-5 text-purple-400" />
                  </div>
                  Workflows
                  <ArrowRight className="h-4 w-4 ml-auto text-purple-400 opacity-0 group-hover:opacity-100 transition-opacity" />
                </CardTitle>
                <CardDescription className="text-slate-400">
                  Automate your content pipeline
                </CardDescription>
              </CardHeader>
            </Card>
          </Link>
        </div>
      </main>
    </div>
  );
}
