import type { Metadata } from 'next';
import { readReleases, formatDate } from '@/lib/changelog';

export const metadata: Metadata = {
  title: 'Changelog',
  description:
    'Every released version of BarakoCMS, what changed in it and why. Rendered from the CHANGELOG.md in the repository, so it cannot drift from what actually shipped.',
};

const GITHUB = 'https://github.com/BaryoDev/barakoCMS';

export default function Changelog() {
  const releases = readReleases();
  const unreleased = releases.find((r) => r.unreleased);
  const shipped = releases.filter((r) => !r.unreleased);
  const latest = shipped[0];

  return (
    <>
      <section className="border-b border-rule bg-surface">
        <div className="mx-auto max-w-5xl px-6 py-16">
          <h1 className="font-display text-[clamp(32px,5vw,46px)] leading-tight tracking-tight">
            Changelog
          </h1>
          <p className="mt-5 text-lg text-ink-2 max-w-[62ch] leading-relaxed">
            What changed in each version, and why it changed. Entries explain the reasoning and name
            the bug where there was one, so you can tell an upgrade you need from one you can skip.
          </p>
          <p className="mt-4 text-[15px] text-muted max-w-[62ch] leading-relaxed">
            This page is rendered from{' '}
            <a href={`${GITHUB}/blob/master/CHANGELOG.md`} className="text-bean hover:underline">
              CHANGELOG.md
            </a>{' '}
            in the repository. There is no second copy to fall out of date.
          </p>

          {latest && (
            <div className="mt-8 flex flex-wrap items-center gap-3 text-sm">
              <span className="rounded-md bg-bean px-2.5 py-1 font-mono text-[13px] text-ground">
                {latest.version}
              </span>
              <span className="text-muted">
                current release{latest.date ? `, ${formatDate(latest.date)}` : ''}
              </span>
              <a
                href={`https://www.nuget.org/packages/BarakoCMS/${latest.version}`}
                className="text-bean hover:underline"
              >
                on NuGet
              </a>
            </div>
          )}
        </div>
      </section>

      <div className="mx-auto max-w-5xl px-6 py-14">
        <div className="lg:grid lg:grid-cols-[minmax(0,1fr)_190px] lg:gap-12">
          <div className="min-w-0">
            {unreleased && unreleased.html.trim().length > 0 && (
              <article className="mb-16">
                <header className="mb-5 border-b border-rule pb-4">
                  <h2
                    id={unreleased.slug}
                    className="font-display text-[26px] tracking-tight scroll-mt-24"
                  >
                    Unreleased
                  </h2>
                  <p className="mt-1.5 text-[14px] text-muted">
                    Merged to master and not yet published to NuGet.
                  </p>
                </header>
                <div
                  className="changelog-body"
                  dangerouslySetInnerHTML={{ __html: unreleased.html }}
                />
              </article>
            )}

            {shipped.map((release) => (
              <article key={release.slug} className="mb-16">
                <header className="mb-5 border-b border-rule pb-4">
                  <h2
                    id={release.slug}
                    className="font-display text-[26px] tracking-tight scroll-mt-24"
                  >
                    <a href={`#${release.slug}`} className="hover:text-bean">
                      {release.version}
                    </a>
                  </h2>
                  {release.date && (
                    <p className="mt-1.5 text-[14px] text-muted">
                      <time dateTime={release.date}>{formatDate(release.date)}</time>
                    </p>
                  )}
                </header>
                <div
                  className="changelog-body"
                  dangerouslySetInnerHTML={{ __html: release.html }}
                />
              </article>
            ))}
          </div>

          {/* Version index. Hidden below lg, where the page is already a single column to scroll. */}
          <nav aria-label="Versions" className="hidden lg:block">
            <div className="sticky top-8">
              <p className="text-[13px] font-medium uppercase tracking-wider text-muted">Versions</p>
              <ul className="mt-3 max-h-[70vh] space-y-1.5 overflow-y-auto pr-2 text-sm">
                {unreleased && unreleased.html.trim().length > 0 && (
                  <li>
                    <a href={`#${unreleased.slug}`} className="text-muted hover:text-bean">
                      Unreleased
                    </a>
                  </li>
                )}
                {shipped.map((release) => (
                  <li key={release.slug}>
                    <a href={`#${release.slug}`} className="font-mono text-[13px] text-muted hover:text-bean">
                      {release.version}
                    </a>
                  </li>
                ))}
              </ul>
            </div>
          </nav>
        </div>
      </div>
    </>
  );
}
