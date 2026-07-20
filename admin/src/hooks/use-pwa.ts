import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';

// Mirrors the BarakoCMS.Pwa module responses (camelCase over the wire).

export type DisplayMode = 'standalone' | 'minimal-ui' | 'fullscreen' | 'browser';
export type Platform = 'ios' | 'android' | 'windows' | 'macos' | 'linux' | 'other';

export interface InstallDto {
  userId: string | null;
  username: string | null;
  tenant: string | null;
  platform: Platform | null;
  displayMode: DisplayMode;
  installed: boolean;
  userAgent: string | null;
  launchCount: number;
  firstSeenAt: string;
  lastSeenAt: string;
  installedAt: string | null;
}

/** Devices that have run the app, and who installed it to their home screen.
 * Errors when the BarakoCMS.Pwa module isn't installed on this host. */
export function usePwaInstalls() {
  return useQuery({
    queryKey: ['pwa', 'installs'],
    queryFn: async () => (await api.get<InstallDto[]>('/api/pwa/installs')).data,
  });
}
