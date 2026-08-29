import type { Page } from '@playwright/test';

// A structurally valid JWT the UI can decode (the client never verifies the signature). Lives here,
// not in a *.spec.ts, because Playwright forbids one test file importing another.
const payload = Buffer.from(
    JSON.stringify({
        UserId: '00000000-0000-0000-0000-000000000001',
        Username: 'admin',
        tenant: 'default',
        'http://schemas.microsoft.com/ws/2008/06/identity/claims/role': ['SuperAdmin'],
    })
).toString('base64url');

export const MOCK_TOKEN = `eyJhbGciOiJIUzI1NiJ9.${payload}.sig`;

/** Seed the auth token so a page loads authenticated. Call before page.goto. */
export function authed(page: Page) {
    return page.addInitScript((token) => {
        window.localStorage.setItem('barako_token', token);
    }, MOCK_TOKEN);
}

/** Stub the always-loaded shell calls so an unrelated 500 doesn't disturb the page under test.
 *  Monitoring returns real-shaped objects (not {}), so a page that reads metric fields — the
 *  dashboard formats errorRate/totalRequests — renders instead of crashing on undefined. */
export async function stubShell(page: Page) {
    // Register the generic monitoring stub first so the specific metrics/health routes below,
    // registered later, take precedence for their URLs (Playwright checks newest routes first).
    await page.route('**/api/monitoring/**', (r) => r.fulfill({ json: {} }));
    await page.route('**/api/monitoring/metrics**', (r) =>
        r.fulfill({ json: { totalRequests: 0, totalErrors: 0, averageResponseTime: 0, errorRate: 0 } })
    );
    await page.route('**/api/monitoring/health**', (r) =>
        r.fulfill({ json: { status: 'Healthy', totalDuration: '0', entries: {} } })
    );
    await page.route('**/health**', (r) => r.fulfill({ json: { status: 'Healthy', entries: {} } }));
    await page.route('**/api/me/tenants**', (r) => r.fulfill({ json: pageOf([]) }));
}

export const EMPTY_PAGE = pageOf([]);

/** Wrap items in the envelope every collection endpoint returns.
 *
 *  These specs mock the API, so a mock returning the wrong shape is a spec passing against a
 *  contract the server does not have. Nine endpoints stopped returning bare arrays in 4.0, and the
 *  mocks that still returned them were describing an API that no longer exists. One helper means
 *  the next shape change is one edit rather than a hunt. */
export function pageOf<T>(items: T[], pageSize = 100) {
    return {
        items,
        page: 1,
        pageSize,
        totalItems: items.length,
        totalPages: items.length === 0 ? 0 : Math.ceil(items.length / pageSize),
        hasNextPage: false,
        hasPreviousPage: false,
    };
}
