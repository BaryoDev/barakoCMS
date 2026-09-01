'use client';

import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { NavMetric } from '@/lib/navigation';

/**
 * The numbers the sidebar rail shows beside a nav item.
 *
 * Two rules hold this together.
 *
 * A metric is either real or absent. Every query below either returns a number the API stated or
 * `null`, and `null` renders nothing. There is no placeholder and no derived guess: a rail that
 * says "5 installed" when nothing counted the modules is worse than a rail that says nothing.
 *
 * A metric is only fetched when its destination is on screen. The rail runs on every admin route,
 * so an ungated count would make every session pay for a list it may not be allowed to read. The
 * caller passes the hrefs that survived role filtering and each query is enabled by that, which
 * also keeps a 403 out of the picture rather than retrying it on every navigation.
 */

/** How far back a delivery problem still counts as current. */
const BOUNCE_WINDOW_MS = 24 * 60 * 60 * 1000;

/**
 * Rows pulled to count recent bounces. /api/email-events returns a bare newest-first array with no
 * total and no read state, so the window has to be counted client side. Saturating the cap means
 * the true figure is at least this, which is why the rail renders `50+` rather than `50` there.
 */
export const BOUNCE_LIMIT = 50;

/** Counts go stale slowly. They are chrome, not the page's own data. */
const STALE_MS = 60 * 1000;

export interface NavMetricValue {
  value: number;
  /** True when the count hit the query's cap, so the real figure is at least `value`. */
  atLeast: boolean;
}

export type NavMetrics = Partial<Record<NavMetric, NavMetricValue>>;

interface CountEnvelope {
  totalItems?: number;
}

/**
 * A count taken from the pagination envelope, asking for one row rather than a page of them.
 * `PaginatedResponse.TotalItems` is the whole point of the request; the item is waste we cannot
 * avoid without a count endpoint. Returns null when the response has no total, which is what an
 * older module returning a bare array looks like.
 */
function useTotalItems(
  key: string,
  url: string,
  enabled: boolean,
  extraParams: Record<string, string | number | boolean> = {},
) {
  return useQuery({
    queryKey: ['nav-metric', key],
    queryFn: async () => {
      const { data } = await api.get<CountEnvelope>(url, {
        params: { page: 1, pageSize: 1, ...extraParams },
      });
      return typeof data?.totalItems === 'number' ? data.totalItems : null;
    },
    enabled,
    retry: false,
    staleTime: STALE_MS,
  });
}

/**
 * Bounces inside the window, counted from the rows the endpoint returned.
 *
 * A row with an unparseable `at` is not counted. The alternative is treating it as current, which
 * turns bad data into an alarm nobody can clear.
 */
export function countRecentBounces(events: { at: string }[], now = Date.now()): number {
  const since = now - BOUNCE_WINDOW_MS;
  return events.filter((e) => {
    const at = Date.parse(e.at);
    return Number.isFinite(at) && at >= since;
  }).length;
}

function useRecentBounces(enabled: boolean) {
  return useQuery({
    queryKey: ['nav-metric', 'recentBounces'],
    queryFn: async () => {
      const { data } = await api.get<{ at: string }[]>('/api/email-events', {
        params: { type: 'bounced', limit: BOUNCE_LIMIT },
      });
      if (!Array.isArray(data)) return null;
      return countRecentBounces(data);
    },
    enabled,
    retry: false,
    staleTime: STALE_MS,
  });
}

/** Resolves the metrics named on the visible nav items. Pass the hrefs role filtering left behind. */
export function useNavMetrics(visibleHrefs: ReadonlySet<string>): NavMetrics {
  const entries = useTotalItems('entries', '/api/contents', visibleHrefs.has('/content'));
  const contentTypes = useTotalItems('contentTypes', '/api/content-types', visibleHrefs.has('/schemas'));
  const workflows = useTotalItems('workflows', '/api/workflows', visibleHrefs.has('/workflows'));
  const errors = useTotalItems('unresolvedErrors', '/api/client-errors', visibleHrefs.has('/errors'), {
    resolved: false,
  });
  const bounces = useRecentBounces(visibleHrefs.has('/email-events'));

  const metrics: NavMetrics = {};
  const put = (key: NavMetric, value: number | null | undefined, atLeast = false) => {
    if (typeof value === 'number') metrics[key] = { value, atLeast: atLeast && value > 0 };
  };

  put('entries', entries.data);
  put('contentTypes', contentTypes.data);
  put('workflows', workflows.data);
  put('unresolvedErrors', errors.data);
  put('recentBounces', bounces.data, bounces.data === BOUNCE_LIMIT);

  return metrics;
}
