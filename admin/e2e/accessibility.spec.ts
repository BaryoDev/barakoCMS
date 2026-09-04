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

/** A type with a Sensitive field, so the builder has something it must leave out of the form. */
const SUBSCRIBER = {
    id: 's2',
    name: 'Subscriber',
    displayName: 'Subscriber',
    fields: [
        { name: 'Email', displayName: 'Email', type: 'email', isRequired: true },
        { name: 'Status', displayName: 'Status', type: 'string', isRequired: false },
        { name: 'Salary', displayName: 'Salary', type: 'money', isRequired: false, sensitivity: 'Sensitive' },
    ],
};

const SAVED_QUERY = {
    id: 'q1',
    name: 'Active subscribers',
    slug: 'active-subscribers',
    contentType: 'Subscriber',
    filters: [{ field: 'Status', op: 'eq', value: 'Active' }],
    sortField: 'Email',
    descending: false,
    limit: 100,
    fields: ['Email', 'Status'],
    createdAt: new Date(Date.now() - 86400_000).toISOString(),
    updatedAt: new Date(Date.now() - 3600_000).toISOString(),
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

    test('the queries screen, with the builder open and a preview rendered', async ({ page }) => {
        await authed(page);
        await stubShell(page);
        await stubContentTypes(page, [SUBSCRIBER]);
        await page.route(/\/api\/queries(\?|$)/, (r) => r.fulfill({ json: pageOf([SAVED_QUERY]) }));
        await page.route('**/api/queries/active-subscribers', (r) => r.fulfill({ json: SAVED_QUERY }));
        await page.route('**/api/queries/active-subscribers/preview', (r) =>
            r.fulfill({
                json: {
                    ok: true,
                    count: 2,
                    rows: [
                        { Email: 'ana@example.com', Status: 'Active' },
                        { Email: 'ben@example.com', Status: 'Active' },
                    ],
                },
            })
        );

        await page.goto('/queries');
        await expect(page.getByRole('heading', { name: 'Queries' })).toBeVisible({ timeout: 15000 });

        // The builder is where the controls are: three kinds of select, a checkbox group, a switch
        // and a radio inside a table cell. Scanning the list alone would miss all of them.
        await page.getByLabel('Open Active subscribers').check();
        await expect(page.getByLabel('Content type')).toBeVisible();

        // Pinned by name rather than left to the scan. axe accepts a placeholder as an accessible
        // name, so a filter row whose controls lost their aria-labels would still pass it, named
        // after the placeholder text that vanishes the moment somebody types.
        await expect(page.getByLabel('Field for filter 1')).toBeVisible();
        await expect(page.getByLabel('Operator for filter 1')).toBeVisible();
        await expect(page.getByLabel('Value for filter 1')).toBeVisible();
        await expect(page.getByLabel('Remove filter 1')).toBeVisible();

        // The other two controls that carry a placeholder, and the same trap for the same reason.
        // Both survived a mutation that cut their label link, because axe then named them after the
        // placeholder. Their labels are what a screen reader has to read once a value is typed.
        await expect(page.getByLabel('Name')).toBeVisible();
        await expect(page.getByLabel('Slug')).toBeVisible();

        // The screen's headline claim, and until now nothing checked it. SUBSCRIBER carries a
        // Sensitive Salary field, and the builder must not offer it anywhere: not as a projection
        // checkbox, and not in the filter-field or sort-by selects, since filtering or sorting on a
        // field the rows cannot show reads it without printing it. projectableFields is tested on
        // its own; this is the line that connects it to the form.
        await expect(page.getByRole('checkbox', { name: /Salary/ })).toHaveCount(0);
        await expect(page.locator('option', { hasText: 'Salary' })).toHaveCount(0);
        // Paired with the negatives so they cannot pass on a form that rendered no fields at all.
        await expect(page.getByRole('checkbox', { name: /Email/ })).toHaveCount(1);
        await expect(page.locator('option', { hasText: 'Email' })).toHaveCount(2);

        await page.getByRole('button', { name: 'Preview' }).click();
        await expect(page.getByRole('cell', { name: 'ana@example.com' })).toBeVisible();

        // Settled first, and this is a real trap rather than a sprinkle of patience. The Run again
        // button comes back from disabled when the rows land, and Button transitions opacity, so for
        // about 150ms its near-black text is drawn at half opacity. axe reads the composited colour
        // and measures 4.09:1 against the card, which is a serious contrast failure the settled page
        // does not have. Without this the case fails perhaps one run in three, and a gate that fails
        // at random gets switched off.
        await page.waitForFunction(() =>
            document.getAnimations().every((a) => a.playState === 'finished')
        );

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
