import { useQuery } from '@tanstack/react-query';
import { api, getApiUrl } from '@/lib/api';

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

// /health is anonymous and outside /api, so plain fetch is fine — but the URL
// must come from getApiUrl() so runtime env-config.js overrides still apply.
export function useHealthStatus() {
    return useQuery({
        queryKey: ['health-status'],
        queryFn: async () => {
            const response = await fetch(`${getApiUrl()}/health`);
            if (!response.ok) throw new Error('Failed to fetch health status');
            return response.json() as Promise<DetailedHealthStatus>;
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
