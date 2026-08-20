'use client';

import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';

export interface ApiMeta {
  /** The version of the API this admin is talking to, as the API itself reports it. */
  version: string;
  /** Whether the instance exposes /swagger, so the About dialog can link to it or leave it out. */
  swaggerEnabled: boolean;
}

/**
 * The running API's own version. This is the source of truth for the version shown in the admin,
 * replacing the hand-maintained CURRENT_VERSION constant that went nineteen minor versions stale
 * because bumping it was a step somebody had to remember.
 */
export function useApiMeta() {
  return useQuery({
    queryKey: ['meta'],
    queryFn: async () => (await api.get<ApiMeta>('/api/meta')).data,
    // The API and the admin are separate images and redeploy independently, so an open tab can
    // outlive the version it first read. Cached for five minutes and refreshed when the tab is
    // focused again: enough to keep this off the hot path, short enough that "what am I running"
    // stops being wrong shortly after an API deploy rather than until someone reloads.
    staleTime: 5 * 60 * 1000,
    refetchOnWindowFocus: true,
    // A failed /api/meta must not surface as an error anywhere. Everything downstream treats an
    // absent version as "unknown" and hides itself.
    retry: false,
  });
}

/** The admin bundle's own version, stamped into the image at build time. */
export function adminVersion(): string | null {
  return process.env.NEXT_PUBLIC_ADMIN_VERSION || null;
}
