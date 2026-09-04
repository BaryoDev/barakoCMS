import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { RUN_STATUSES } from './use-workflow-runs';

/**
 * RUN_STATUSES is what the filter dropdown offers, and the server's RunStatus is what it filters on.
 * Nothing in TypeScript can hold those two together, so this reads the enum out of the C# and
 * compares the names.
 *
 * Asserting the list against itself is the failure this exists to stop: a count, or a copy of the
 * same names in a spec, stays green when a status is dropped, and an operator is then left with no
 * way to filter for it.
 *
 * Written as a path join rather than `new URL(..., import.meta.url)` because Vite rewrites that
 * pattern into an asset URL and the result is no longer a file path.
 */
const MODEL = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  '../../../barakoCMS/Models/WorkflowRun.cs'
);

function serverRunStatuses(): string[] {
  const source = readFileSync(MODEL, 'utf8');
  const body = source.match(/enum\s+RunStatus\s*\{([^}]*)\}/);
  if (!body) throw new Error(`No "enum RunStatus" declaration in ${MODEL}`);

  return body[1]
    .split(',')
    .map((member) => member.split('=')[0].trim())
    .filter((name) => name.length > 0);
}

describe('RUN_STATUSES', () => {
  it('is every RunStatus the server defines, in the order the server declares them', () => {
    const names = serverRunStatuses();

    // The parse can fail open: a regex that matched something useless would let any list through.
    expect(names.length).toBeGreaterThan(1);
    expect(names).toContain('PartiallyFailed');

    expect([...RUN_STATUSES]).toEqual(names);
  });
});
