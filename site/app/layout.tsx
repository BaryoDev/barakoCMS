import type { Metadata } from 'next';
import Link from 'next/link';
import { Bean } from './bean';
import './globals.css';

export const metadata: Metadata = {
  metadataBase: new URL('https://barakocms.baryo.dev'),
  title: {
    default: 'BarakoCMS · the .NET backend your next project already needs',
    template: '%s · BarakoCMS',
  },
  description:
    'An open-source .NET 8 base for any project: content, users, roles, per-field permissions, workflow, audit history and multi-tenancy, with optional modules for everything else. Bring your own frontend.',
  openGraph: {
    title: 'BarakoCMS',
    description: 'An open-source .NET 8 base for any project. Bring your own frontend.',
    url: 'https://barakocms.baryo.dev',
    siteName: 'BarakoCMS',
    type: 'website',
  },
};

const GITHUB = 'https://github.com/BaryoDev/barakoCMS';
const DISCORD = 'https://discord.gg/M2BuZn6X3';

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <body className="min-h-screen flex flex-col">
        <a href="#main" className="skip-link">
          Skip to content
        </a>
        <header className="border-b border-rule">
          <nav className="mx-auto max-w-5xl px-6 h-16 flex items-center gap-7">
            <Link href="/" className="flex items-center gap-2.5 font-display text-[19px] tracking-tight">
              <Bean size={26} />
              BarakoCMS
            </Link>
            <div className="flex-1" />
            <Link href="/marketplace/" className="text-sm hover:text-bean">
              Modules
            </Link>
            <a href={`${GITHUB}#quick-start`} className="text-sm hover:text-bean">
              Docs
            </a>
            <a href={DISCORD} className="text-sm hover:text-bean">
              Discord
            </a>
            <a
              href={GITHUB}
              className="text-sm font-medium rounded-md border border-rule px-3 py-1.5 hover:border-bean hover:text-bean"
            >
              GitHub
            </a>
          </nav>
        </header>

        <main id="main" className="flex-1">{children}</main>

        <footer className="border-t border-rule mt-24">
          <div className="mx-auto max-w-5xl px-6 py-10 text-sm text-muted flex flex-wrap gap-x-8 gap-y-3">
            <span>MPL-2.0</span>
            <a href={GITHUB} className="hover:text-bean">
              Source
            </a>
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
