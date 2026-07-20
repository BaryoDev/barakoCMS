import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';

// Mirrors the feature-flags module admin responses (camelCase over the wire).

export interface FeatureFlag {
  key: string;
  description: string;
  enabled: boolean;
  tenantSlugs: string[];
  userEmails: string[];
  rolloutPercent: number;
  updatedAt: string;
}

/** All flags with their admin metadata. Errors when the module isn't installed. */
export function useFeatureFlags() {
  return useQuery({
    queryKey: ['feature-flags'],
    queryFn: async () => (await api.get<FeatureFlag[]>('/api/feature-flags/admin')).data,
  });
}

export function useToggleFeatureFlag() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (key: string) => {
      await api.post(`/api/feature-flags/admin/${key}/toggle`, {});
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['feature-flags'] });
    },
  });
}
