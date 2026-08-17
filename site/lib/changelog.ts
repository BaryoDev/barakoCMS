import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { marked } from 'marked';

/*
 * The changelog lives at the repository root and is the same file the release process reads. The
 * site renders it rather than keeping a second copy, because two changelogs diverge and the one on
 * the website is the one that would go stale without anybody noticing.
 *
 * Read at build time. The site is a static export, so this runs once during `next build` and the
 * result is baked into HTML; nothing reads the filesystem in the browser.
 */
const CHANGELOG_PATH = join(process.cwd(), '..', 'CHANGELOG.md');

/*
 * Markdown only: raw HTML in the source is dropped rather than passed through.
 *
 * The changelog is edited by pull request, so its contents are not automatically trusted. Without
 * this, a `<script>` in a CHANGELOG.md diff would be baked into the static export and run for every
 * visitor — a review would have to catch it every time, and reviews of a changelog entry are the
 * least likely place anyone is looking for that. Dropping HTML costs nothing, because the file is
 * plain markdown throughout.
 */
const renderer = new marked.Renderer();
renderer.html = () => '';

/*
 * Link destinations are allowlisted by protocol. Dropping raw HTML is not enough on its own:
 * `[text](javascript:...)` is ordinary markdown, so it survives that and marked emits the href
 * untouched — it does not sanitise, by design, and says so.
 *
 * Resolving against a base URL rather than string-matching the prefix is deliberate. It normalises
 * case, leading whitespace and embedded control characters, so `JaVaScRiPt:` and ` javascript:` are
 * the same thing to this check, and relative paths come back as https because they inherit the base.
 */
const SAFE_PROTOCOLS = new Set(['http:', 'https:', 'mailto:']);

export function isSafeHref(href: string): boolean {
  const raw = href.trim();
  if (raw.startsWith('#')) return true; // in-page anchor
  try {
    return SAFE_PROTOCOLS.has(new URL(raw, 'https://barakocms.baryo.dev/').protocol);
  } catch {
    return false;
  }
}

const renderLink = renderer.link.bind(renderer);
renderer.link = function (token) {
  // Unsafe destination: keep the visible text, lose the link. Nothing silently disappears.
  if (!isSafeHref(token.href)) return this.parser.parseInline(token.tokens);
  return renderLink(token);
};

/*
 * Alt text is a raw string, not tokens, so it has to be escaped on the way out. Returning it
 * unescaped reopened everything the html override closes: the alt text of an image with a rejected
 * destination is a straight path into the HTML stream, and
 * `![x <script>alert(1)</script> y](javascript:...)` emitted a live script tag through it.
 */
const escapeHtml = (s: string): string =>
  s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

const renderImage = renderer.image.bind(renderer);
renderer.image = function (token) {
  if (!isSafeHref(token.href)) return escapeHtml(token.text ?? '');
  return renderImage(token);
};

/**
 * Renders a changelog fragment to HTML with raw HTML dropped and link destinations allowlisted.
 * Exported so the tests exercise this exact path rather than a reconstruction of it.
 */
export function renderMarkdown(md: string): string {
  return marked.parse(md, { async: false, renderer }) as string;
}

export type Release = {
  version: string;
  /** ISO date from the heading, or null for the Unreleased section. */
  date: string | null;
  /** URL-safe anchor, e.g. "v3-20-1". */
  slug: string;
  /** Rendered HTML for everything under the version heading. */
  html: string;
  unreleased: boolean;
};

/*
 * Matches `## [3.20.1] - 2026-08-15` and `## [Unreleased]`. The date is optional because the
 * Unreleased section carries no date, and anything else with a bracketed version is treated the
 * same way rather than silently dropped.
 */
const HEADING = /^## \[([^\]]+)\](?:\s*-\s*(\S+))?\s*$/;

function slugify(version: string): string {
  return `v${version.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')}`;
}

export function readReleases(): Release[] {
  let raw: string;
  try {
    raw = readFileSync(CHANGELOG_PATH, 'utf8');
  } catch (cause) {
    /*
     * Fail the build rather than shipping an empty changelog page. A missing file here means the
     * build context does not include the repository root, which is a deployment mistake that a
     * blank page would hide until someone happened to look.
     */
    throw new Error(`Could not read ${CHANGELOG_PATH}. The site build needs the repository root.`, {
      cause,
    });
  }

  const lines = raw.split('\n');
  const releases: Release[] = [];
  let current: { version: string; date: string | null; body: string[] } | null = null;

  const flush = () => {
    if (!current) return;
    const body = current.body.join('\n').trim();
    releases.push({
      version: current.version,
      date: current.date,
      slug: slugify(current.version),
      html: marked.parse(body, { async: false, renderer }) as string,
      unreleased: current.version.toLowerCase() === 'unreleased',
    });
  };

  for (const line of lines) {
    const match = HEADING.exec(line);
    if (match) {
      flush();
      current = { version: match[1], date: match[2] ?? null, body: [] };
      continue;
    }
    current?.body.push(line);
  }
  flush();

  /*
   * An empty result means the heading format changed and every section was swallowed. That is
   * exactly the kind of failure that renders as a plausible-looking page, so it stops the build.
   */
  if (releases.length === 0) {
    throw new Error('Parsed no releases from CHANGELOG.md. Has the `## [version] - date` format changed?');
  }

  return releases;
}

/** "2026-08-15" to "15 August 2026". Returns the input unchanged if it is not a date we recognise. */
export function formatDate(iso: string | null): string | null {
  if (!iso) return null;
  const parsed = new Date(`${iso}T00:00:00Z`);
  if (Number.isNaN(parsed.getTime())) return iso;
  return parsed.toLocaleDateString('en-GB', {
    day: 'numeric',
    month: 'long',
    year: 'numeric',
    timeZone: 'UTC',
  });
}
