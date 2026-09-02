'use client';

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api, type Paginated } from '@/lib/api';

export interface Device {
    id: string;
    description: string;
    lastSeenIp: string;
    lastUsedAt: string;
    status: 'Pending' | 'Trusted' | 'Revoked';
    /** The device this session is on. It can be revoked, which signs this browser out. */
    current: boolean;
}

/**
 * The signed-in user's own devices.
 *
 * Not an administrative list. `GET /api/devices` is authenticated and scoped to the caller, so this
 * is the same shape as the security page next to it: a person looking at their own account.
 */
export function useDevices() {
    return useQuery({
        queryKey: ['devices'],
        queryFn: async () => {
            const response = await api.get<Paginated<Device>>('/api/devices', {
                params: { pageSize: 100 },
            });
            return response.data;
        },
    });
}

export function useRevokeDevice() {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async (id: string) => {
            await api.post(`/api/devices/${id}/revoke`);
        },
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['devices'] }),
    });
}
