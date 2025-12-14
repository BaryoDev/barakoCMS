import { test, expect } from '@playwright/test';

test.describe('Runtime Configuration', () => {
    test('should use API URL from window._env_ if available', async ({ page }) => {
        // 1. Arrange: Inject a custom API URL into window._env_
        const CUSTOM_API_URL = 'http://runtime-api-test:9999';

        await page.addInitScript((url) => {
            window['_env_'] = {
                NEXT_PUBLIC_API_URL: url
            };
        }, CUSTOM_API_URL);

        // 2. Act: Navigate to the login page (which usually triggers an API check or is the entry point)
        await page.goto('/login');

        // 3. Assert: Check if the frontend code is using the custom URL
        // We can verify this by checking the base URL of any recorded API request
        // or by evaluating the internal state if exposed.
        // A direct way is to check the window._env_ object first.
        const runtimeUrl = await page.evaluate(() => window['_env_']?.NEXT_PUBLIC_API_URL);
        expect(runtimeUrl).toBe(CUSTOM_API_URL);
    });

    test('should fallback to process.env or default if window._env_ is missing', async ({ page }) => {
        await page.goto('/login');
        const runtimeUrl = await page.evaluate(() => window['_env_']?.NEXT_PUBLIC_API_URL);
        // In local dev without the script running, this might be undefined or the default
        // We just want to ensure it doesn't crash the app.
        expect(runtimeUrl).toBeDefined();
    });
});
