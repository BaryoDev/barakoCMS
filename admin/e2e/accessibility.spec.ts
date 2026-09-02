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

function row(id: string, contentType: string, status: string, title: string) {
    return {
        id,
        contentType,
        data: { Title: title },
        status,
        sensitivity: 'Public',
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
                ]),
            })
        );

        await page.goto('/content');
        await expect(page.getByRole('heading', { name: 'Entries', exact: true })).toBeVisible({ timeout: 15000 });
        // The badges are the point of this case, so fail loudly if the table did not render them
        // rather than scanning an empty page and calling it a pass.
        await expect(page.getByText('Private', { exact: true })).toBeVisible();
        await expect(page.getByText('Draft', { exact: true })).toBeVisible();
        await expect(page.getByText('Posted', { exact: true })).toBeVisible();
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

    test('the content types list', async ({ page }) => {
        await authed(page);
        await stubShell(page);
        await stubContentTypes(page, [SCHEMA]);

        await page.goto('/schemas');
        await expect(page.getByRole('link', { name: /Article/ }).first()).toBeVisible({ timeout: 15000 });
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
