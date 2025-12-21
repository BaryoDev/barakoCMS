import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';

// Kubernetes cluster status
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

// Health check data
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

// Fetch Kubernetes status
export function useKubernetesStatus() {
    return useQuery({
        queryKey: ['kubernetes-status'],
        queryFn: async () => {
            const response = await api.get<ClusterStatus>('/api/monitoring/k8s');
            return response.data;
        },
        refetchInterval: 30000, // Refresh every 30 seconds
    });
}

// Fetch health status
export function useHealthStatus() {
    return useQuery({
        queryKey: ['health-status'],
        queryFn: async () => {
            const response = await fetch(`${process.env.NEXT_PUBLIC_API_URL}/health`);
            if (!response.ok) throw new Error('Failed to fetch health status');
            return response.json() as Promise<DetailedHealthStatus>;
        },
        refetchInterval: 15000, // Refresh every 15 seconds
    });
}

// Fetch metrics
export function useMetrics() {
    return useQuery({
        queryKey: ['metrics'],
        queryFn: async () => {
            const response = await api.get<MetricsSummary>('/api/monitoring/metrics');
            return response.data;
        },
        refetchInterval: 10000, // Refresh every 10 seconds
    });
}
