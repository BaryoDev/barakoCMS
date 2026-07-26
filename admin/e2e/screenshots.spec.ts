import { test, expect } from '@playwright/test';
import { authed, stubShell } from './helpers';

/**
 * Release screenshots. Not a behaviour test — it drives the real UI to real states
 * and saves a picture of each, so an announcement can show what shipped instead of
 * only describing it (see AI_DEVELOPMENT_LIFECYCLE.md, "Announce"). It still asserts
 * the screen rendered, so a broken capture fails loudly rather than shooting a blank.
 *
 * Images land in test-results/screenshots/ (gitignored). Run locally with:
 *   npx playwright test screenshots.spec.ts --project=chromium
 */

const SCHEMA = {
    id: 'ct-1',
    name: 'memberprofile_ft',
    displayName: 'Member Profile',
    description: 'Field-type showcase',
    fields: [
        { name: 'FullName', displayName: 'Full Name', type: 'string', isRequired: true },
        { name: 'Email', displayName: 'Email', type: 'email', isRequired: true },
        { name: 'Website', displayName: 'Website', type: 'url', isRequired: false },
        { name: 'Handle', displayName: 'Handle', type: 'slug', isRequired: false },
        { name: 'Dues', displayName: 'Monthly Dues', type: 'money', isRequired: false },
        { name: 'JoinDate', displayName: 'Join Date', type: 'date', isRequired: false },
        { name: 'JoinTime', displayName: 'Join Time', type: 'time', isRequired: false },
        { name: 'Bio', displayName: 'Bio', type: 'richtext', isRequired: false },
        { name: 'Prefs', displayName: 'Preferences', type: 'json', isRequired: false },
    ],
};

test('api keys page', async ({ page }, testInfo) => {
    await authed(page);
    await stubShell(page);
    await page.route('**/api/api-keys**', (r) =>
        r.fulfill({
            json: [
                {
                    id: 'k1', name: 'CI deploy', prefix: 'bcms_ab12cd34', scopes: ['content:read', 'content:write'],
                    tenantSlug: 'default', expiresAt: null, lastUsedAt: new Date().toISOString(), revoked: false,
                    createdAt: new Date().toISOString(),
                },
                {
                    id: 'k2', name: 'Analytics export', prefix: 'bcms_99ff00aa', scopes: ['content:read'],
                    tenantSlug: 'default', expiresAt: null, lastUsedAt: null, revoked: false,
                    createdAt: new Date().toISOString(),
                },
            ],
        })
    );

    await page.goto('/api-keys');
    await expect(page.getByRole('heading', { name: 'API keys' })).toBeVisible({ timeout: 15000 });
    await expect(page.getByText('CI deploy')).toBeVisible();
    await page.screenshot({ path: `${testInfo.project.outputDir}/screenshots/api-keys.png`, fullPage: true });
});

test('entry form with the new field types', async ({ page }, testInfo) => {
    await authed(page);
    await stubShell(page);
    await page.route('**/api/schemas**', (r) => r.fulfill({ json: [SCHEMA] }));

    await page.goto('/content/new?type=memberprofile_ft');
    await expect(page.locator('#Email')).toBeVisible({ timeout: 15000 });
    // Fill a couple so the shot shows real, typed values.
    await page.locator('#FullName').fill('Arnel Robles');
    await page.locator('#Email').fill('arnel@baryo.dev');
    await page.locator('#Website').fill('https://baryo.dev');
    await page.locator('#Dues').fill('250');
    await page.locator('#Handle').fill('arnel-robles');

    await page.screenshot({
        path: `${testInfo.project.outputDir}/screenshots/field-types-entry-form.png`,
        fullPage: true,
    });
});
