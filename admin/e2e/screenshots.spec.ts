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
