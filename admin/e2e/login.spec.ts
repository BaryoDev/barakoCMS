import { test, expect } from '@playwright/test';
import { MOCK_TOKEN, stubShell, EMPTY_PAGE, pageOf, stubContentTypes } from './helpers';


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
        await stubContentTypes(page);
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
        await stubContentTypes(page);
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

    test('device approval: an unapproved device asks for the emailed code', async ({ page }) => {
        await page.route('**/api/auth/login', (route) =>
            route.fulfill({
                status: 200,
                contentType: 'application/json',
                body: JSON.stringify({
                    token: '',
                    refreshToken: '',
                    requiresDeviceApproval: true,
                    message: "This device isn't approved yet. Enter the code we emailed to approve it.",
                    email: 'admin@example.com',
                }),
            })
        );

        await page.goto('/login');
        await page.getByLabel('Username').fill('admin');
        await page.getByLabel('Password', { exact: true }).fill('correct-password');
        await page.getByRole('button', { name: 'Sign in' }).click();

        // Before this existed the page showed a toast and stopped, so turning on
        // DeviceTrust__Enforce locked every administrator out with no way back in.
        await expect(page.getByLabel('Device approval code')).toBeVisible();
        await expect(page.getByText('admin@example.com')).toBeVisible();
    });

    test('device approval: a valid code completes the sign-in', async ({ page }) => {
        await page.route('**/api/auth/login', (route) =>
            route.fulfill({
                status: 200,
                contentType: 'application/json',
                body: JSON.stringify({
                    token: '',
                    refreshToken: '',
                    requiresDeviceApproval: true,
                    email: 'admin@example.com',
                }),
            })
        );
        await page.route('**/api/auth/otp/verify', (route) =>
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
        await stubContentTypes(page);
        await page.route('**/api/workflows**', (r) => r.fulfill({ json: pageOf([]) }));
        await page.route('**/api/contents**', (r) => r.fulfill({ json: EMPTY_PAGE }));

        await page.goto('/login');
        await page.getByLabel('Username').fill('admin');
        await page.getByLabel('Password', { exact: true }).fill('correct-password');
        await page.getByRole('button', { name: 'Sign in' }).click();

        await page.getByLabel('Device approval code').fill('123456');
        await page.getByRole('button', { name: 'Approve device' }).click();

        await expect(page).toHaveURL('/', { timeout: 10000 });
    });

    /**
     * A correct email code on an account with MFA enabled owes a second factor.
     *
     * Worth its own test because the tempting implementation treats any 200 from otp/verify as
     * signed in, and that response carries no tokens. Mailbox possession is a first factor and
     * cannot stand in for the enrolled second one.
     */
    test('device approval: an MFA account is handed to the authenticator step, not signed in', async ({ page }) => {
        await page.route('**/api/auth/login', (route) =>
            route.fulfill({
                status: 200,
                contentType: 'application/json',
                body: JSON.stringify({
                    token: '',
                    refreshToken: '',
                    requiresDeviceApproval: true,
                    email: 'admin@example.com',
                }),
            })
        );
        await page.route('**/api/auth/otp/verify', (route) =>
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

        await page.getByLabel('Device approval code').fill('123456');
        await page.getByRole('button', { name: 'Approve device' }).click();

        await expect(page.getByLabel('Authentication code')).toBeVisible();
        await expect(page).not.toHaveURL('/');
    });

});
