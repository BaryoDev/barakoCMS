import { test, expect } from '@playwright/test';
import { MOCK_TOKEN } from './login.spec';

const SCHEMAS = [
    {
        name: 'blog-post',
        displayName: 'Blog post',
        description: 'Articles',
        fields: [{ name: 'Title', displayName: 'Title', type: 'string', isRequired: true }],
    },
];

test.describe('Admin flows (mocked API)', () => {
    test.beforeEach(async ({ page }) => {
        await page.addInitScript((token) => {
            window.localStorage.setItem('barako_token', token);
        }, MOCK_TOKEN);

        await page.route('**/api/schemas', (route) =>
            route.fulfill({ json: SCHEMAS })
        );
        await page.route('**/api/workflows', (route) => route.fulfill({ json: [] }));
        await page.route('**/api/monitoring/**', (route) => route.fulfill({ json: {} }));
        await page.route('**/health', (route) =>
            route.fulfill({ json: { status: 'Healthy', totalDuration: '0', entries: {} } })
        );
        await page.route('**/api/contents**', (route) =>
            route.fulfill({
                json: {
                    items: [],
                    page: 1,
                    pageSize: 20,
                    totalItems: 0,
                    totalPages: 0,
                    hasNextPage: false,
                    hasPreviousPage: false,
                },
            })
        );
    });

    test('sidebar navigates between feature pages', async ({ page }) => {
        await page.goto('/');
        await expect(page.getByRole('heading', { name: 'Overview' })).toBeVisible();

        await page.getByRole('link', { name: 'Content types' }).click();
        await expect(page).toHaveURL('/schemas');
        await expect(page.getByText('Blog post')).toBeVisible();
    });

    test('creating a content type posts to /api/content-types with PascalCase fields', async ({ page }) => {
        await page.route('**/api/content-types', (route) =>
            route.fulfill({ json: { id: 'ct1', name: 'secret-doc' } })
        );

        await page.goto('/schemas/new');
        await page.getByLabel('Display name').fill('Secret doc');
        await expect(page.getByLabel('API name (slug)')).toHaveValue('secret-doc');

        // Add a field via the dialog; the API name auto-PascalCases.
        await page.getByRole('button', { name: 'Add field' }).first().click();
        await page.getByLabel('Display name').last().fill('Document title');
        await expect(page.getByLabel('Field name (API)')).toHaveValue('DocumentTitle');
        await page.getByRole('dialog').getByRole('button', { name: 'Add field' }).click();

        const requestPromise = page.waitForRequest(
            (request) => request.url().includes('/api/content-types') && request.method() === 'POST'
        );
        await page.getByRole('button', { name: 'Create content type' }).click();
        const request = await requestPromise;
        const body = request.postDataJSON();

        expect(body.name).toBe('secret-doc');
        expect(body.fields[0].name).toBe('DocumentTitle');
        expect(body.fields[0].type).toBe('string');
        await expect(page).toHaveURL('/schemas');
    });

    test('creating a role sends the ContentTypePermission structure', async ({ page }) => {
        await page.route('**/api/roles', (route) => {
            if (route.request().method() === 'POST') {
                return route.fulfill({ json: { id: 'r9', message: 'created' } });
            }
            return route.fulfill({
                json: { items: [], page: 1, pageSize: 20, totalItems: 0, totalPages: 0, hasNextPage: false, hasPreviousPage: false },
            });
        });

        await page.goto('/roles/new');
        await page.getByLabel('Name').fill('Editor');

        await page.getByLabel('Allow create on Blog post').click();
        await page.getByLabel('Allow read on Blog post').click();

        const requestPromise = page.waitForRequest(
            (request) => request.url().includes('/api/roles') && request.method() === 'POST'
        );
        await page.getByRole('button', { name: 'Create role' }).click();
        const body = (await requestPromise).postDataJSON();

        expect(body.name).toBe('Editor');
        expect(body.permissions).toHaveLength(1);
        expect(body.permissions[0].contentTypeSlug).toBe('blog-post');
        expect(body.permissions[0].create.enabled).toBe(true);
        expect(body.permissions[0].read.enabled).toBe(true);
        expect(body.permissions[0].delete.enabled).toBe(false);
    });
});
