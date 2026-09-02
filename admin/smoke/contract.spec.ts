import { test, expect } from '@playwright/test';

/**
 * The admin, unmocked, against a real API and a real database.
 *
 * Every assertion here is one the mocked pack cannot make. The rule for deciding what belongs: if a
 * `page.route` fixture could satisfy it, it belongs in `e2e/` instead. What is left is the set of
 * beliefs the admin holds about the server, which is exactly where the bugs have been.
 *
 * The failure this exists for shipped and survived: the History panel read `versions` from a
 * response that has returned `items` since the envelope change. It rendered an empty list rather
 * than erroring, so it looked like an entry with no history. Every mocked spec passed, because the
 * mock returned `versions` too, and it was written by the same person as the code.
 *
 * Nothing here is mocked. There is no `page.route` in this file and there should never be one.
 */

const USERNAME = process.env.SMOKE_ADMIN_USERNAME || 'admin';
const PASSWORD = process.env.SMOKE_ADMIN_PASSWORD || '';

/**
 * One browser context for the whole file, and one sign-in.
 *
 * Not a style preference. `/api/auth/*` is rate limited to five requests per fifteen minutes per
 * IP, and login and refresh share that budget, so a page load costs one before anyone types
 * anything. A context per test spent it after two tests and the rest got 429 from the server, which
 * surfaced as a login page that would not submit.
 *
 * That is correct server behaviour and it is the pack finding a real constraint on itself. The
 * admin handles the 429 properly, with "Too many requests", which is worth recording because the
 * first read of this failure was that it did not.
 */
test.describe.configure({ mode: 'serial' });

let page: import('@playwright/test').Page;

test.beforeAll(async ({ browser }) => {
    // A blank password would make every login below fail for a reason that has nothing to do with
    // the contract, and the failures would read as contract breakage.
    expect(PASSWORD, 'SMOKE_ADMIN_PASSWORD must be set to the seeded administrator password').not.toBe('');

    const context = await browser.newContext();
    page = await context.newPage();
    await signIn(page);
});

test.afterAll(async () => {
    await page?.context().close();
});

/**
 * Navigate the way a person does, by clicking, not with `page.goto`.
 *
 * The access token lives in memory, deliberately, so a full page load drops it and the app does a
 * bootstrap refresh to get a new one. `/api/auth/*` allows five requests per fifteen minutes per IP
 * and refresh shares that budget with login, so a `goto` per test spent it partway through the file.
 * The app then logged out and the tests that followed described a login page while reporting that
 * the entries table was empty.
 *
 * Client-side navigation keeps the token, costs no auth request, and is what a real session does.
 */
async function goToEntries() {
    if (!page.url().endsWith('/content')) {
        // Anchored rather than exact. The sidebar rail puts a live count inside the link, so
        // against a real API the accessible name is "Entries 148" and an exact match finds nothing.
        // The count belongs in the name: a screen reader should hear it the way it hears an unread
        // count, which is why it is not aria-hidden. The mocked suites have no totals, so they
        // render no count and would never have caught this.
        await page.getByRole('link', { name: /^Entries\b/ }).first().click();
        await expect(page).toHaveURL(/\/content$/, { timeout: 20_000 });
    }
}

async function signIn(page: import('@playwright/test').Page) {
    await page.goto('/login');
    // By id, not by label. getByLabel(/password/i) also matches the Show password toggle, and a
    // strict-mode violation there would read as a contract failure rather than a locator one.
    await page.locator("#username").fill(USERNAME);
    await page.locator("#password").fill(PASSWORD);
    // Enter, not a click on the button. The button animates on state change and Playwright's
    // stability wait can outlast the test waiting for it to stop moving, which reads as "login is
    // broken" rather than "the button is mid-transition".
    await page.locator("#password").press('Enter');
    await expect(page).toHaveURL(/\/(dashboard|content)?$/, { timeout: 20_000 });
}

/**
 * Signing in end to end.
 *
 * Covers more than it looks: the login response shape, the token the client extracts from it, the
 * refresh cookie the server sets, and the bootstrap refresh the admin does on the next page load.
 * The mocked pack stubs all four.
 */
test('an administrator can sign in against the real API', async () => {
    // beforeAll already signed in, so reaching here at all means login worked end to end. The
    // assertion is that the authenticated shell rendered, which is the part a 200 does not prove.
    await expect(page.getByRole('navigation')).toBeVisible();
});

/**
 * A wrong password renders as a sentence.
 *
 * The server returns ProblemDetails whose entries carry `name` and `reason`. The admin read
 * `message`, which does not exist, so every validation failure rendered as the literal string
 * "[object Object]", including this one. A mock returning `message` would keep that green forever.
 *
 * Its own context, because it needs an unauthenticated page, and declared first so the shared
 * sign-in below is not spent before it runs. Two contexts is four auth requests against a budget of
 * five, which is why there are only two.
 */
test('a rejected login shows a message, not [object Object]', async ({ browser }) => {
    const context = await browser.newContext();
    const fresh = await context.newPage();

    await fresh.goto('/login');
    await fresh.locator("#username").fill(USERNAME);
    await fresh.locator("#password").fill('definitely-not-the-password');
    await fresh.locator("#password").press('Enter');

    const body = fresh.locator('body');
    await expect(body).toContainText(/invalid|incorrect|credential/i, { timeout: 20_000 });
    await expect(body).not.toContainText('[object Object]');
    await expect(body).not.toContainText('undefined');

    await context.close();
});

/**
 * The content list renders from the real envelope.
 *
 * The admin assumed a shape for collections and the assumption was wrong for at least one endpoint.
 * Reading a real page proves the envelope the server sends is the one the client unwraps, without
 * anyone having to keep a fixture in step.
 */
test('the content list renders whatever the server actually returns', async () => {
    await goToEntries();

    // Either rows or the empty state. What must not happen is the error state, which is what an
    // unreadable envelope produces, and the two are deliberately distinguishable in this admin.
    await expect(page.locator('body')).not.toContainText(/could not load|failed to load/i, {
        timeout: 20_000,
    });
    await expect(page.locator('body')).not.toContainText('[object Object]');
});

/**
 * A status reaches the browser as a name.
 *
 * `ContentStatus` was numeric on both sides, transcribed. `Draft` was `0`, which is falsy, so any
 * truthiness check written against it meant the opposite after the switch to strings. This asserts
 * the wire value, not the rendered label, because the label would look right either way.
 */
test('a content status crosses the wire as a name, not a number', async ({ request }) => {
    // The token comes from the harness, which got it from the real API. Not read out of the
    // browser, because the admin keeps its access token in memory rather than localStorage: that is
    // a deliberate choice recorded in api.ts, and reading it back would be testing a weakness.
    const token = process.env.SMOKE_TOKEN;
    const api = process.env.SMOKE_API_URL || 'http://127.0.0.1:5099';
    expect(token, 'SMOKE_TOKEN must be set by scripts/smoke-check.sh').toBeTruthy();

    const response = await request.get(`${api}/api/contents?page=1&pageSize=1`, {
        headers: { Authorization: `Bearer ${token}` },
    });
    expect(response.ok(), `GET /api/contents returned ${response.status()}`).toBeTruthy();

    const body = await response.json();
    expect(body, 'the collection envelope is items plus totals').toHaveProperty('items');

    // Asserted, not guarded. scripts/smoke-check.sh seeds an entry and refuses to continue if the
    // API does not report it, so an empty array here means the list endpoint broke rather than that
    // there is nothing to check. An `if (length > 0)` around the two assertions below would make
    // this test pass in exactly the case it exists to catch.
    expect(Array.isArray(body.items), 'items must be an array').toBe(true);
    expect(body.items.length, 'the seeded entry must be in the list').toBeGreaterThan(0);

    expect(typeof body.items[0].status).toBe('string');
    expect(['Draft', 'Published', 'Archived']).toContain(body.items[0].status);
});

/**
 * The History panel shows the entry's history.
 *
 * This is the worked example. It read `versions` from a response that returns `items`, so it
 * rendered an empty list, which is indistinguishable from an entry that has no history. Creating
 * the entry here means there is history to show, so an empty panel is unambiguous.
 */
test('the history panel shows history for an entry that has some', async () => {
    await goToEntries();

    // Asserted, not skipped. scripts/smoke-check.sh seeds an entry and fails if the API does not
    // report it, so an empty table here means the admin cannot read the list rather than that there
    // is nothing to read. The first version of this skipped on an empty table, which quietly turned
    // the one test this whole pack exists for into a no-op: it reported 4 passed, 1 skipped, and
    // the skip was the worked example.
    const rows = page.getByRole('row');
    await expect
        .poll(async () => await rows.count(), { timeout: 20_000 })
        .toBeGreaterThan(1);

    await rows.nth(1).click();
    await page.getByRole('tab', { name: /history/i }).click();

    // Every entry has at least its own creation, so an empty history panel here means the client
    // and the server disagree about the shape rather than that nothing has happened.
    await expect(page.locator('body')).not.toContainText(/no history|nothing here yet/i, {
        timeout: 20_000,
    });
    await expect(page.locator('body')).not.toContainText('[object Object]');
});
