import { test, expect } from '@playwright/test';
import { authed, stubShell, pageOf, stubContentTypes } from './helpers';

/**
 * Scheduled publishing, from the admin.
 *
 * The server has had `PUT /api/contents/{id}/schedule` and a background sweeper for a while, and
 * the README advertises arming any item. Until now nothing in the admin called it, so the feature
 * was real and unreachable, and the README was a claim anyone could check and find false.
 *
 * These are route-mocked like the rest of the pack, so they prove the panel drives the right
 * request rather than proving the server honours it. `ScheduledContentTests` covers the other half.
 */

const CONTENT_ID = '11111111-1111-1111-1111-111111111111';

function contentDetail(overrides: Record<string, unknown> = {}) {
    return {
        id: CONTENT_ID,
        contentType: 'article',
        data: { Title: 'A post' },
        createdAt: '2026-01-01T00:00:00Z',
        updatedAt: '2026-01-01T00:00:00Z',
        status: 'Draft',
        sensitivity: 'Public',
        version: 1,
        scheduledPublishAt: null,
        scheduledUnpublishAt: null,
        ...overrides,
    };
}

async function openSchedule(page: import('@playwright/test').Page, detail: Record<string, unknown>) {
    await authed(page);
    await stubShell(page);
    await stubContentTypes(page, [
        { name: 'article', displayName: 'Article', fields: [{ name: 'Title', type: 'string' }] },
    ]);
    await page.route('**/api/contents**', (r) => r.fulfill({ json: pageOf([detail]) }));
    await page.route(`**/api/contents/${CONTENT_ID}`, (r) => r.fulfill({ json: detail }));
    await page.route(`**/api/contents/${CONTENT_ID}/history**`, (r) => r.fulfill({ json: pageOf([]) }));

    await page.goto(`/content/${CONTENT_ID}`);
    await page.getByRole('tab', { name: 'Schedule' }).click();
}

test('an entry with nothing armed says so', async ({ page }) => {
    await openSchedule(page, contentDetail());

    await expect(page.getByTestId('schedule-armed')).toContainText('Nothing is scheduled');
});

/**
 * The readback. Arming a time and having no way to see it means the only way to know it took is to
 * wait and find out whether it happened.
 */
test('an armed entry shows when it goes out', async ({ page }) => {
    await openSchedule(page, contentDetail({ scheduledPublishAt: '2027-03-04T05:06:07Z' }));

    await expect(page.getByTestId('schedule-armed')).toContainText('Publishing');
    await expect(page.getByTestId('schedule-armed')).not.toContainText('Nothing is scheduled');
});

/**
 * The time entered is a local wall clock and the API takes UTC, so the request body has to be the
 * converted instant. Reading the input as if it were already UTC is the easy mistake, and it is
 * wrong by the reader's offset without ever looking wrong.
 */
test('a time entered locally is sent as UTC', async ({ page }) => {
    // Typed as the union rather than inferred: assigned only inside the route closure, so TS
    // narrows it to null at the assertions below and the casts stop compiling.
    let body: Record<string, unknown> | undefined;

    await openSchedule(page, contentDetail());
    await page.route(`**/api/contents/${CONTENT_ID}/schedule`, (route) => {
        body = route.request().postDataJSON() as Record<string, unknown>;
        return route.fulfill({ json: { id: CONTENT_ID } });
    });

    await page.getByLabel('Publish at').fill('2027-03-04T05:06');
    await page.getByRole('button', { name: 'Save schedule' }).click();

    await expect.poll(() => body).toBeDefined();
    const sent = String(body!.scheduledPublishAt);
    expect(sent).toMatch(/Z$|\+00:00$/);
    expect(new Date(sent).toISOString()).toBe(new Date('2027-03-04T05:06').toISOString());
});

/**
 * The server refuses an inverted window too. Saying so here means the person is told before the
 * round trip, and the server stays the thing that actually enforces it.
 */
test('archiving before publishing is refused without a round trip', async ({ page }) => {
    let called = false;

    await openSchedule(page, contentDetail());
    await page.route(`**/api/contents/${CONTENT_ID}/schedule`, (route) => {
        called = true;
        return route.fulfill({ json: { id: CONTENT_ID } });
    });

    await page.getByLabel('Publish at').fill('2027-03-04T05:06');
    await page.getByLabel('Archive at').fill('2027-03-01T05:06');

    await expect(page.getByTestId('schedule-inverted')).toContainText('after publish time');
    await expect(page.getByRole('button', { name: 'Save schedule' })).toBeDisabled();
    expect(called).toBe(false);
});

test('clearing sends nulls rather than omitting the fields', async ({ page }) => {
    // Typed as the union rather than inferred: assigned only inside the route closure, so TS
    // narrows it to null at the assertions below and the casts stop compiling.
    let body: Record<string, unknown> | undefined;

    await openSchedule(page, contentDetail({ scheduledPublishAt: '2027-03-04T05:06:07Z' }));
    await page.route(`**/api/contents/${CONTENT_ID}/schedule`, (route) => {
        body = route.request().postDataJSON() as Record<string, unknown>;
        return route.fulfill({ json: { id: CONTENT_ID } });
    });

    await page.getByRole('button', { name: 'Clear schedule' }).click();

    await expect.poll(() => body).toBeDefined();
    // Null is how the API clears an armed time. Omitting the key would leave it armed, so the
    // button would report success and change nothing.
    expect(body!.scheduledPublishAt).toBeNull();
    expect(body!.scheduledUnpublishAt).toBeNull();
});
