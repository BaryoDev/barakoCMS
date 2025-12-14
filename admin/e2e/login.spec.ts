import { test, expect } from '@playwright/test';

test.describe('Login & Authentication', () => {
    test.beforeEach(async ({ page }) => {
        // Clear local storage to ensure fresh state
        await page.addInitScript(() => {
            window.localStorage.clear();
        });
    });

    test('should show login page when unauthenticated', async ({ page }) => {
        await page.goto('/');
        await expect(page).toHaveURL(/\/login/);
        await expect(page.getByText('Welcome back')).toBeVisible({ timeout: 10000 });
    });

    test('should show error with invalid credentials', async ({ page }) => {
        // Mock 401 response
        await page.route('**/api/auth/login', async route => {
            await route.fulfill({
                status: 401,
                contentType: 'application/json',
                body: JSON.stringify({ message: 'Unauthorized' }),
            });
        });

        await page.goto('/login');

        await page.getByLabel('Username').fill('wronguser');
        await page.getByLabel('Password').fill('wrongpass');
        await page.getByRole('button', { name: 'Sign in' }).click();

        // The error message might appear in a dedicated error box or toast
        // Based on page.tsx, it renders: 
        // <div className="... text-red-400 ...">Invalid username or password</div>
        // Let's maximize timeout or use a more specific locator if needed.
        await expect(page.getByText('Invalid username or password')).toBeVisible({ timeout: 10000 });
    });

    test('should login successfully with correct credentials', async ({ page }) => {
        // Mock the API response for successful login
        await page.route('**/api/auth/login', async route => {
            await route.fulfill({
                status: 200,
                contentType: 'application/json',
                body: JSON.stringify({ token: 'mock-token-xyz' }),
            });
        });

        await page.goto('/login');
        await page.getByLabel('Username').fill('admin');
        await page.getByLabel('Password').fill('admin');
        await page.getByRole('button', { name: 'Sign in' }).click();

        await expect(page).toHaveURL('/', { timeout: 10000 });
    });
});
