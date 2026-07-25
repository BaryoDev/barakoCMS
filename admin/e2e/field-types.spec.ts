import { test, expect } from '@playwright/test';
import { authed, stubShell } from './helpers';

/**
 * F.1/F.2 — field types. The browser-level mirror of the live API check: a content
 * type using the new validation-shaped types renders the right control for each,
 * a valid entry saves, and the server's per-type validation errors surface to the
 * editor. Route-mocked (no backend), but it drives the real DynamicForm and the
 * real create flow — the layer where "which input for which type" actually lives.
 */

// A content type exercising the new types alongside the originals.
const SCHEMA = {
    id: 'ct-1',
    name: 'memberprofile_ft',
    displayName: 'Member Profile FT',
    description: 'Field-type coverage',
    fields: [
        { name: 'FullName', displayName: 'Full Name', type: 'string', isRequired: true },
        { name: 'Email', displayName: 'Email', type: 'email', isRequired: true },
        { name: 'Website', displayName: 'Website', type: 'url', isRequired: false },
        { name: 'Handle', displayName: 'Handle', type: 'slug', isRequired: false },
        { name: 'MemberId', displayName: 'Member Id', type: 'uuid', isRequired: false },
        { name: 'Dues', displayName: 'Dues', type: 'money', isRequired: false },
        { name: 'JoinDate', displayName: 'Join Date', type: 'date', isRequired: false },
        { name: 'JoinTime', displayName: 'Join Time', type: 'time', isRequired: false },
        { name: 'Meeting', displayName: 'Meeting', type: 'datetime', isRequired: false },
        { name: 'Bio', displayName: 'Bio', type: 'richtext', isRequired: false },
        { name: 'Prefs', displayName: 'Prefs', type: 'json', isRequired: false },
    ],
};

async function gotoNewEntry(page: import('@playwright/test').Page) {
    await authed(page);
    await stubShell(page);
    await page.route('**/api/schemas**', (r) => r.fulfill({ json: [SCHEMA] }));
    await page.goto('/content/new?type=memberprofile_ft');
    // The form only appears once the schema resolves.
    await expect(page.locator('#Email')).toBeVisible({ timeout: 15000 });
}

test.describe('F.1 — the entry form renders the right control per field type', () => {
    test('typed inputs get the matching native control', async ({ page }) => {
        await gotoNewEntry(page);

        // Validated single-line types → typed <input>s.
        await expect(page.locator('#Email')).toHaveAttribute('type', 'email');
        await expect(page.locator('#Website')).toHaveAttribute('type', 'url');
        await expect(page.locator('#Handle')).toHaveAttribute('type', 'text');
        await expect(page.locator('#MemberId')).toHaveAttribute('type', 'text');

        // Money is a number spinner; the temporal trio get native pickers.
        await expect(page.locator('#Dues')).toHaveAttribute('type', 'number');
        await expect(page.locator('#JoinDate')).toHaveAttribute('type', 'date');
        await expect(page.locator('#JoinTime')).toHaveAttribute('type', 'time');
        await expect(page.locator('#Meeting')).toHaveAttribute('type', 'datetime-local');

        // Rich/markdown and structured JSON are textareas.
        await expect(page.locator('textarea#Bio')).toBeVisible();
        await expect(page.locator('textarea#Prefs')).toBeVisible();

        // Email input carries a helpful placeholder (the editor-hint made it through).
        await expect(page.locator('#Email')).toHaveAttribute('placeholder', /@/);
    });
});

test.describe('F.1 — saving entries', () => {
    test('a valid entry saves and navigates to its detail page', async ({ page }) => {
        await gotoNewEntry(page);

        await page.route('**/api/contents**', (route) => {
            if (route.request().method() === 'POST') {
                return route.fulfill({
                    json: { id: 'new-entry-1', version: 1, message: 'Content created successfully' },
                });
            }
            // Detail GET after the redirect.
            return route.fulfill({
                json: {
                    id: 'new-entry-1',
                    contentType: 'memberprofile_ft',
                    data: { FullName: 'Arnel R', Email: 'arnel@baryo.dev' },
                    status: 1,
                    version: 1,
                },
            });
        });

        await page.locator('#FullName').fill('Arnel R');
        await page.locator('#Email').fill('arnel@baryo.dev');
        await page.locator('#Website').fill('https://baryo.dev');
        await page.locator('#Dues').fill('250.50');
        await page.getByRole('button', { name: 'Publish' }).click();

        await page.waitForURL('**/content/new-entry-1', { timeout: 15000 });
    });

    // Edge case (server): a value whose format doesn't match the field type is
    // rejected by the API, and the editor must show that exact message, not swallow
    // it. An <input type=email> accepts free text, so a bad email does reach the API
    // (unlike money, whose number input blocks non-numeric entry client-side — that
    // path is covered by the backend FieldTypeRegistry tests instead).
    test('a malformed email surfaces the server validation error', async ({ page }) => {
        await gotoNewEntry(page);
        const message = "Validation Failed: Field 'Email' expects type 'email' but received 'string'";
        await page.route('**/api/contents**', (route) =>
            route.fulfill({ status: 400, json: { message } })
        );

        await page.locator('#FullName').fill('Test User');
        await page.locator('#Email').fill('not-an-email');
        await page.getByRole('button', { name: 'Publish' }).click();

        await expect(page.getByText(message)).toBeVisible({ timeout: 10000 });
    });

    // Edge case (client): the JSON editor warns on unparseable input and keeps the
    // last valid value rather than writing garbage.
    test('invalid JSON in a json field shows an inline parse warning', async ({ page }) => {
        await gotoNewEntry(page);

        await page.locator('textarea#Prefs').fill('{ not valid json');

        await expect(page.getByText(/Not valid JSON yet/i)).toBeVisible({ timeout: 10000 });
    });
});
