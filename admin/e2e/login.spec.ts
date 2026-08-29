import { test, expect } from '@playwright/test';
import { MOCK_TOKEN, stubShell, EMPTY_PAGE, pageOf } from './helpers';


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
        await page.getByLabel('Password', { exact: true }).fill('wrongpass');
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
        // Stub the shell (monitoring/health/tenants return objects, not arrays) and the
        // dashboard's own queries so the authenticated page renders. A blanket [] for every
        // endpoint crashes it — some hooks read object fields off the response.
        await stubShell(page);
        await page.route('**/api/schemas**', (r) => r.fulfill({ json: pageOf([]) }));
        await page.route('**/api/workflows**', (r) => r.fulfill({ json: pageOf([]) }));
        await page.route('**/api/contents**', (r) => r.fulfill({ json: EMPTY_PAGE }));

        await page.goto('/login');
        await page.getByLabel('Username').fill('admin');
        await page.getByLabel('Password', { exact: true }).fill('admin');
        await page.getByRole('button', { name: 'Sign in' }).click();

        await expect(page).toHaveURL('/', { timeout: 10000 });
        await expect(page.getByRole('heading', { name: 'Overview' })).toBeVisible();
    });

    test('two-factor: password step asks for a code instead of signing in', async ({ page }) => {
        // The API answers a correct password with 200 and NO tokens when MFA is enrolled.
        await page.route('**/api/auth/login', (route) =>
            route.fulfill({
                status: 200,
                contentType: 'application/json',
                body: JSON.stringify({
                    token: '',
                    refreshToken: '',
                    requiresMfa: true,
                    mfaChallengeToken: 'challenge-abc',
                }),
            })
        );

        await page.goto('/login');
        await page.getByLabel('Username').fill('admin');
        await page.getByLabel('Password', { exact: true }).fill('correct-password');
        await page.getByRole('button', { name: 'Sign in' }).click();

        // Must stay on /login showing the code step — not navigate, and not store a session.
        await expect(page.getByLabel('Authentication code')).toBeVisible({ timeout: 10000 });
        await expect(page).toHaveURL(/\/login/);
        expect(await page.evaluate(() => window.localStorage.getItem('barako_token'))).toBeFalsy();
    });

    test('two-factor: a valid code completes the sign-in', async ({ page }) => {
        await page.route('**/api/auth/login', (route) =>
            route.fulfill({
                status: 200,
                contentType: 'application/json',
                body: JSON.stringify({ token: '', refreshToken: '', requiresMfa: true, mfaChallengeToken: 'challenge-abc' }),
            })
        );
        await page.route('**/api/auth/mfa/verify', (route) =>
            route.fulfill({
                status: 200,
                contentType: 'application/json',
                body: JSON.stringify({
                    token: MOCK_TOKEN,
                    expiry: new Date(Date.now() + 900_000).toISOString(),
                    refreshToken: 'mock-refresh',
                    refreshTokenExpiry: new Date(Date.now() + 7 * 86400_000).toISOString(),
                }),
            })
        );
        await stubShell(page);
        await page.route('**/api/schemas**', (r) => r.fulfill({ json: pageOf([]) }));
        await page.route('**/api/workflows**', (r) => r.fulfill({ json: pageOf([]) }));
        await page.route('**/api/contents**', (r) => r.fulfill({ json: EMPTY_PAGE }));

        await page.goto('/login');
        await page.getByLabel('Username').fill('admin');
        await page.getByLabel('Password', { exact: true }).fill('correct-password');
        await page.getByRole('button', { name: 'Sign in' }).click();

        await page.getByLabel('Authentication code').fill('123456');
        await page.getByRole('button', { name: 'Verify' }).click();

        await expect(page).toHaveURL('/', { timeout: 10000 });
        await expect(page.getByRole('heading', { name: 'Overview' })).toBeVisible();
    });
});
