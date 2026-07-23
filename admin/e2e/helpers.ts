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

/** Stub the always-loaded shell calls so an unrelated 500 doesn't disturb the page under test. */
export async function stubShell(page: Page) {
    await page.route('**/api/monitoring/**', (r) => r.fulfill({ json: {} }));
    await page.route('**/health**', (r) => r.fulfill({ json: { status: 'Healthy', entries: {} } }));
    await page.route('**/api/me/tenants**', (r) => r.fulfill({ json: [] }));
}

export const EMPTY_PAGE = {
    items: [],
    page: 1,
    pageSize: 20,
    totalItems: 0,
    totalPages: 0,
    hasNextPage: false,
    hasPreviousPage: false,
};
