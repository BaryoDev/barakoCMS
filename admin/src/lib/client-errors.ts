import { getApiUrl, tokenStore, tenantOfToken } from './api';

/**
 * Browser-side capture for the Diagnostics module. Queues faults and POSTs them in small batches to
 * `/api/client-errors`, which dedupes by fingerprint server-side.
 *
 * The hard constraint here is that an error reporter must never become a source of errors. So:
 * sending uses raw `fetch` rather than the shared axios instance (whose 401 interceptor would try to
 * refresh a token and could re-enter), every failure is swallowed, and the send path is never allowed
 * to reject. A per-session cap and a message-level dedupe stop an error loop from flooding the API.
 */

export type ClientErrorKind = 'error' | 'unhandledrejection' | 'react' | 'api';
export type ClientErrorSeverity = 'error' | 'warning';

export interface ClientErrorReport {
    kind: ClientErrorKind;
    severity?: ClientErrorSeverity;
    message: string;
    stack?: string;
    source?: string;
    status?: number;
    url?: string;
    appVersion?: string;
    tenant?: string;
}

/** Matches ClientErrorRecorder.MaxItems on the server — a larger batch is truncated there anyway. */
const MAX_BATCH = 20;
/** Stop after this many sends per page session, so a render loop can't hammer the endpoint. */
const MAX_PER_SESSION = 25;
const FLUSH_DELAY_MS = 2000;

let queue: ClientErrorReport[] = [];
let timer: ReturnType<typeof setTimeout> | null = null;
let sentThisSession = 0;
const seen = new Set<string>();

function fingerprint(r: ClientErrorReport) {
    return `${r.kind}|${r.message}|${r.source ?? ''}`;
}

/** Queue a fault. Safe to call from anywhere; never throws. */
export function reportClientError(report: ClientErrorReport) {
    try {
        if (sentThisSession >= MAX_PER_SESSION) return;
        if (!report.message) return;

        const key = fingerprint(report);
        if (seen.has(key)) return; // the server dedupes too; this saves the round trip
        seen.add(key);

        queue.push({
            severity: 'error',
            url: typeof window !== 'undefined' ? window.location.href : undefined,
            tenant: tenantOfToken(tokenStore.token) ?? undefined,
            ...report,
        });

        if (queue.length >= MAX_BATCH) {
            void flushClientErrors();
        } else if (!timer) {
            timer = setTimeout(() => void flushClientErrors(), FLUSH_DELAY_MS);
        }
    } catch {
        // Never let capture itself surface an error.
    }
}

/** Send whatever is queued. Resolves even when the request fails — failures are intentionally silent. */
export async function flushClientErrors(): Promise<void> {
    if (timer) {
        clearTimeout(timer);
        timer = null;
    }
    if (queue.length === 0) return;

    const items = queue.slice(0, MAX_BATCH);
    queue = queue.slice(MAX_BATCH);
    sentThisSession += 1;

    try {
        const headers: Record<string, string> = { 'Content-Type': 'application/json' };
        // Identity is best-effort on the server; attach it when we have it so errors can be attributed.
        const token = tokenStore.token;
        if (token) {
            headers.Authorization = `Bearer ${token}`;
            const tenant = tenantOfToken(token);
            if (tenant) headers['X-Tenant'] = tenant;
        }

        await fetch(`${getApiUrl()}/api/client-errors`, {
            method: 'POST',
            headers,
            body: JSON.stringify({ items }),
            // Let the report survive a navigation away from the failing page.
            keepalive: true,
        });
    } catch {
        // Swallow. Reporting a reporting failure is how you build an infinite loop.
    }
}

/**
 * Attach the global listeners. Returns a cleanup function. Calling it twice is harmless — the second
 * call replaces the first set of listeners.
 */
export function installClientErrorHandlers(): () => void {
    if (typeof window === 'undefined') return () => {};

    const onError = (event: ErrorEvent) => {
        // Resource load failures (img/script) also fire 'error' but carry no Error object.
        const message = event.error?.message || event.message || 'Unknown error';
        reportClientError({
            kind: 'error',
            message,
            stack: event.error?.stack,
            source: event.filename || undefined,
        });
    };

    const onRejection = (event: PromiseRejectionEvent) => {
        const reason = event.reason;
        const message =
            (reason instanceof Error ? reason.message : typeof reason === 'string' ? reason : null) ||
            'Unhandled promise rejection';
        reportClientError({
            kind: 'unhandledrejection',
            message,
            stack: reason instanceof Error ? reason.stack : undefined,
        });
    };

    // Best effort: get anything queued out before the tab goes away.
    const onHidden = () => {
        if (document.visibilityState === 'hidden') void flushClientErrors();
    };

    window.addEventListener('error', onError);
    window.addEventListener('unhandledrejection', onRejection);
    document.addEventListener('visibilitychange', onHidden);

    return () => {
        window.removeEventListener('error', onError);
        window.removeEventListener('unhandledrejection', onRejection);
        document.removeEventListener('visibilitychange', onHidden);
    };
}

/** Test seam: clear queue, dedupe set and session counter. */
export function __resetClientErrorsForTests() {
    queue = [];
    seen.clear();
    sentThisSession = 0;
    if (timer) {
        clearTimeout(timer);
        timer = null;
    }
}
