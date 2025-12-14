"use client";

import { useSystemMetrics } from "@/hooks/use-system-metrics";

export function SystemMetrics() {
    const { data: metrics, isLoading } = useSystemMetrics();

    if (isLoading || !metrics) {
        return (
            <div className="bg-white rounded-lg shadow p-6">
                <h3 className="text-lg font-semibold mb-4">System Health</h3>
                <p className="text-gray-500">Loading metrics...</p>
            </div>
        );
    }

    const getHealthColor = (status: string) => {
        switch (status) {
            case "Healthy":
                return "text-green-600";
            case "Degraded":
                return "text-yellow-600";
            case "Unhealthy":
                return "text-red-600";
            default:
                return "text-gray-600";
        }
    };

    const getUsageColor = (percent: number) => {
        if (percent > 85) return "bg-red-500";
        if (percent > 70) return "bg-yellow-500";
        return "bg-green-500";
    };

    return (
        <div className="bg-white rounded-lg shadow p-6">
            <h3 className="text-lg font-semibold mb-4">System Health</h3>

            {/* Health Status */}
            <div className="mb-6">
                <div className="flex items-center justify-between mb-2">
                    <span className="text-sm font-medium text-gray-700">Status</span>
                    <span className={`text-sm font-bold ${getHealthColor(metrics.healthStatus)}`}>
                        {metrics.healthStatus}
                    </span>
                </div>
            </div>

            {/* Memory Usage */}
            <div className="mb-4">
                <div className="flex items-center justify-between mb-2">
                    <span className="text-sm font-medium text-gray-700">Memory</span>
                    <span className="text-sm text-gray-600">{metrics.memoryUsagePercent.toFixed(1)}%</span>
                </div>
                <div className="w-full bg-gray-200 rounded-full h-2">
                    <div
                        className={`h-2 rounded-full ${getUsageColor(metrics.memoryUsagePercent)}`}
                        style={{ width: `${Math.min(metrics.memoryUsagePercent, 100)}%` }}
                    ></div>
                </div>
            </div>

            {/* Disk Usage */}
            <div className="mb-4">
                <div className="flex items-center justify-between mb-2">
                    <span className="text-sm font-medium text-gray-700">Disk</span>
                    <span className="text-sm text-gray-600">{metrics.diskUsagePercent.toFixed(1)}%</span>
                </div>
                <div className="w-full bg-gray-200 rounded-full h-2">
                    <div
                        className={`h-2 rounded-full ${getUsageColor(metrics.diskUsagePercent)}`}
                        style={{ width: `${Math.min(metrics.diskUsagePercent, 100)}%` }}
                    ></div>
                </div>
            </div>

            {/* Error Rate */}
            <div>
                <div className="flex items-center justify-between mb-2">
                    <span className="text-sm font-medium text-gray-700">Error Rate</span>
                    <span className="text-sm text-gray-600">{metrics.errorRate.toFixed(2)}%</span>
                </div>
                <div className="text-xs text-gray-500 mt-1">
                    {metrics.totalRequests.toLocaleString()} total requests
                </div>
            </div>
        </div>
    );
}
