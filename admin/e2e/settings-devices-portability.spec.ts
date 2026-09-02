import { test, expect } from '@playwright/test';
import { authed, stubShell, pageOf } from './helpers';

const DEVICES = pageOf([
    {
        id: '11111111-1111-1111-1111-111111111111',
        description: 'Chrome on macOS',
        lastSeenIp: '203.0.113.24',
        lastUsedAt: new Date(Date.now() - 4 * 60_000).toISOString(),
        status: 'Trusted',
        current: true,
    },
    {
        id: '22222222-2222-2222-2222-222222222222',
        description: 'Safari on iPhone',
        lastSeenIp: '198.51.100.7',
        lastUsedAt: new Date(Date.now() - 3 * 3600_000).toISOString(),
        status: 'Trusted',
        current: false,
    },
    {
        id: '44444444-4444-4444-4444-444444444444',
        description: 'Edge on Windows',
        lastSeenIp: '203.0.113.140',
        lastUsedAt: new Date(Date.now() - 9 * 86400_000).toISOString(),
        status: 'Revoked',
        current: false,
    },
]);

/**
 * The devices screen, which is a person looking at their own account rather than an admin list.
 *
 * The revoke path is the whole reason this screen exists, so it is driven rather than described:
 * the confirmation has to say something different for the device you are sitting at, and the
 * request has to name the device that was clicked. Asserting the dialog alone would pass against a
 * button wired to the wrong row.
 */
test.describe('devices', () => {
    test('lists every device with its status, and flags the current one', async ({ page }) => {
        await authed(page);
        await stubShell(page);
        await page.route('**/api/devices**', (r) => r.fulfill({ json: DEVICES }));

        await page.goto('/settings/devices');
        await expect(page.getByRole('heading', { name: 'Devices', exact: true })).toBeVisible({ timeout: 20000 });

        const rows = page.getByRole('table');
        await expect(rows.getByText('Chrome on macOS')).toBeVisible();
        await expect(rows.getByText('Safari on iPhone')).toBeVisible();
        await expect(rows.getByText('This device', { exact: true })).toHaveCount(1);

        // A revoked device has nothing to revoke, so the control is absent rather than disabled.
        await expect(page.getByRole('button', { name: 'Revoke Edge on Windows' })).toHaveCount(0);
        await expect(page.getByRole('button', { name: 'Revoke Safari on iPhone' })).toBeVisible();
    });

    test('warns that revoking this device signs you out, and revokes the one that was clicked', async ({ page }) => {
        await authed(page);
        await stubShell(page);
        await page.route('**/api/devices**', (r) => r.fulfill({ json: DEVICES }));

        const revoked: string[] = [];
        await page.route('**/api/devices/*/revoke', (r) => {
            revoked.push(new URL(r.request().url()).pathname);
            return r.fulfill({ status: 200, json: {} });
        });

        await page.goto('/settings/devices');
        await page.getByRole('button', { name: 'Revoke Chrome on macOS' }).click();

        await expect(page.getByText('This is the device you are using now')).toBeVisible();

        await page.getByRole('button', { name: 'Revoke', exact: true }).click();
        await expect.poll(() => revoked.length).toBe(1);

        expect(revoked[0]).toContain('11111111-1111-1111-1111-111111111111');
    });

    test('says something different for a device that is not this one', async ({ page }) => {
        // Paired with the test above. If both said the same thing, the first would pass against a
        // dialog that always warns about signing out, which is the wording that matters least when
        // it is wrong and most when it is missing.
        await authed(page);
        await stubShell(page);
        await page.route('**/api/devices**', (r) => r.fulfill({ json: DEVICES }));

        await page.goto('/settings/devices');
        await page.getByRole('button', { name: 'Revoke Safari on iPhone' }).click();

        await expect(page.getByText('Safari on iPhone will lose access')).toBeVisible();
        await expect(page.getByText('This is the device you are using now')).toHaveCount(0);
    });
});

const BUNDLE = {
    contentTypes: [{ name: 'article' }, { name: 'member' }],
    contents: Array.from({ length: 5 }, () => ({ contentType: 'article', data: {}, status: 'Draft' })),
};

function bundleFile(body: unknown, name = 'bundle.json') {
    return {
        name,
        mimeType: 'application/json',
        buffer: Buffer.from(typeof body === 'string' ? body : JSON.stringify(body)),
    };
}

test.describe('export and import', () => {
    test('previews with a dry run and imports for real, and the two are different requests', async ({ page }) => {
        await authed(page);
        await stubShell(page);

        const dryRuns: boolean[] = [];
        await page.route('**/api/portability/import**', async (r) => {
            dryRuns.push(r.request().postDataJSON().dryRun);
            return r.fulfill({
                json: {
                    dryRun: r.request().postDataJSON().dryRun,
                    contentTypesCreated: 2,
                    contentTypesUpdated: 0,
                    contentsCreated: 5,
                    contentsWithoutContentType: 0,
                },
            });
        });

        await page.goto('/settings/portability');
        await expect(page.getByRole('heading', { name: 'Export and import' })).toBeVisible({ timeout: 20000 });

        await page.getByLabel('Choose a bundle file').setInputFiles(bundleFile(BUNDLE));
        await expect(page.getByText('2 content types and 5 entries in this file.')).toBeVisible();

        await page.getByRole('button', { name: 'Preview' }).click();
        await expect(page.getByText('This is what an import would do')).toBeVisible();

        await page.getByRole('button', { name: 'Import', exact: true }).click();
        await expect(page.getByText('Imported', { exact: true })).toBeVisible();

        // The point of the pair: Preview must not write and Import must. A screen that sent
        // dryRun true both times would look identical and quietly never import anything.
        expect(dryRuns).toEqual([true, false]);
    });

    test('refuses a file that is not a bundle without asking the server', async ({ page }) => {
        await authed(page);
        await stubShell(page);

        let requests = 0;
        await page.route('**/api/portability/import**', (r) => {
            requests++;
            return r.fulfill({ json: {} });
        });

        await page.goto('/settings/portability');
        await expect(page.getByRole('heading', { name: 'Export and import' })).toBeVisible({ timeout: 20000 });

        // Valid JSON, wrong shape. This is the case that matters: the server takes it, finds no
        // content types and no entries, and reports a successful import of nothing.
        await page.getByLabel('Choose a bundle file').setInputFiles(bundleFile({ hello: 'world' }));

        await expect(page.getByText('not a bundle this can import', { exact: false })).toBeVisible();
        await expect(page.getByRole('button', { name: 'Preview' })).toHaveCount(0);
        expect(requests).toBe(0);
    });
});
