import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';

export interface ClusterStatus {
    isInCluster: boolean;
    isConnected: boolean;
    connectionMethod: string;
    nodes: NodeInfo[];
    deployments: DeploymentInfo[];
    error?: string;
}

export interface NodeInfo {
    name: string;
    status: string;
    cpuCapacity: string;
    memoryCapacity: string;
}

export interface DeploymentInfo {
    name: string;
    namespace: string;
    replicas: number;
    availableReplicas: number;
}

export interface MetricsSummary {
    totalRequests: number;
    totalErrors: number;
    averageResponseTime: number;
    errorRate: number;
}

export interface DetailedHealthStatus {
    status: string;
    totalDuration: string;
    entries: Record<string, HealthEntry>;
}

export interface HealthEntry {
    status: string;
    duration: string;
    description?: string;
    data?: Record<string, unknown>;
    tags?: string[];
}

export function useKubernetesStatus() {
    return useQuery({
        queryKey: ['kubernetes-status'],
        queryFn: async () => {
            const response = await api.get<ClusterStatus>('/api/monitoring/k8s');
            return response.data;
        },
        refetchInterval: 30000,
    });
}

// Anonymous /health returns only { status }; the dashboard needs per-check `entries`, so it uses
// the authenticated /api/monitoring/health endpoint.
export function useHealthStatus() {
    return useQuery({
        queryKey: ['health-status'],
        queryFn: async () => {
            const response = await api.get<DetailedHealthStatus>('/api/monitoring/health');
            return response.data;
        },
        refetchInterval: 15000,
    });
}

export function useMetrics() {
    return useQuery({
        queryKey: ['metrics'],
        queryFn: async () => {
            const response = await api.get<MetricsSummary>('/api/monitoring/metrics');
            return response.data;
        },
        refetchInterval: 10000,
    });
}
