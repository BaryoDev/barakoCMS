import { test, expect } from '@playwright/test';

// A structurally valid JWT the UI can decode (signature is never verified client-side).
const payload = Buffer.from(
    JSON.stringify({
        UserId: '00000000-0000-0000-0000-000000000001',
        Username: 'admin',
        'http://schemas.microsoft.com/ws/2008/06/identity/claims/role': ['SuperAdmin'],
    })
).toString('base64url');
export const MOCK_TOKEN = `eyJhbGciOiJIUzI1NiJ9.${payload}.sig`;

test.describe('Login & Authentication', () => {
    test.beforeEach(async ({ page }) => {
        await page.addInitScript(() => {
            window.localStorage.clear();
        });
    });

    test('should show login page when unauthenticated', async ({ page }) => {
        await page.goto('/');
        await expect(page).toHaveURL(/\/login/);
        await expect(page.getByText('Sign in to manage your content')).toBeVisible({ timeout: 10000 });
    });

    test('should show error with invalid credentials', async ({ page }) => {
        await page.route('**/api/auth/login', async (route) => {
            await route.fulfill({
                status: 401,
                contentType: 'application/json',
                body: JSON.stringify({ message: 'Invalid username or password' }),
            });
        });

        await page.goto('/login');
        await page.getByLabel('Username').fill('wronguser');
        await page.getByLabel('Password').fill('wrongpass');
        await page.getByRole('button', { name: 'Sign in' }).click();

        // Errors surface as a sonner toast with the API's message.
        await expect(page.getByText('Invalid username or password')).toBeVisible({ timeout: 10000 });
    });

    test('should login successfully and land on the dashboard', async ({ page }) => {
        await page.route('**/api/auth/login', async (route) => {
            await route.fulfill({
                status: 200,
                contentType: 'application/json',
                body: JSON.stringify({
                    token: MOCK_TOKEN,
                    expiry: new Date(Date.now() + 900_000).toISOString(),
                    refreshToken: 'mock-refresh',
                    refreshTokenExpiry: new Date(Date.now() + 7 * 86400_000).toISOString(),
                }),
            });
        });
        // Dashboard queries can fail silently; the shell should still render.
        await page.route('**/api/**', async (route) => {
            if (route.request().url().includes('/api/auth/login')) return route.fallback();
            await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
        });

        await page.goto('/login');
        await page.getByLabel('Username').fill('admin');
        await page.getByLabel('Password').fill('admin');
        await page.getByRole('button', { name: 'Sign in' }).click();

        await expect(page).toHaveURL('/', { timeout: 10000 });
        await expect(page.getByRole('heading', { name: 'Overview' })).toBeVisible();
    });
});
