'use client';

import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';

export type EmailEventType = 'bounced' | 'complained' | 'delivery_delayed';

export interface EmailEvent {
  id: string;
  email: string;
  type: string;
  reason: string;
  at: string;
  emailId: string;
}

/** Delivery problems Resend reported (bounces, complaints, delays), newest first. Optional type filter. */
export function useEmailEvents(type?: string) {
  return useQuery({
    queryKey: ['email-events', type ?? 'all'],
    queryFn: async () =>
      (await api.get<EmailEvent[]>('/api/email-events', { params: { limit: 200, type: type || undefined } })).data,
  });
}
