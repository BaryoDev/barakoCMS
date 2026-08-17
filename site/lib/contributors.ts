import { readFileSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Contributors shown on the site.
 *
 * Deliberately not derived from the GitHub contributors API. That lists commit authors only, and the
 * most useful contributions this project has had were a bug report that stopped a redundant issue
 * being built and a diagnosis that closed half of another. Crediting only commits would show neither.
 *
 * Read from .all-contributorsrc, the same file the README renders from, so the two cannot drift.
 * Read at build time in a server component, which is why plain fs is fine here.
 */
export type Contributor = {
  login: string;
  name: string;
  profile: string;
  contributions: string[];
};

/** What each all-contributors key means, in words rather than emoji. */
const LABELS: Record<string, string> = {
  code: 'Code',
  bug: 'Bug reports',
  doc: 'Documentation',
  review: 'Review',
  ideas: 'Ideas',
  design: 'Design',
  test: 'Tests',
  infra: 'Infrastructure',
  security: 'Security',
  example: 'Examples',
};

/**
 * A contributor profile link, or undefined if it is not a plain GitHub profile URL.
 *
 * `.all-contributorsrc` is edited by the all-contributors bot in response to a comment on an issue,
 * so its contents are influenced by anyone who can comment. React does not sanitize `href`, so a
 * `javascript:` URL in that file would execute on click. Same lesson as the package icons on the
 * marketplace: validate the URL where the data is read, not where it is rendered.
 */
export function safeProfileUrl(raw: string | undefined, login: string): string {
  const fallback = `https://github.com/${encodeURIComponent(login)}`;
  if (!raw) return fallback;
  try {
    const url = new URL(raw);
    if (url.protocol !== 'https:') return fallback;
    return url.host === 'github.com' ? url.toString() : fallback;
  } catch {
    return fallback;
  }
}

/**
 * A parsed entry is only usable if it actually has the fields the page reads. The file is edited by
 * the all-contributors bot in response to an issue comment, so a malformed record is a realistic
 * input rather than a hypothetical one, and one bad entry should not fail the whole build.
 */
function isContributor(v: unknown): v is Contributor {
  if (typeof v !== 'object' || v === null) return false;
  const c = v as Record<string, unknown>;
  return (
    typeof c.login === 'string' &&
    c.login.length > 0 &&
    Array.isArray(c.contributions) &&
    c.contributions.every((x) => typeof x === 'string')
  );
}

export function contributors(): Contributor[] {
  try {
    const raw = readFileSync(join(process.cwd(), '..', '.all-contributorsrc'), 'utf8');
    const parsed: unknown = JSON.parse(raw).contributors;
    if (!Array.isArray(parsed)) return [];

    return parsed.filter(isContributor).map((c) => ({
      ...c,
      name: typeof c.name === 'string' && c.name.length > 0 ? c.name : c.login,
      profile: safeProfileUrl(c.profile, c.login),
    }));
  } catch {
    // A missing or unreadable file must not fail the build. The section simply does not render.
    return [];
  }
}

export function describe(contributions: string[]): string {
  const named = contributions.map((c) => LABELS[c] ?? c);
  if (named.length <= 1) return named[0] ?? '';
  return `${named.slice(0, -1).join(', ')} and ${named[named.length - 1].toLowerCase()}`;
}
