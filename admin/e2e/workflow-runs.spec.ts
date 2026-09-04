import { expect, test, type Page } from '@playwright/test';
import { authed, pageOf, stubShell } from './helpers';

/**
 * The workflow runs screen: does the list read at a glance, and is the retry button offered only
 * where retrying is safe.
 *
 * The gating is unit tested on `isRetryable` and on the attempt card, so these specs are about the
 * wiring the unit tests cannot see: that the button reaches the right ordinal on the right run, and
 * that pressing it makes the screen read the run again rather than trust the response body.
 */

interface Attempt {
  ordinal: number;
  actionType: string;
  status: string;
  attempts: number;
  nextAttemptAt?: string | null;
  responseStatus?: number | null;
  error?: string | null;
  completedAt?: string | null;
  durationMs?: number | null;
}

function attempt(ordinal: number, actionType: string, status: string, extra: Partial<Attempt> = {}): Attempt {
  return {
    ordinal,
    actionType,
    status,
    attempts: 1,
    nextAttemptAt: null,
    responseStatus: null,
    error: null,
    completedAt: null,
    durationMs: 820,
    ...extra,
  };
}

function run(id: string, workflowName: string, status: string, actions: Attempt[]) {
  return {
    id,
    workflowDefinitionId: 'wf-1',
    workflowName,
    contentId: '11111111-1111-1111-1111-111111111111',
    contentType: 'article',
    triggerEvent: 'ContentPublished',
    status,
    createdAt: new Date(Date.now() - 3600_000).toISOString(),
    completedAt: status === 'Pending' || status === 'Running' ? null : new Date().toISOString(),
    actions,
  };
}

const PARTLY_FAILED = run('run-partly', 'Announce a post', 'PartiallyFailed', [
  attempt(1, 'Email', 'Succeeded', { responseStatus: 202, completedAt: new Date().toISOString() }),
  attempt(2, 'Webhook', 'Failed', {
    attempts: 3,
    responseStatus: 503,
    error: 'Service Unavailable from hooks.example.com',
    durationMs: 15_400,
  }),
  attempt(3, 'Webhook', 'Unknown', { attempts: 1, error: 'The request timed out.' }),
  attempt(4, 'Email', 'Running', { attempts: 1, durationMs: null }),
  attempt(5, 'Email', 'Skipped', { attempts: 0, durationMs: null }),
]);

const RUNS = [
  PARTLY_FAILED,
  run('run-ok', 'Tidy the index', 'Succeeded', [attempt(1, 'Email', 'Succeeded')]),
  run('run-bad', 'Push to the CDN', 'Failed', [attempt(1, 'Webhook', 'Failed', { error: 'Bad gateway' })]),
  run('run-live', 'Notify the desk', 'Running', [attempt(1, 'Email', 'Running')]),
  run('run-queued', 'Archive the draft', 'Pending', [attempt(1, 'Email', 'Pending')]),
];

/** Stubs the three endpoints and reports how many times the detail was read. */
async function stubRuns(
  page: Page,
  options: { retry?: (route: import('@playwright/test').Route) => void | Promise<void> } = {}
) {
  const detailReads: string[] = [];
  const retries: string[] = [];

  await page.route(/\/api\/workflow-runs(\?|$)/, (route) => {
    const status = new URL(route.request().url()).searchParams.get('status');
    const items = status ? RUNS.filter((r) => r.status === status) : RUNS;
    return route.fulfill({ json: pageOf(items) });
  });

  await page.route(/\/api\/workflow-runs\/[^/]+$/, (route) => {
    const id = route.request().url().split('/').pop() as string;
    detailReads.push(id);
    const found = RUNS.find((r) => r.id === id);
    return found
      ? route.fulfill({ json: found })
      : route.fulfill({ status: 404, json: { message: 'Not found' } });
  });

  await page.route('**/api/workflow-runs/*/actions/*/retry', (route) => {
    retries.push(route.request().url());
    if (options.retry) return options.retry(route);
    return route.fulfill({ json: { ...PARTLY_FAILED, status: 'Running' } });
  });

  return { detailReads, retries };
}

/** The action list of the open run, named so the sidebar's own lists are not caught. */
function actions(page: Page) {
  return page.getByRole('list', { name: 'Actions' }).getByRole('listitem');
}

async function open(page: Page) {
  await authed(page);
  await stubShell(page);
  const stubs = await stubRuns(page);
  await page.goto('/workflow-runs');
  await expect(page.getByRole('heading', { name: 'Workflow runs' })).toBeVisible({ timeout: 15000 });
  return stubs;
}

test.describe('workflow runs', () => {
  test('lists every run with its status carried by a badge', async ({ page }) => {
    await open(page);

    const table = page.getByRole('table');
    await expect(table.getByText('Announce a post')).toBeVisible();
    await expect(table.getByText('PartiallyFailed', { exact: true })).toBeVisible();
    await expect(table.getByText('Succeeded', { exact: true })).toBeVisible();
    await expect(table.getByText('Failed', { exact: true })).toBeVisible();
    await expect(table.getByText('Running', { exact: true })).toBeVisible();
    await expect(table.getByText('Pending', { exact: true })).toBeVisible();
  });

  test('a status filter narrows the list to that status', async ({ page }) => {
    await open(page);

    await page.getByLabel('Filter runs by status').click();
    await page.getByRole('option', { name: 'Failed', exact: true }).click();

    const table = page.getByRole('table');
    await expect(table.getByText('Push to the CDN')).toBeVisible();
    await expect(table.getByText('Announce a post')).toBeHidden();
  });

  test('opening a run shows its actions in order, with the error on the one that failed', async ({ page }) => {
    await open(page);

    await page.getByRole('radio', { name: /Announce a post/ }).check();

    const detail = actions(page);
    await expect(detail).toHaveCount(5);
    await expect(detail.nth(1)).toContainText('Service Unavailable from hooks.example.com');
    await expect(detail.nth(1)).toContainText('15.4 s');
    await expect(detail.nth(1)).toContainText('503');
  });

  test('offers retry on the failed and unknown actions and on nothing else', async ({ page }) => {
    await open(page);

    await page.getByRole('radio', { name: /Announce a post/ }).check();
    await expect(actions(page)).toHaveCount(5);

    // Ordinals 2 (Failed) and 3 (Unknown). Ordinal 1 succeeded, so a retry would send it a second
    // time, which is the hazard the idempotency key exists for.
    await expect(page.getByRole('button', { name: /Retry action 2/ })).toBeVisible();
    await expect(page.getByRole('button', { name: /Retry action 3/ })).toBeVisible();
    await expect(page.getByRole('button', { name: /Retry/ })).toHaveCount(2);
  });

  test('retrying posts to that ordinal and then reads the run again', async ({ page }) => {
    const stubs = await open(page);

    await page.getByRole('radio', { name: /Announce a post/ }).check();
    await expect(actions(page)).toHaveCount(5);
    const readsBefore = stubs.detailReads.length;
    expect(readsBefore).toBeGreaterThan(0);

    await page.getByRole('button', { name: /Retry action 2/ }).click();

    await expect(page.getByText('Queued that action to run again.')).toBeVisible();
    expect(stubs.retries).toHaveLength(1);
    // The ordinal, not the array index. Ordinal 2 is the failed webhook; index 2 is the timeout.
    expect(stubs.retries[0]).toContain('/api/workflow-runs/run-partly/actions/2/retry');

    // The response body is discarded, so the only way the screen can be right is a fresh read.
    await expect.poll(() => stubs.detailReads.length).toBeGreaterThan(readsBefore);
  });

  test('a refused retry says so and leaves the screen alone', async ({ page }) => {
    await authed(page);
    await stubShell(page);
    const stubs = await stubRuns(page, {
      retry: (route) =>
        route.fulfill({
          status: 409,
          json: { errors: [{ reason: 'That action is running now. Wait for it to finish.' }] },
        }),
    });

    await page.goto('/workflow-runs');
    await page.getByRole('radio', { name: /Announce a post/ }).check();
    await expect(actions(page)).toHaveCount(5);
    const readsBefore = stubs.detailReads.length;

    await page.getByRole('button', { name: /Retry action 2/ }).click();

    await expect(page.getByText('That action is running now. Wait for it to finish.')).toBeVisible();
    expect(stubs.detailReads.length).toBe(readsBefore);
  });

  test('every retry button is disabled while one retry is in flight', async ({ page }) => {
    await authed(page);
    await stubShell(page);

    let release = () => {};
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });

    await stubRuns(page, {
      retry: async (route) => {
        await held;
        await route.fulfill({ json: { ...PARTLY_FAILED, status: 'Running' } });
      },
    });

    await page.goto('/workflow-runs');
    await page.getByRole('radio', { name: /Announce a post/ }).check();
    await expect(actions(page)).toHaveCount(5);

    const second = page.getByRole('button', { name: /Retry action 2/ });
    const third = page.getByRole('button', { name: /Retry action 3/ });
    await expect(second).toBeEnabled();
    await expect(third).toBeEnabled();

    await second.click();

    // The page has to raise the in-flight flag, not merely pass one the card knows how to honour.
    // Pressing retry twice sends the action twice, and the second post is what the idempotency key
    // is there to absorb rather than something the screen should invite.
    await expect(second).toBeDisabled();
    await expect(third).toBeDisabled();

    release();

    await expect(page.getByText('Queued that action to run again.')).toBeVisible();
    await expect(third).toBeEnabled();
  });

  test('fits a phone screen, so the retry button can be pressed', async ({ page }) => {
    await page.setViewportSize({ width: 393, height: 851 });
    await open(page);

    await page.getByRole('radio', { name: /Announce a post/ }).check();
    await expect(actions(page)).toHaveCount(5);

    // The two-column grid used to let the table's own width grow the track, so the page scrolled
    // sideways and the detail card sat past the right edge with the retry button on it.
    const width = await page.evaluate(() => ({
      scroll: document.documentElement.scrollWidth,
      client: document.documentElement.clientWidth,
    }));
    expect(width.scroll).toBe(width.client);

    const retry = page.getByRole('button', { name: /Retry action 2/ });
    const box = await retry.boundingBox();
    expect(box).not.toBeNull();
    expect(box!.x + box!.width).toBeLessThanOrEqual(width.client);

    // The measurement is the diagnosis; the click is the thing an operator could not do.
    await retry.click();
    await expect(page.getByText('Queued that action to run again.')).toBeVisible();
  });
});

/**
 * Paging, which the single-page fixture above cannot reach: PaginationControls renders nothing when
 * there is only one page, so the page-change handler and the reset-to-page-one both sit unexercised.
 */
test.describe('workflow runs paging', () => {
  /** One run per page, so there is always a next page, and reports the query each read carried. */
  async function stubPagedRuns(page: Page) {
    const asked: URL[] = [];

    await page.route(/\/api\/workflow-runs(\?|$)/, (route) => {
      const url = new URL(route.request().url());
      asked.push(url);

      const status = url.searchParams.get('status');
      const source = status ? RUNS.filter((r) => r.status === status) : RUNS;
      const wanted = Number(url.searchParams.get('page') ?? '1');
      const item = source[wanted - 1];

      return route.fulfill({
        json: {
          items: item ? [item] : [],
          page: wanted,
          pageSize: 1,
          totalItems: source.length,
          totalPages: source.length,
          hasNextPage: wanted < source.length,
          hasPreviousPage: wanted > 1,
        },
      });
    });

    return asked;
  }

  test('next asks the API for the following page', async ({ page }) => {
    await authed(page);
    await stubShell(page);
    const asked = await stubPagedRuns(page);

    await page.goto('/workflow-runs');
    const table = page.getByRole('table');
    await expect(table.getByText('Announce a post')).toBeVisible({ timeout: 15000 });

    await page.getByRole('button', { name: 'Next', exact: true }).click();

    await expect(table.getByText('Tidy the index')).toBeVisible();
    await expect(table.getByText('Announce a post')).toBeHidden();
    expect(asked.map((url) => url.searchParams.get('page'))).toContain('2');
  });

  test('choosing a status from page 2 starts the filtered list at page 1', async ({ page }) => {
    await authed(page);
    await stubShell(page);
    const asked = await stubPagedRuns(page);

    await page.goto('/workflow-runs');
    await expect(page.getByRole('table').getByText('Announce a post')).toBeVisible({ timeout: 15000 });

    await page.getByRole('button', { name: 'Next', exact: true }).click();
    await expect(page.getByRole('table').getByText('Tidy the index')).toBeVisible();

    await page.getByLabel('Filter runs by status').click();
    await page.getByRole('option', { name: 'Failed', exact: true }).click();

    // Page 2 of everything is not page 2 of one status. Staying there reads an empty page for a
    // filter that has a match, which is the screen telling the operator nothing is Failed.
    await expect
      .poll(() =>
        asked
          .filter((url) => url.searchParams.get('status') === 'Failed')
          .map((url) => url.searchParams.get('page'))
      )
      .toEqual(['1']);
    await expect(page.getByRole('table').getByText('Push to the CDN')).toBeVisible();
  });
});
