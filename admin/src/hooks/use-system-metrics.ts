import { useQuery } from "@tanstack/react-query";

export interface SystemMetrics {
    diskUsagePercent: number;
    memoryUsagePercent: number;
    healthStatus: "Healthy" | "Degraded" | "Unhealthy";
    totalRequests: number;
    errorRate: number;
}

// Parse Prometheus text format
function parsePrometheusMetrics(text: string): Record<string, number> {
    const metrics: Record<string, number> = {};
    const lines = text.split("\n");

    for (const line of lines) {
        if (!line || line.startsWith("#")) continue;

        const match = line.match(/^([a-z_]+)(?:\{[^}]+\})?\s+([\d.]+)/);
        if (match) {
            const [, key, value] = match;
            metrics[key] = parseFloat(value);
        }
    }

    return metrics;
}

export function useSystemMetrics() {
    return useQuery({
        queryKey: ["system-metrics"],
        queryFn: async (): Promise<SystemMetrics> => {
            // Fetch /metrics endpoint
            const response = await fetch(`${process.env.NEXT_PUBLIC_API_URL}/metrics`);
            const text = await response.text();
            const metrics = parsePrometheusMetrics(text);

            // Calculate disk usage (placeholder - requires actual metric name from Prometheus)
            const diskUsagePercent = metrics["disk_usage_percent"] || 0;

            // Calculate memory usage
            const memoryBytes = metrics["process_private_memory_bytes"] || 0;
            const memoryUsagePercent = (memoryBytes / (1024 * 1024 * 1024)) * 100; // Convert to GB percentage

            // Get request metrics
            const totalRequests = metrics["http_requests_received_total"] || 0;
            const totalErrors = metrics["http_requests_errors_total"] || 0;
            const errorRate = totalRequests > 0 ? (totalErrors / totalRequests) * 100 : 0;

            // Determine health status
            let healthStatus: "Healthy" | "Degraded" | "Unhealthy" = "Healthy";
            if (diskUsagePercent > 90 || memoryUsagePercent > 85 || errorRate > 5) {
                healthStatus = "Unhealthy";
            } else if (diskUsagePercent > 70 || memoryUsagePercent > 70 || errorRate > 2) {
                healthStatus = "Degraded";
            }

            return {
                diskUsagePercent,
                memoryUsagePercent,
                healthStatus,
                totalRequests,
                errorRate,
            };
        },
        refetchInterval: 30000, // Refetch every 30 seconds
    });
}
