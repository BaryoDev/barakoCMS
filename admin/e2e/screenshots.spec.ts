import { test, expect } from '@playwright/test';
import { authed, stubShell, pageOf, stubContentTypes } from './helpers';

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
            json: pageOf([
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
            ]),
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
    await stubContentTypes(page, [SCHEMA]);

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

test('sign in', async ({ page }, testInfo) => {
    // The GitHub button only renders when the deployment reports the provider, so a shot of the
    // default deployment would not show one. This stubs the answer an ExternalAuth install gives.
    await page.route('**/api/auth/providers', (r) =>
        r.fulfill({ json: { facebook: false, google: false, linkedin: false, github: true } })
    );

    await page.goto('/login');
    await expect(page.getByRole('heading', { name: 'Sign in to barakoBrew' })).toBeVisible({
        timeout: 15000,
    });
    await expect(page.getByRole('button', { name: 'Continue with GitHub' })).toBeVisible();
    await page.getByLabel('Username').fill('demo_admin');
    await page.getByLabel('Password', { exact: true }).fill('passwordpassword');

    await page.screenshot({ path: `${testInfo.project.outputDir}/screenshots/sign-in.png` });
});

test('entries list', async ({ page }, testInfo) => {
    await authed(page);
    await stubShell(page);
    await stubContentTypes(page, [
        { id: 'ct-a', name: 'article', displayName: 'Article', fields: [], isPubliclyDeliverable: true },
        { id: 'ct-p', name: 'page', displayName: 'Page', fields: [], isPubliclyDeliverable: true },
        { id: 'ct-n', name: 'newsletter', displayName: 'Newsletter', fields: [], isPubliclyDeliverable: true },
        // Not publicly deliverable, so its rows carry the lock and the Private pill.
        { id: 'ct-m', name: 'member', displayName: 'Member', fields: [], isPubliclyDeliverable: false },
    ]);

    const hoursAgo = (h: number) => new Date(Date.now() - h * 3600_000).toISOString();
    const rows = [
        { id: 'e1', contentType: 'article', data: { Title: 'Spring roast notes' }, status: 'Draft', hours: 2 },
        { id: 'e2', contentType: 'newsletter', data: { Title: 'Barako Weekly, issue 12' }, status: 'Draft', hours: 24 },
        { id: 'e3', contentType: 'page', data: { Title: 'Founding members page' }, status: 'Published', hours: 48 },
        { id: 'e4', contentType: 'member', data: { Name: 'Member, A. Reyes' }, status: 'Published', hours: 96 },
        { id: 'e5', contentType: 'article', data: { Title: 'Roast curve reference' }, status: 'Published', hours: 120 },
        { id: 'e6', contentType: 'page', data: { Title: 'Guild bylaws 2026' }, status: 'Archived', hours: 168 },
    ].map((r) => ({
        id: r.id,
        contentType: r.contentType,
        data: r.data,
        status: r.status,
        sensitivity: 'Public',
        createdAt: hoursAgo(r.hours),
        updatedAt: hoursAgo(r.hours),
    }));

    await page.route('**/api/contents**', (r) =>
        r.fulfill({
            json: {
                items: rows,
                page: 1,
                pageSize: 20,
                totalItems: 148,
                totalPages: 8,
                hasNextPage: true,
                hasPreviousPage: false,
            },
        })
    );

    await page.goto('/content');
    await expect(page.getByRole('heading', { name: 'Entries', exact: true })).toBeVisible({
        timeout: 15000,
    });
    await expect(page.getByText('Spring roast notes')).toBeVisible();
    await expect(page.getByText('Private').first()).toBeVisible();

    await page.screenshot({
        path: `${testInfo.project.outputDir}/screenshots/entries.png`,
        fullPage: true,
    });
});
