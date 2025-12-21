import { test, expect } from '@playwright/test';

test.describe('Admin UI E2E Verification', () => {
    // Mock API responses before each test
    test.beforeEach(async ({ page }) => {
        // Mock Login
        await page.route('**/api/auth/login', async route => {
            await route.fulfill({ json: { token: 'mock-jwt-token' } });
        });

        // Mock User/Role Data
        await page.route('**/api/users/me', async route => {
            await route.fulfill({ json: { id: '1', username: 'admin', roles: ['admin'] } });
        });

        await page.route('**/api/roles', async route => {
            if (route.request().method() === 'GET') {
                await route.fulfill({ json: [{ id: 'r1', name: 'Admin', permissions: ['*'] }] });
            } else if (route.request().method() === 'POST') {
                const data = route.request().postDataJSON();
                await route.fulfill({ json: { id: 'r2', ...data } });
            }
        });

        // Mock Schemas
        await page.route('**/api/schemas', async route => {
            if (route.request().method() === 'GET') {
                await route.fulfill({ json: [{ name: 'blog', displayName: 'Blog', fields: [] }] });
            } else if (route.request().method() === 'POST') {
                const data = route.request().postDataJSON();
                await route.fulfill({ json: { ...data } });
            }
        });

        // Mock Workflows
        await page.route('**/api/workflows', async route => {
            await route.fulfill({ json: [] });
        });
    });

    test('User can login and navigate to dashboard', async ({ page }) => {
        await page.goto('/login');
        await page.fill('input[name="username"]', 'admin');
        await page.fill('input[name="password"]', 'password');
        await page.click('button[type="submit"]');

        await expect(page).toHaveURL('/');
        await expect(page.locator('h1')).toContainText('Dashboard');
    });

    test('User can create a Role with Granular Permissions', async ({ page }) => {
        // Login bypass
        await page.addInitScript(() => {
            window.localStorage.setItem('barako_token', 'mock-token');
        });

        await page.goto('/roles/new');

        // Fill basic info
        await page.fill('#name', 'Editor');

        // Interact with Permission Matrix
        // The matrix should show "Blog" content type (mocked)
        await expect(page.getByText('Blog')).toBeVisible();

        // Check "Create" permission for Blog
        // We locate the checkbox within the row for "Blog" and column for "Create"
        // Since my matrix UI is complex, I'll access checkboxes by row/col index or accessible role if strictly defined, 
        // but here I'll try to click the checkbox associated with "create"
        // Actually, shadcn checkboxes are buttons or inputs.

        // Let's create a specific locator strategy:
        // Find row containing "Blog" -> find checkbox inside.
        // For simplicity in this assessment, we'll verify the checkboxes exist and are clickable.
        const checkboxes = page.locator('button[role="checkbox"]');
        await expect(checkboxes).toHaveCount(5); // 1 "All" + 4 (C/R/U/D)

        // Click the first one (All)
        await checkboxes.first().click();

        // Intercept the POST request to verify payload
        const requestPromise = page.waitForRequest(request =>
            request.url().includes('/api/roles') && request.method() === 'POST'
        );

        await page.click('button[type="submit"]');

        const request = await requestPromise;
        const postData = request.postDataJSON();

        expect(postData.name).toBe('Editor');
        expect(postData.permissions).toContain('contents:blog:create'); // "All" should select create
        expect(postData.permissions).toContain('contents:blog:read');
    });

    test('User can create a Content Type with Sensitivity', async ({ page }) => {
        await page.addInitScript(() => {
            window.localStorage.setItem('barako_token', 'mock-token');
        });

        await page.goto('/schemas/new');

        await page.fill('#displayName', 'Secret Doc');
        // Slug should auto-fill
        await expect(page.locator('#name')).toHaveValue('secret-doc');

        // Select Sensitivity
        await page.click('button[role="combobox"]'); // Select trigger
        await page.click('div[role="option"]:has-text("Confidential")');

        // Add a field
        // Add a field
        await page.click('button:has-text("Add Field")'); // Main button to open dialog
        await page.fill('input[id="field-displayName"]', 'Title'); // Dialog input
        await page.fill('input[id="field-name"]', 'title'); // Dialog input (required)
        await page.click('div[role="dialog"] button:has-text("Add Field")'); // Dialog confirm button

        // Submit
        const requestPromise = page.waitForRequest(request =>
            request.url().includes('/api/schemas') && request.method() === 'POST'
        );

        await page.click('button:has-text("Create Content Type")');

        const request = await requestPromise;
        const postData = request.postDataJSON();

        expect(postData.name).toBe('secret-doc');
        expect(postData.sensitivity).toBe('confidential');
        expect(postData.fields).toHaveLength(1);
    });

    test('User can view Ops Dashboard Check items', async ({ page }) => {
        await page.addInitScript(() => {
            window.localStorage.setItem('barako_token', 'mock-token');
        });

        await page.goto('/ops/health');
        await expect(page.locator('h1')).toContainText('System Health');
        await expect(page.locator('table')).toContainText('barako-api'); // Mock pod name
    });
});
