import type { Metadata } from 'next';
import Link from 'next/link';
import { Bricolage_Grotesque, IBM_Plex_Sans, IBM_Plex_Mono } from 'next/font/google';
import { Bean } from './bean';
import './globals.css';

/*
 * Self-hosted at build time by next/font, so the static export makes no request to Google at run
 * time. That matters here: the site is served from a VM we own, and a third-party font request
 * would be the only external call on the page.
 *
 * Bricolage is a variable grotesque with uneven widths and a slightly industrial cut, which suits a
 * product named after a coffee that is not the refined one. Plex Sans and Plex Mono share a
 * skeleton, so labels and code sit next to prose without a seam of their own.
 */
const bricolage = Bricolage_Grotesque({
  subsets: ['latin'],
  variable: '--font-bricolage',
  display: 'swap',
});

const plexSans = IBM_Plex_Sans({
  subsets: ['latin'],
  weight: ['400', '500', '600'],
  variable: '--font-plex-sans',
  display: 'swap',
});

const plexMono = IBM_Plex_Mono({
  subsets: ['latin'],
  weight: ['400', '500'],
  variable: '--font-plex-mono',
  display: 'swap',
});

const SITE = 'https://barakocms.com';

export const metadata: Metadata = {
  metadataBase: new URL(SITE),
  title: {
    default: 'BarakoCMS · the .NET backend your next project already needs',
    template: '%s · BarakoCMS',
  },
  description:
    'An open-source .NET 8 base for any project: content, users, roles, per-field permissions, workflow, audit history and multi-tenancy, with optional modules for everything else. Bring your own frontend.',
  openGraph: {
    title: 'BarakoCMS',
    description: 'An open-source .NET 8 base for any project. Bring your own frontend.',
    url: SITE,
    siteName: 'BarakoCMS',
    type: 'website',
  },
};

const GITHUB = 'https://github.com/BaryoDev/barakoCMS';
const DISCORD = 'https://discord.gg/7GYKzDx7Z2';

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" className={`${bricolage.variable} ${plexSans.variable} ${plexMono.variable}`}>
      <body className="flex min-h-screen flex-col">
        <a href="#main" className="skip-link">
          Skip to content
        </a>
        <header className="border-b border-rule">
          {/*
            Wraps on narrow screens. Fixed at h-16 with no wrapping, the five links ran to 506px
            against a 360px viewport, so the whole page scrolled sideways on a phone. The spacer
            that pushes the links right only exists from sm up, because in a wrapping row it would
            force a break of its own.
          */}
          <nav className="mx-auto flex max-w-5xl flex-wrap items-center gap-x-5 gap-y-2 px-6 py-3 sm:h-16 sm:gap-x-7 sm:py-0">
            <Link
              href="/"
              className="flex items-center gap-2.5 font-display text-[19px] font-semibold tracking-[-0.02em]"
            >
              <Bean size={26} />
              BarakoCMS
            </Link>
            <div className="hidden flex-1 sm:block" />
            <Link href="/marketplace/" className="text-sm hover:text-bean">
              Modules
            </Link>
            <Link href="/changelog/" className="text-sm hover:text-bean">
              Changelog
            </Link>
            <a href={`${GITHUB}#quick-start`} className="text-sm hover:text-bean">
              Docs
            </a>
            <a href={DISCORD} className="text-sm hover:text-bean">
              Discord
            </a>
            <a
              href={GITHUB}
              className="rounded-sm border border-rule px-3 py-1.5 text-sm font-medium hover:border-bean hover:text-bean"
            >
              GitHub
            </a>
          </nav>
        </header>

        {/* tabIndex allows the skip link to move focus here without adding main to the tab order. */}
        <main id="main" tabIndex={-1} className="flex-1 focus:outline-none">
          {children}
        </main>

        <footer className="border-t border-rule">
          <div className="mx-auto flex max-w-5xl flex-wrap gap-x-8 gap-y-3 px-6 py-10 text-sm text-muted">
            <span>MPL-2.0</span>
            <a href={GITHUB} className="hover:text-bean">
              Source
            </a>
            <Link href="/changelog/" className="hover:text-bean">
              Changelog
            </Link>
            <a href={`${GITHUB}/blob/master/CONTRIBUTING.md`} className="hover:text-bean">
              Contributing
            </a>
            <a href={DISCORD} className="hover:text-bean">
              Discord
            </a>
            <span className="flex-1" />
            <span>
              A star on{' '}
              <a href={GITHUB} className="text-bean hover:underline">
                GitHub
              </a>{' '}
              helps other people find it.
            </span>
          </div>
        </footer>
      </body>
    </html>
  );
}
