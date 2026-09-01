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

/**
 * Make a page load authenticated, the way a real browser now does.
 *
 * Seeding localStorage no longer works and should not: the access token lives in memory and the
 * refresh cookie carries the session, so a page load starts with no token and does one silent
 * refresh to get one. Stubbing that refresh is the honest simulation, and it exercises the bootstrap
 * path on every authenticated spec rather than leaving it untested.
 *
 * Call before page.goto.
 */
export async function authed(page: Page) {
    await page.route('**/api/auth/refresh', (route) =>
        route.fulfill({
            json: {
                token: MOCK_TOKEN,
                expiry: new Date(Date.now() + 900_000).toISOString(),
                refreshToken: 'mock-refresh',
                refreshTokenExpiry: new Date(Date.now() + 7 * 86400_000).toISOString(),
            },
        })
    );
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

    // The sidebar rail's counts and badges. These are shell calls now (they run on every admin
    // route, not just the page that owns the list), so a spec that does not care about them still
    // has to answer them, or the rail renders no count and the screenshots show an empty rail.
    // Registered here first, so a spec's own route for the same URL still wins.
    await page.route('**/api/meta**', (r) =>
        r.fulfill({ json: { version: '4.0.0', swaggerEnabled: false } })
    );
    await page.route('**/api/contents**', (r) => r.fulfill({ json: countOf(148) }));
    await page.route(/\/api\/content-types(\?|$)/, (r) => r.fulfill({ json: countOf(6) }));
    await page.route('**/api/workflows**', (r) => r.fulfill({ json: countOf(3) }));
    await page.route('**/api/client-errors**', (r) => r.fulfill({ json: countOf(0) }));
    await page.route('**/api/email-events**', (r) => r.fulfill({ json: [] }));
}

/** The pagination envelope as a count query sees it: one row asked for, the real total reported. */
function countOf(totalItems: number) {
    return {
        items: [],
        page: 1,
        pageSize: 1,
        totalItems,
        totalPages: totalItems,
        hasNextPage: totalItems > 1,
        hasPreviousPage: false,
    };
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

/** Stub the content-type list on both of its route names.
 *
 *  4.0 consolidated the resource on GET /api/content-types and kept /api/schemas as a deprecated
 *  alias until 5.0. The admin calls the new name. A mock that only knows the old one returns
 *  nothing, the page renders its empty state, and the spec looks like it passed. */
export async function stubContentTypes(page: Page, items: unknown[] = []) {
    await page.route(/\/api\/content-types(\?|$)/, (r) => r.fulfill({ json: pageOf(items) }));
    await page.route('**/api/schemas**', (r) => r.fulfill({ json: pageOf(items) }));
}
