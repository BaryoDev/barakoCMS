import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';

// Mirrors the BarakoCMS.Analytics.Umami module responses (camelCase over the wire).

export type AnalyticsRange = '24h' | '7d' | '30d' | '90d';
export type MetricType = 'path' | 'referrer' | 'country' | 'browser' | 'os' | 'device';

export interface AnalyticsWebsite {
  id: string;
  name: string;
  domain: string;
}

export interface WebsitesResponse {
  configured: boolean;
  websites: AnalyticsWebsite[];
}

export interface StatValue {
  value: number;
  previous: number;
}

export interface AnalyticsSummary {
  pageviews: StatValue;
  visitors: StatValue;
  visits: StatValue;
  bounces: StatValue;
  totalTime: StatValue;
}

export interface SeriesPoint {
  x: string;
  y: number;
}

export interface AnalyticsSeries {
  unit: string;
  pageviews: SeriesPoint[];
  sessions: SeriesPoint[];
}

export interface MetricRow {
  x: string;
  y: number;
}

export interface CreatedWebsite {
  id: string;
  name: string;
  domain: string;
  snippet: string;
}

export interface SiteStatus {
  installed: boolean;
  pageviews: number;
  activeNow: number;
  snippet: string;
}

/** Whether a site has started sending data to Umami yet (snippet live), plus a live active-visitor
 * count for instant verification. `refetch()` powers the "Verify installation" button. */
export function useSiteStatus(websiteId: string | undefined) {
  return useQuery({
    queryKey: ['analytics', 'status', websiteId],
    queryFn: async () => (await api.get<SiteStatus>(`/api/analytics/${websiteId}/status`)).data,
    enabled: !!websiteId,
  });
}

/** Sites Umami tracks. `configured: false` means the module is installed but Umami isn't wired up. */
export function useAnalyticsWebsites() {
  return useQuery({
    queryKey: ['analytics', 'websites'],
    queryFn: async () => (await api.get<WebsitesResponse>('/api/analytics/websites')).data,
  });
}

export function useAnalyticsSummary(websiteId: string | undefined, range: AnalyticsRange) {
  return useQuery({
    queryKey: ['analytics', 'summary', websiteId, range],
    queryFn: async () =>
      (await api.get<AnalyticsSummary>(`/api/analytics/${websiteId}/summary`, { params: { range } })).data,
    enabled: !!websiteId,
  });
}

export function useAnalyticsSeries(websiteId: string | undefined, range: AnalyticsRange) {
  return useQuery({
    queryKey: ['analytics', 'series', websiteId, range],
    queryFn: async () =>
      (await api.get<AnalyticsSeries>(`/api/analytics/${websiteId}/series`, { params: { range } })).data,
    enabled: !!websiteId,
  });
}

export function useAnalyticsMetric(
  websiteId: string | undefined,
  type: MetricType,
  range: AnalyticsRange,
  limit = 10,
) {
  return useQuery({
    queryKey: ['analytics', 'metric', websiteId, type, range, limit],
    queryFn: async () =>
      (await api.get<MetricRow[]>(`/api/analytics/${websiteId}/metric`, { params: { type, range, limit } })).data,
    enabled: !!websiteId,
  });
}

export function useCreateWebsite() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (data: { name: string; domain: string }) =>
      (await api.post<CreatedWebsite>('/api/analytics/websites', data)).data,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['analytics', 'websites'] });
    },
  });
}
