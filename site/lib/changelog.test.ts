import { describe, it, expect } from 'vitest';
import { renderMarkdown, isSafeHref, readReleases } from './changelog';

/*
 * CHANGELOG.md is edited by pull request, so its contents are not trusted. Everything rendered from
 * it reaches every visitor of a static page with no runtime in front of it, which makes a review the
 * only other thing standing in the way — and a changelog entry is close to the last place a reviewer
 * looks for an attack.
 *
 * Both vectors here were live at some point during this file's development. Raw HTML was blocked
 * first and looked sufficient; `javascript:` link destinations are ordinary markdown, so they
 * survived that and rendered untouched.
 */
describe('changelog rendering drops hostile markup', () => {
  it('removes raw HTML blocks and inline handlers', () => {
    const html = renderMarkdown(
      `<script>alert(1)</script>\n\nText with <img src=x onerror="alert(2)"> inline.\n\n<div onclick="alert(3)">d</div>`,
    );
    expect(html).not.toContain('<script');
    expect(html).not.toContain('onerror');
    expect(html).not.toContain('onclick');
  });

  it.each([
    ['javascript:', `[x](javascript:alert('a'))`],
    ['mixed case javascript:', `[x](JaVaScRiPt:alert('b'))`],
    ['leading whitespace', `[x]( javascript:alert('c'))`],
    ['data: html', `[x](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)`],
    ['vbscript:', `[x](vbscript:msgbox(1))`],
    ['image with javascript:', `![x](javascript:alert('d'))`],
  ])('strips a %s destination', (_label, md) => {
    const html = renderMarkdown(md);
    expect(html).not.toMatch(/javascript:/i);
    expect(html).not.toMatch(/vbscript:/i);
    expect(html).not.toMatch(/data:text\/html/i);
  });

  it('keeps the visible text when it drops an unsafe link, rather than losing the content', () => {
    expect(renderMarkdown(`[click me](javascript:alert(1))`)).toContain('click me');
  });

  /*
   * The alt text of a rejected image is a raw string rather than tokens, so it went straight into
   * the output and carried markup with it. This bypassed the raw-HTML override entirely.
   */
  it.each([
    ['script tag', '![x <script>alert(1)</script> y](javascript:x)', /<script/i],
    ['svg onload', '![<svg onload=alert(1)>](javascript:x)', /<svg/i],
    ['bold tags', '![a<b>c</b>d](javascript:x)', /<b>/i],
  ])('escapes %s in the alt text of a rejected image', (_label, md, forbidden) => {
    const html = renderMarkdown(md);
    expect(html).not.toMatch(forbidden);
  });

  it('keeps rejected image alt text readable once escaped', () => {
    expect(renderMarkdown('![the alt text](javascript:x)')).toContain('the alt text');
  });

  it('escapes markup in the text of a rejected link too', () => {
    expect(renderMarkdown('[a <script>alert(1)</script> b](javascript:x)')).not.toMatch(/<script/i);
  });

  it('leaves safe destinations alone', () => {
    const html = renderMarkdown(
      `[a](https://example.com/x) [b](/docs/x) [c](#v3-20-1) [d](mailto:x@example.com)`,
    );
    expect(html).toContain('href="https://example.com/x"');
    expect(html).toContain('href="/docs/x"');
    expect(html).toContain('href="#v3-20-1"');
    expect(html).toContain('href="mailto:x@example.com"');
  });

  it('renders ordinary markdown', () => {
    const html = renderMarkdown('### H\n\n**bold** and `code`\n\n- item');
    expect(html).toContain('<h3>');
    expect(html).toContain('<strong>bold</strong>');
    expect(html).toContain('<code>code</code>');
    expect(html).toContain('<li>');
  });

  /*
   * Angle brackets inside backticks are code, not markup. The entries use them for `<Version>` and
   * `session.Query<Account>()`, and an HTML-stripping pass that ran before the code tokenizer would
   * silently eat them.
   */
  it('keeps angle brackets that appear inside inline code', () => {
    const html = renderMarkdown('the module `<Version>` and `session.Query<Account>()`');
    expect(html).toContain('&lt;Version&gt;');
    expect(html).toContain('Query&lt;Account&gt;');
  });
});

describe('isSafeHref', () => {
  it.each(['https://x.test/a', 'http://x.test', 'mailto:a@x.test', '/relative', '#anchor', 'docs/x'])(
    'allows %s',
    (href) => expect(isSafeHref(href)).toBe(true),
  );

  it.each(['javascript:alert(1)', ' javascript:alert(1)', 'JAVASCRIPT:alert(1)', 'data:text/html,x', 'vbscript:x', 'file:///etc/passwd'])(
    'rejects %s',
    (href) => expect(isSafeHref(href)).toBe(false),
  );
});

describe('readReleases', () => {
  it('parses the real CHANGELOG.md and finds the current release', () => {
    const releases = readReleases();
    expect(releases.length).toBeGreaterThan(10);

    const shipped = releases.filter((r) => !r.unreleased);
    expect(shipped.length).toBeGreaterThan(10);

    // Every shipped entry needs a version, a date and a body, or the page renders a blank section.
    // The day is optional only because 3.0.0 predates the current discipline and was never
    // published to NuGet as a stable version; everything since carries a full date.
    for (const r of shipped) {
      expect(r.version).toMatch(/^\d+\.\d+/);
      expect(r.date).toMatch(/^\d{4}-\d{2}(-\d{2})?$/);
      expect(r.html.trim().length).toBeGreaterThan(0);
    }

    /*
     * Exactly one Unreleased section. A second one is not cosmetic: the page renders the first as
     * Unreleased and filters the rest out of the shipped list, so a stray one makes a whole release
     * invisible on the site. That is what happened to 3.1.1, whose security hardening entry sat
     * mislabelled as Unreleased from July until this test was written.
     */
    expect(releases.filter((r) => r.unreleased)).toHaveLength(1);

    // Anchors are linked from the version index; a duplicate would make one of them unreachable.
    const slugs = shipped.map((r) => r.slug);
    expect(new Set(slugs).size).toBe(slugs.length);
  });
});
