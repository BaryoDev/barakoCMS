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

export const tokenStore = {
    get token() {
        return typeof window !== 'undefined' ? localStorage.getItem(TOKEN_KEY) : null;
    },
    get refreshToken() {
        return typeof window !== 'undefined' ? localStorage.getItem(REFRESH_KEY) : null;
    },
    set(token: string, refreshToken?: string) {
        localStorage.setItem(TOKEN_KEY, token);
        if (refreshToken) localStorage.setItem(REFRESH_KEY, refreshToken);
        notifyAuthChange();
    },
    clear() {
        localStorage.removeItem(TOKEN_KEY);
        localStorage.removeItem(REFRESH_KEY);
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

api.interceptors.request.use((config) => {
    config.baseURL = getApiUrl();
    const token = tokenStore.token;
    if (token) {
        config.headers.Authorization = `Bearer ${token}`;
    }
    return config;
});

// Access tokens expire after 15 minutes; the backend rotates refresh tokens
// (7-day expiry, single use). On 401 we refresh once — single-flight so
// concurrent 401s share one refresh call and don't trip reuse detection.
let refreshPromise: Promise<string | null> | null = null;

async function refreshAccessToken(): Promise<string | null> {
    const refreshToken = tokenStore.refreshToken;
    if (!refreshToken) return null;
    try {
        const { data } = await axios.post(`${getApiUrl()}/api/auth/refresh`, { refreshToken });
        tokenStore.set(data.token, data.refreshToken);
        return data.token as string;
    } catch {
        tokenStore.clear();
        return null;
    }
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

// Backend list endpoints (contents, users, roles, content-types) return this envelope.
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
    sortBy?: string;
    sortOrder?: 'asc' | 'desc';
}

export function apiErrorMessage(error: unknown, fallback = 'Something went wrong'): string {
    if (axios.isAxiosError(error)) {
        const data = error.response?.data;
        if (typeof data === 'string' && data) return data;
        if (data?.message) return data.message;
        if (data?.errors) {
            const errs = data.errors;
            if (Array.isArray(errs)) return errs.map((e) => e.message ?? e).join(', ');
            if (typeof errs === 'object') return Object.values(errs).flat().join(', ');
        }
        if (error.response?.status === 401) return 'Your session has expired. Sign in again.';
        if (error.response?.status === 403) return 'You do not have permission to do that.';
        if (error.response?.status === 412) return 'This item changed while you were editing. Reload and try again.';
        if (error.response?.status === 429) return 'Too many requests. Wait a moment and try again.';
    }
    return fallback;
}
