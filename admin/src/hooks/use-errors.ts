import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { Paginated } from '@/lib/api';

// Mirrors the BarakoCMS.Diagnostics client-error module responses (camelCase over the wire).

export type ErrorKind = 'error' | 'unhandledrejection' | 'react' | 'api';
export type ErrorSeverity = 'error' | 'warning';

export interface ClientErrorDto {
  id: string;
  kind: string;
  severity: string;
  message: string;
  stack?: string | null;
  source?: string | null;
  status?: number | null;
  url?: string | null;
  userAgent?: string | null;
  appVersion?: string | null;
  tenant?: string | null;
  username?: string | null;
  count: number;
  firstSeenAt: string;
  lastSeenAt: string;
  resolved?: boolean;
}

export interface ClientErrorsQuery {
  page?: number;
  pageSize?: number;
  resolved?: boolean; // undefined = all
  severity?: ErrorSeverity; // undefined = all
  q?: string;
}

export interface ClientErrorsResult {
  items: ClientErrorDto[];
  totalItems: number;
  page: number;
  pageSize: number;
  totalPages: number;
  hasNextPage: boolean;
  hasPreviousPage: boolean;
}

// The backend may return the standard Paginated<T> envelope or a bare array
// (depending on the module version). Normalise both into ClientErrorsResult.
function normalize(
  data: Paginated<ClientErrorDto> | ClientErrorDto[],
  page: number,
  pageSize: number,
): ClientErrorsResult {
  if (Array.isArray(data)) {
    return {
      items: data,
      totalItems: data.length,
      page,
      pageSize,
      totalPages: 1,
      hasNextPage: false,
      hasPreviousPage: false,
    };
  }
  return {
    items: data.items ?? [],
    totalItems: data.totalItems ?? 0,
    page: data.page ?? page,
    pageSize: data.pageSize ?? pageSize,
    totalPages: data.totalPages ?? 1,
    hasNextPage: data.hasNextPage ?? false,
    hasPreviousPage: data.hasPreviousPage ?? false,
  };
}

export function useClientErrors(query: ClientErrorsQuery) {
  const { page = 1, pageSize = 25, resolved, severity, q } = query;
  return useQuery({
    queryKey: ['client-errors', { page, pageSize, resolved, severity, q }],
    queryFn: async () => {
      const params: Record<string, string | number | boolean> = { page, pageSize };
      if (resolved !== undefined) params.resolved = resolved;
      if (severity) params.severity = severity;
      if (q) params.q = q;
      const response = await api.get<Paginated<ClientErrorDto> | ClientErrorDto[]>(
        '/api/client-errors',
        { params },
      );
      return normalize(response.data, page, pageSize);
    },
  });
}

export function useResolveClientError() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      await api.post(`/api/client-errors/${id}/resolve`, {});
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['client-errors'] });
    },
  });
}
