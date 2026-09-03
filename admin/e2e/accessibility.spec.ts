import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import { authed, stubShell, stubContentTypes, EMPTY_PAGE, pageOf } from './helpers';

/**
 * Accessibility checks on the routes an editor actually lives in.
 *
 * WCAG 2.2 AA and EN 301 549 are procurement gates for public-sector and large-enterprise buyers,
 * so this is a gate rather than a preference. There was none before: no axe, no jsx-a11y beyond the
 * six rules Next enables by default, nothing.
 *
 * Two honest limits, stated so nobody reads a green run as more than it is:
 *
 * 1. These specs mock the API, so axe sees markup rendered from fixtures. That is fine for the
 *    rules it checks, which are about the rendered document, but it is not a test of real content.
 * 2. Automated tooling catches roughly half of WCAG. Keyboard order and focus management through
 *    the Radix dialogs and dropdowns need a person, and no assertion here substitutes for that.
 *
 * Failing on serious and critical only, deliberately. A gate that fails on every minor contrast
 * nit gets switched off within a week, and a gate everyone ignores is worse than none.
 */

const SCHEMA = {
    id: 's1',
    name: 'article',
    displayName: 'Article',
    fields: [{ name: 'Title', displayName: 'Title', type: 'Text', isRequired: true }],
};

function device(id: string, description: string, status: string, current: boolean) {
    return {
        id,
        description,
        status,
        current,
        lastSeenIp: '203.0.113.24',
        lastUsedAt: new Date(Date.now() - 3600_000).toISOString(),
    };
}

/** A run with an action in every state, so the badge tones and the retry button are all rendered. */
function workflowRun() {
    return {
        id: 'r1',
        workflowDefinitionId: 'wf-1',
        workflowName: 'Announce a post',
        contentId: '11111111-1111-1111-1111-111111111111',
        contentType: 'article',
        triggerEvent: 'ContentPublished',
        status: 'PartiallyFailed',
        createdAt: new Date(Date.now() - 3600_000).toISOString(),
        completedAt: new Date().toISOString(),
        actions: [
            { ordinal: 1, actionType: 'Email', status: 'Succeeded', attempts: 1, responseStatus: 202, durationMs: 420, error: null, completedAt: new Date().toISOString(), nextAttemptAt: null },
            { ordinal: 2, actionType: 'Webhook', status: 'Failed', attempts: 3, responseStatus: 503, durationMs: 15_400, error: 'Service Unavailable from hooks.example.com', completedAt: null, nextAttemptAt: null },
            { ordinal: 3, actionType: 'Webhook', status: 'Unknown', attempts: 1, responseStatus: null, durationMs: null, error: 'The request timed out.', completedAt: null, nextAttemptAt: null },
            { ordinal: 4, actionType: 'Email', status: 'Running', attempts: 1, responseStatus: null, durationMs: null, error: null, completedAt: null, nextAttemptAt: null },
            { ordinal: 5, actionType: 'Email', status: 'Skipped', attempts: 0, responseStatus: null, durationMs: null, error: null, completedAt: null, nextAttemptAt: null },
        ],
    };
}

function row(id: string, contentType: string, status: string, title: string, version = 3) {
    return {
        id,
        contentType,
        data: { Title: title },
        status,
        sensitivity: 'Public',
        version,
        createdAt: new Date(Date.now() - 3600_000).toISOString(),
        updatedAt: new Date(Date.now() - 3600_000).toISOString(),
    };
}

/** Serious and critical only. Minor and moderate are reported in the failure message, not failed on. */
async function scan(page: import('@playwright/test').Page) {
    const results = await new AxeBuilder({ page })
        .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
        .analyze();

    const blocking = results.violations.filter(
        (v) => v.impact === 'serious' || v.impact === 'critical'
    );

    const describe = (v: (typeof results.violations)[number]) =>
        `${v.impact} ${v.id}: ${v.help} (${v.nodes.length} node${v.nodes.length === 1 ? '' : 's'})` +
        `\n      first: ${v.nodes[0]?.html?.slice(0, 160) ?? '?'}`;

    expect(
        blocking.map(describe).join('\n    '),
        `serious or critical accessibility violations.\n  Also present, not failed on: ` +
            (results.violations
                .filter((v) => v.impact !== 'serious' && v.impact !== 'critical')
                .map((v) => `${v.impact} ${v.id}`)
                .join(', ') || 'none')
    ).toBe('');
}

test.describe('accessibility', () => {
    test('the sign-in page', async ({ page }) => {
        await stubShell(page);
        await page.goto('/login');
        await expect(page.getByLabel('Username')).toBeVisible();
        await scan(page);
    });

    /**
     * Rows, not an empty page.
     *
     * This stubbed EMPTY_PAGE, so the table body never rendered and the gate had never once seen a
     * status badge. It was missing a real failure: the badge tones built their background from an
     * alpha wash and took their text colour from `--warning-foreground`, which is white, so a
     * warning badge was white on a 10% wash of white. Every status the table can show is here, plus
     * a row of a type that is not publicly deliverable, so the Private pill is scanned too.
     */
    test('the content list, with a row of every status', async ({ page }) => {
        await authed(page);
        await stubShell(page);
        await stubContentTypes(page, [
            SCHEMA,
            { id: 's2', name: 'member', displayName: 'Member', fields: [], isPubliclyDeliverable: false },
        ]);
        await page.route('**/api/contents**', (r) =>
            r.fulfill({
                json: pageOf([
                    // Titles deliberately share no word with a badge label, so an assertion on a
                    // pill cannot be satisfied by an entry title instead.
                    row('c1', 'article', 'Draft', 'Spring roast notes'),
                    row('c2', 'article', 'Published', 'Roast curve reference'),
                    row('c3', 'article', 'Archived', 'Old landing copy'),
                    // Private sits beside the status rather than replacing it, so both pills scan.
                    row('c4', 'member', 'Published', 'Member, A. Reyes'),
                    // A status this admin does not know. statusMeta renders the raw value in the
                    // muted tone rather than inventing one, and that path needs contrast too.
                    row('c5', 'article', 'Posted', 'Journal entry JE-2044'),
                    // Scheduled uses the accent tint, which is a pair no other badge on this page
                    // uses, so leaving it out would mean the one new colour is the one never scanned.
                    row('c6', 'article', 'Scheduled', 'Autumn blend announcement'),
                ]),
            })
        );

        await page.goto('/content');
        await expect(page.getByRole('heading', { name: 'Entries', exact: true })).toBeVisible({ timeout: 15000 });
        // The badges are the point of this case, so fail loudly if the table did not render them
        // rather than scanning an empty page and calling it a pass.
        //
        // Scoped to the table. The filter bar above it has buttons reading Draft, Published,
        // Scheduled and Archived, so an unscoped exact-text match now finds two elements and a
        // strict-mode violation reads as a broken selector rather than as what it is.
        const rows = page.getByRole('table');
        await expect(rows.getByText('Private', { exact: true })).toBeVisible();
        await expect(rows.getByText('Draft', { exact: true })).toBeVisible();
        await expect(rows.getByText('Scheduled', { exact: true })).toBeVisible();
        await expect(rows.getByText('Posted', { exact: true })).toBeVisible();

        // And the controls themselves, which this case now covers: an empty filter bar would let
        // the scan pass without ever looking at the search box or the segmented control.
        await expect(page.getByLabel('Search entries')).toBeVisible();
        await expect(page.getByRole('group', { name: 'Filter by status' })).toBeVisible();

        await scan(page);
    });

    test('the content list, empty', async ({ page }) => {
        await authed(page);
        await stubShell(page);
        await stubContentTypes(page, [SCHEMA]);
        await page.route('**/api/contents**', (r) => r.fulfill({ json: EMPTY_PAGE }));

        await page.goto('/content');
        await expect(page.getByRole('heading', { name: 'Entries', exact: true })).toBeVisible({ timeout: 15000 });
        await scan(page);
    });

    test('the devices list, with a row of every status', async ({ page }) => {
        await authed(page);
        await stubShell(page);
        await page.route('**/api/devices**', (r) =>
            r.fulfill({
                json: pageOf([
                    device('d1', 'Chrome on macOS', 'Trusted', true),
                    device('d2', 'Safari on iPhone', 'Trusted', false),
                    device('d3', 'Firefox on Windows', 'Pending', false),
                    device('d4', 'Edge on Windows', 'Revoked', false),
                ]),
            })
        );

        await page.goto('/settings/devices');
        await expect(page.getByRole('heading', { name: 'Devices', exact: true })).toBeVisible({ timeout: 15000 });

        // Every badge tone this screen can draw, so the scan sees the colours rather than an empty
        // table. The accent pill on the current device is the one no other row uses.
        const rows = page.getByRole('table');
        await expect(rows.getByText('This device', { exact: true })).toBeVisible();
        await expect(rows.getByText('Pending', { exact: true })).toBeVisible();
        await expect(rows.getByText('Revoked', { exact: true }).first()).toBeVisible();
        await scan(page);
    });

    test('export and import, with a bundle chosen and a report shown', async ({ page }) => {
        await authed(page);
        await stubShell(page);
        await page.route('**/api/portability/import**', (r) =>
            r.fulfill({
                json: {
                    dryRun: true,
                    contentTypesCreated: 2,
                    contentTypesUpdated: 1,
                    contentsCreated: 34,
                    contentsWithoutContentType: 3,
                },
            })
        );

        await page.goto('/settings/portability');
        await expect(page.getByRole('heading', { name: 'Export and import' })).toBeVisible({ timeout: 15000 });

        await page.getByLabel('Choose a bundle file').setInputFiles({
            name: 'bundle.json',
            mimeType: 'application/json',
            buffer: Buffer.from(JSON.stringify({ contentTypes: [{ name: 'article' }], contents: [] })),
        });
        await page.getByRole('button', { name: 'Preview' }).click();

        // The warning is the only thing on this page in the warning tone, and it is the reason the
        // report exists, so scanning without it would miss the contrast pair that matters.
        await expect(page.getByText('nothing knows which of their fields are public', { exact: false })).toBeVisible();
        await scan(page);
    });

    test('the content types list', async ({ page }) => {
        await authed(page);
        await stubShell(page);
        await stubContentTypes(page, [SCHEMA]);

        await page.goto('/schemas');
        await expect(page.getByRole('link', { name: /Article/ }).first()).toBeVisible({ timeout: 15000 });
        await scan(page);
    });

    test('the workflow runs screen, with a run open and every badge tone on screen', async ({ page }) => {
        await authed(page);
        await stubShell(page);

        const detail = workflowRun();
        await page.route(/\/api\/workflow-runs(\?|$)/, (r) =>
            r.fulfill({
                json: pageOf([
                    detail,
                    { ...detail, id: 'r2', workflowName: 'Tidy the index', status: 'Succeeded' },
                    { ...detail, id: 'r3', workflowName: 'Push to the CDN', status: 'Failed' },
                    { ...detail, id: 'r4', workflowName: 'Notify the desk', status: 'Running' },
                    { ...detail, id: 'r5', workflowName: 'Archive the draft', status: 'Pending' },
                ]),
            })
        );
        await page.route(/\/api\/workflow-runs\/[^/]+$/, (r) => r.fulfill({ json: detail }));

        await page.goto('/workflow-runs');
        await expect(page.getByRole('heading', { name: 'Workflow runs' })).toBeVisible({ timeout: 15000 });

        // Opened, so the scan sees the action list, the error block and the retry button rather than
        // an empty panel. Every badge tone this screen can draw is on the page at once.
        await page.getByRole('radio', { name: /Announce a post/ }).check();
        await expect(page.getByRole('button', { name: /Retry action 2/ })).toBeVisible();
        await scan(page);
    });

    test('the entry form, which is the page an editor spends the most time in', async ({ page }) => {
        await authed(page);
        await stubShell(page);
        await stubContentTypes(page, [SCHEMA]);
        await page.route('**/api/contents**', (r) => r.fulfill({ json: pageOf([]) }));

        await page.goto('/content/new?type=article');
        // The form only appears once the schema resolves.
        await expect(page.locator('#Title')).toBeVisible({ timeout: 15000 });
        await scan(page);
    });
});
