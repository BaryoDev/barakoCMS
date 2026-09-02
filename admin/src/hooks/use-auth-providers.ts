'use client';

import { useQuery } from '@tanstack/react-query';
import { api, getApiUrl } from '@/lib/api';

/**
 * Which social sign-in buttons this deployment can actually complete.
 *
 * `GET /api/auth/providers` only exists when BarakoCMS.ExternalAuth is installed, and it answers
 * false for a provider whose client id is unset even then. Rendering the buttons unconditionally
 * put a dead control on every deployment without the module, which is the default one.
 *
 * Anything other than a well-formed answer means no buttons. A 404 (module absent), a 500, a
 * network failure and an unreachable API are all the same fact from here: nothing on this screen
 * can finish an external sign-in, so nothing on this screen should offer one.
 */
export interface AuthProviders {
    facebook: boolean;
    google: boolean;
    linkedin: boolean;
    github: boolean;
}

const NONE: AuthProviders = { facebook: false, google: false, linkedin: false, github: false };

export function useAuthProviders() {
    return useQuery({
        queryKey: ['auth', 'providers'],
        queryFn: async (): Promise<AuthProviders> => {
            try {
                const { data } = await api.get<Partial<AuthProviders>>('/api/auth/providers');
                return {
                    facebook: data?.facebook === true,
                    google: data?.google === true,
                    linkedin: data?.linkedin === true,
                    github: data?.github === true,
                };
            } catch {
                return NONE;
            }
        },
        // Which providers a deployment configured changes on restart, not while a login page is
        // open, and this runs before there is a session to invalidate anything.
        staleTime: Infinity,
        // The catch above already turned every failure into NONE, so a retry would only re-run a
        // request whose answer is already decided.
        retry: false,
    });
}

/**
 * Where the browser goes to start an external sign-in.
 *
 * A full navigation, not an XHR: the flow is an OAuth redirect that sets cookies on the API origin
 * and comes back to the callback, so it has to leave the SPA. The path is built from the same base
 * the axios client uses, because the admin picks its API at runtime from `window._env_`.
 */
export function externalSignInUrl(provider: keyof AuthProviders): string {
    return `${getApiUrl()}/api/auth/${provider}/start`;
}
