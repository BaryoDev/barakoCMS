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
async function stubRuns(page: Page, options: { retry?: (route: import('@playwright/test').Route) => void } = {}) {
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
});
