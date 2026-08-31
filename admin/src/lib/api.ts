import axios from 'axios';

export function getApiUrl(): string {
    return (
        (typeof window !== 'undefined' && window._env_?.NEXT_PUBLIC_API_URL) ||
        process.env.NEXT_PUBLIC_API_URL ||
        'http://localhost:5006'
    );
}

const TOKEN_KEY = 'barako_token';
const REFRESH_KEY = 'barako_refresh';

/**
 * The access token, in memory only.
 *
 * Both tokens used to live in localStorage, which any script on the origin can read. The access
 * token is a 15 minute credential and has to be readable, because it is sent as a bearer. The
 * refresh token was the real loss: seven days, renewable, and rotation does not help an attacker who
 * simply keeps refreshing. One XSS, or one compromised dependency in this build, was a week of
 * account takeover.
 *
 * So the refresh token is now an httpOnly cookie the server sets and this code never sees, and the
 * access token is a module variable rather than storage. A reload has no token and silently refreshes
 * to get one, which the cookie makes possible without anything being persisted here.
 *
 * The cost is honest and small: a second tab starts without a token and refreshes once, and closing
 * every tab ends the in-memory half. The refresh cookie is what carries the session, not this.
 */
let accessToken: string | null = null;

export const tokenStore = {
    get token() {
        return accessToken;
    },
    set(token: string) {
        accessToken = token;
        notifyAuthChange();
    },
    clear() {
        accessToken = null;
        // Left over from the versions that persisted tokens. Removing them means an upgrade does not
        // leave a week-long refresh token sitting in storage for a later XSS to find.
        if (typeof window !== 'undefined') {
            localStorage.removeItem(TOKEN_KEY);
            localStorage.removeItem(REFRESH_KEY);
        }
        notifyAuthChange();
    },
};

const AUTH_EVENT = 'barako-auth-change';

function notifyAuthChange() {
    window.dispatchEvent(new Event(AUTH_EVENT));
}

// Subscribe/read pair for useSyncExternalStore; 'storage' covers other tabs.
export function subscribeToAuth(callback: () => void) {
    window.addEventListener(AUTH_EVENT, callback);
    window.addEventListener('storage', callback);
    return () => {
        window.removeEventListener(AUTH_EVENT, callback);
        window.removeEventListener('storage', callback);
    };
}

export const api = axios.create({
    headers: {
        'Content-Type': 'application/json',
    },
});

/** The tenant a token was minted for, read from its `tenant` claim (null for a legacy/global token). */
export function tenantOfToken(token: string | null): string | null {
    if (!token) return null;
    try {
        const payload = JSON.parse(atob(token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')));
        return typeof payload.tenant === 'string' ? payload.tenant : null;
    } catch {
        return null;
    }
}

api.interceptors.request.use((config) => {
    config.baseURL = getApiUrl();
    // The refresh cookie is httpOnly and scoped to /api/auth/refresh, so it only rides along there,
    // but the flag has to be on for the browser to send it at all.
    config.withCredentials = true;
    const token = tokenStore.token;
    if (token) {
        config.headers.Authorization = `Bearer ${token}`;
        // Multi-tenant deployments scope data by the X-Tenant header. Derive it from the token's own
        // tenant claim so the header always matches what the token was minted for — the API's
        // tenant-access guard rejects a mismatch. Switching tenants swaps the token (see useSwitchTenant),
        // which automatically changes this header too.
        const tenant = tenantOfToken(token);
        if (tenant) {
            config.headers['X-Tenant'] = tenant;
        }
    }
    return config;
});

// Access tokens expire after 15 minutes; the backend rotates refresh tokens
// (7-day expiry, single use). On 401 we refresh once — single-flight so
// concurrent 401s share one refresh call and don't trip reuse detection.
let refreshPromise: Promise<string | null> | null = null;

async function refreshAccessToken(): Promise<string | null> {
    try {
        // Carry the current tenant into the refresh: the API mints the new token for the tenant in
        // X-Tenant, so without it a refreshed token would silently revert to the default tenant and
        // lose the club the user switched into.
        const tenant = tenantOfToken(tokenStore.token);

        // No body and withCredentials, so the httpOnly cookie carries the refresh token. This code
        // cannot read it, which is the entire point: an XSS arriving after sign-in has nothing to
        // steal. The response still contains a refresh token for non-browser callers, and it is
        // deliberately not stored.
        const { data } = await axios.post(
            `${getApiUrl()}/api/auth/refresh`,
            {},
            { withCredentials: true, headers: tenant ? { 'X-Tenant': tenant } : undefined },
        );
        tokenStore.set(data.token);
        return data.token as string;
    } catch {
        tokenStore.clear();
        return null;
    }
}

/**
 * One silent refresh on first load, so a reload is not a sign-out.
 *
 * The access token lives in memory, so every page load starts with none. The refresh cookie is what
 * carries the session across that gap, and this is where it gets used: without it the app would
 * decide you are signed out the moment you press reload, which is a regression dressed up as a
 * security improvement.
 *
 * Runs once per page load and is shared, so ten components mounting at once produce one request
 * rather than ten, and a failure is a normal signed-out state rather than an error.
 */
let bootstrapPromise: Promise<void> | null = null;

export function ensureSession(): Promise<void> {
    if (typeof window === 'undefined') return Promise.resolve();

    bootstrapPromise ??= (async () => {
        if (accessToken) return;
        await refreshAccessToken();
    })();

    return bootstrapPromise;
}

/** Test hook: forget the one-shot bootstrap so a spec can drive it again. */
export function __resetSessionBootstrapForTests() {
    bootstrapPromise = null;
}

api.interceptors.response.use(
    (response) => response,
    async (error) => {
        const original = error.config;
        if (
            error.response?.status === 401 &&
            typeof window !== 'undefined' &&
            !original._retried &&
            !original.url?.includes('/api/auth/')
        ) {
            original._retried = true;
            refreshPromise ??= refreshAccessToken().finally(() => {
                refreshPromise = null;
            });
            const token = await refreshPromise;
            if (token) {
                original.headers.Authorization = `Bearer ${token}`;
                return api(original);
            }
            if (!window.location.pathname.startsWith('/login')) {
                window.location.href = '/login';
            }
        }
        return Promise.reject(error);
    }
);

// Every collection endpoint returns this envelope. The one deliberate exception is
// /api/public/{type}/search, which echoes a query rather than paging a set; see
// PublicSearchResponse for why.
//
// This comment used to name four endpoints and was wrong about one of them: /api/schemas returned
// a bare array. It is the thing a contributor reads to learn the convention, so it has to be true.
export interface Paginated<T> {
    items: T[];
    page: number;
    pageSize: number;
    totalItems: number;
    totalPages: number;
    hasNextPage: boolean;
    hasPreviousPage: boolean;
}

export interface PageParams {
    page?: number;
    pageSize?: number;
    // sortBy is gone in 4.0. The server accepted it everywhere and honoured it nowhere.
    sortOrder?: 'asc' | 'desc';
}

export function apiErrorMessage(error: unknown, fallback = 'Something went wrong'): string {
    if (axios.isAxiosError(error)) {
        const data = error.response?.data;
        if (typeof data === 'string' && data) return data;
        if (data?.message) return data.message;
        if (data?.errors) {
            const errs = data.errors;
            // ProblemDetails entries from FastEndpoints carry `reason`, not `message`. Reading
            // `message` first left `?? e` falling back to the object, so every validation failure
            // rendered as "[object Object]", including "Invalid credentials" on the login page.
            if (Array.isArray(errs)) {
                return errs
                    .map((e) => (typeof e === 'string' ? e : (e?.reason ?? e?.message ?? '')))
                    .filter(Boolean)
                    .join(', ') || fallback;
            }
            if (typeof errs === 'object') return Object.values(errs).flat().join(', ');
        }
        if (error.response?.status === 401) return 'Your session has expired. Sign in again.';
        if (error.response?.status === 403) return 'You do not have permission to do that.';
        if (error.response?.status === 412) return 'This item changed while you were editing. Reload and try again.';
        if (error.response?.status === 429) return 'Too many requests. Wait a moment and try again.';
    }
    return fallback;
}
