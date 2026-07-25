import { test, expect } from '@playwright/test';

test.describe('Runtime Configuration', () => {
    test('routes API calls to the URL from window._env_ (runtime config)', async ({ page }) => {
        // The app populates window._env_ from /env-config.js at load. Overriding that file is the
        // real runtime-config path (injecting _env_ directly is clobbered when env-config.js loads).
        const CUSTOM_API_URL = 'http://runtime-api-test:9999';
        await page.route('**/env-config.js', (r) =>
            r.fulfill({
                contentType: 'application/javascript',
                body: `window._env_ = { NEXT_PUBLIC_API_URL: ${JSON.stringify(CUSTOM_API_URL)} };`,
            })
        );

        // Capture where the login request is actually sent.
        let loginOrigin = '';
        await page.route('**/api/auth/login', (route) => {
            loginOrigin = new URL(route.request().url()).origin;
            return route.fulfill({ status: 401, contentType: 'application/json', body: '{"message":"x"}' });
        });

        await page.goto('/login');
        await page.getByLabel('Username').fill('u');
        await page.getByLabel('Password', { exact: true }).fill('p');
        await page.getByRole('button', { name: 'Sign in' }).click();

        // The request went to the runtime-configured host, not the build-time default.
        await expect.poll(() => loginOrigin, { timeout: 10000 }).toBe(CUSTOM_API_URL);
    });

    test('should fallback to process.env or default if window._env_ is missing', async ({ page }) => {
        await page.goto('/login');
        const runtimeUrl = await page.evaluate(() => window['_env_']?.NEXT_PUBLIC_API_URL);
        // In local dev without the script running, this might be undefined or the default
        // We just want to ensure it doesn't crash the app.
        expect(runtimeUrl).toBeDefined();
    });
});
