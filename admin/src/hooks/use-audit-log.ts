'use client';

import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { Paginated } from '@/lib/api';

// Mirrors GET /api/audit (barakoCMS core — always available, unlike an opt-in module).

export interface AuditEventDto {
  id: string;
  tenantSlug: string;
  action: string;
  actorUserId?: string | null;
  actorUsername?: string | null;
  targetType?: string | null;
  targetId?: string | null;
  metadata?: Record<string, unknown> | null;
  ipAddress?: string | null;
  createdAt: string;
}

export interface AuditLogQuery {
  page?: number;
  pageSize?: number;
  action?: string;
  actorUserId?: string;
  tenant?: string;
  from?: string;
  to?: string;
}

export function useAuditLog(query: AuditLogQuery) {
  const { page = 1, pageSize = 25, action, actorUserId, tenant, from, to } = query;
  return useQuery({
    queryKey: ['audit-log', { page, pageSize, action, actorUserId, tenant, from, to }],
    queryFn: async () => {
      const params: Record<string, string | number> = { page, pageSize };
      if (action) params.action = action;
      if (actorUserId) params.actorUserId = actorUserId;
      if (tenant) params.tenant = tenant;
      if (from) params.from = from;
      if (to) params.to = to;
      const response = await api.get<Paginated<AuditEventDto>>('/api/audit', { params });
      return response.data;
    },
  });
}
