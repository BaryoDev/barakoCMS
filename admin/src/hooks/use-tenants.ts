'use client';

import { useSyncExternalStore } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api, tokenStore, subscribeToAuth, tenantOfToken } from '@/lib/api';

export interface TenantSummary {
  slug: string;
  name: string;
  logoUrl?: string | null;
  branding: Record<string, string>;
}

interface SwitchResponse {
  token: string;
  expiry: string;
  refreshToken: string;
  refreshTokenExpiry: string;
}

/** A full tenant record from the platform-admin tenants API (SuperAdmin only). */
export interface Tenant {
  id: string;
  slug: string;
  name: string;
  about?: string | null;
  logoUrl?: string | null;
  email?: string | null;
  location?: string | null;
  isActive: boolean;
}

export interface CreateTenantInput {
  Handle: string;
  Name: string;
  About?: string;
  IsActive: boolean;
}

/** Every tenant on the deployment (platform admin). Distinct from useMyTenants (only the caller's). */
export function useTenants() {
  return useQuery({
    queryKey: ['tenants'],
    queryFn: async () => (await api.get<Tenant[]>('/api/tenants')).data,
  });
}

/** Create a tenant. The API also provisions the creator as an active admin member, so the new
 * tenant is immediately usable (and login to it isn't blocked by the cross-tenant token guard). */
export function useCreateTenant() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (input: CreateTenantInput) =>
      (await api.post<Tenant>('/api/tenants', input)).data,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['tenants'] });
      queryClient.invalidateQueries({ queryKey: ['me', 'tenants'] });
    },
  });
}

/** The tenants the signed-in user belongs to (their active memberships). Empty on a single-tenant
 * deployment or for a user with no memberships. */
export function useMyTenants() {
  return useQuery({
    queryKey: ['me', 'tenants'],
    queryFn: async () => (await api.get<TenantSummary[]>('/api/me/tenants')).data,
    staleTime: 5 * 60 * 1000,
  });
}

/** The tenant the current token is scoped to (its `tenant` claim), reactive to sign-in/switch. */
export function useCurrentTenant(): string | null {
  return useSyncExternalStore(
    subscribeToAuth,
    () => tenantOfToken(tokenStore.token),
    () => null,
  );
}

/** Swap the session token for one scoped to another tenant the user belongs to (no re-auth). The new
 * token carries the tenant claim, so every subsequent request's X-Tenant follows automatically. */
export function useSwitchTenant() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (slug: string) => {
      const { data } = await api.post<SwitchResponse>('/api/me/switch', { Club: slug });
      tokenStore.set(data.token, data.refreshToken);
      return data;
    },
    onSuccess: () => {
      // Everything is tenant-scoped — drop all cached data so it refetches under the new tenant.
      queryClient.invalidateQueries();
    },
  });
}
