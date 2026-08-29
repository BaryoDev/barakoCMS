'use client';

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api, type Paginated } from '@/lib/api';

export interface ApiKey {
  id: string;
  name: string;
  prefix: string;
  scopes: string[];
  tenantSlug: string;
  expiresAt?: string | null;
  lastUsedAt?: string | null;
  revoked: boolean;
  createdAt: string;
}

export interface CreateApiKeyInput {
  name: string;
  scopes: string[];
  expiresAt?: string | null;
}

/** The create response — the only time the full secret (`key`) is ever returned. */
export interface CreatedApiKey extends ApiKey {
  key: string;
}

/** The scopes a key can hold. Mirrors the backend ApiKeyScopes; content surface only. */
export const API_KEY_SCOPES: { value: string; label: string; description: string }[] = [
  { value: 'content:read', label: 'Read content', description: 'List and read entries' },
  { value: 'content:write', label: 'Write content', description: 'Create, update, delete entries' },
  { value: 'contenttype:read', label: 'Read content types', description: 'Read schemas' },
  { value: 'contenttype:write', label: 'Write content types', description: 'Create, update schemas' },
  { value: '*', label: 'Full content access', description: 'Everything on the content API' },
];

export function useApiKeys() {
  return useQuery({
    queryKey: ['api-keys'],
    queryFn: async () => (await api.get<Paginated<ApiKey>>('/api/api-keys')).data.items,
  });
}

export function useCreateApiKey() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (input: CreateApiKeyInput) =>
      (await api.post<CreatedApiKey>('/api/api-keys', input)).data,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['api-keys'] }),
  });
}

export function useRevokeApiKey() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      await api.delete(`/api/api-keys/${id}`);
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['api-keys'] }),
  });
}
