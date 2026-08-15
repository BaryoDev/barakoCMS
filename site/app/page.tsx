import Link from 'next/link';
import { Bean } from './bean';
import { fetchModules } from '@/lib/nuget';

const GITHUB = 'https://github.com/BaryoDev/barakoCMS';
const BARYOVM = 'https://github.com/BaryoDev/BaryoVM';

export default async function Home() {
  const { modules } = await fetchModules();
  const moduleCount = modules.filter((m) => m.id !== 'BarakoCMS').length;

  return (
    <>
      {/* The thesis: this is a base to build on, not a blog engine. */}
      <section className="border-b border-rule bg-surface">
        <div className="mx-auto max-w-5xl px-6 py-20 md:py-28">
          <Bean size={56} className="bean-in mb-8" />
          <h1 className="font-display text-[clamp(38px,6vw,62px)] leading-[1.04] tracking-tight text-balance max-w-[17ch]">
            The backend your next project already needs
          </h1>
          <p className="mt-6 text-lg text-ink-2 max-w-[60ch] leading-relaxed">
            Content, users, roles, permissions, workflow, audit history, multi-tenancy. Every project
            needs them and nobody wants to write them again. BarakoCMS is an open-source .NET 8 base
            that brings them along, so you build the part that is actually yours.
          </p>

          <div className="mt-9 flex flex-wrap items-center gap-3">
            <code className="font-mono text-sm bg-ground border border-rule rounded-md px-4 py-2.5">
              dotnet add package BarakoCMS
            </code>
            <Link
              href="/marketplace/"
              className="text-sm font-medium rounded-md bg-bean text-ground px-4 py-2.5 hover:opacity-90"
            >
              Browse {moduleCount} modules
            </Link>
          </div>
        </div>
      </section>

      {/* The positioning, said plainly: it is not only for content sites. */}
      <section className="mx-auto max-w-5xl px-6 py-20">
        <h2 className="font-display text-3xl tracking-tight">Not only for content sites</h2>
        <p className="mt-4 text-ink-2 max-w-[64ch] leading-relaxed">
          A blog, an events platform, a membership system, a treasury — different products, same
          foundations underneath. They all need somewhere to define records, decide who may read
          which field, keep an audit trail, and serve it over an API.
        </p>
        <p className="mt-4 text-ink-2 max-w-[64ch] leading-relaxed">
          BarakoCMS is that layer. Your frontend is yours, in whatever framework you like. Your
          domain rules are yours. What you inherit is the part that is the same every time.
        </p>

        <div className="mt-9 rounded-lg border border-rule bg-surface p-6">
          <p className="text-[15px] leading-relaxed">
            <strong className="font-medium">The accounting module is the proof.</strong> A
            double-entry ledger — accounts, balanced journal entries, immutable postings, statements
            — is not a content feature by any reading. It is a content type with rules attached, and
            it needed no changes to the core.
          </p>
        </div>
      </section>

      {/* Setup, shown rather than described. */}
      <section className="border-t border-rule bg-surface">
        <div className="mx-auto max-w-5xl px-6 py-20">
          <h2 className="font-display text-3xl tracking-tight">Three lines to start</h2>
          <p className="mt-3 text-muted max-w-[62ch]">
            No scaffolding step and no generated project to maintain. It is a service registration in
            an ASP.NET app you already have.
          </p>
          <pre className="mt-7 overflow-x-auto rounded-lg border border-rule bg-ground p-5">
            <code className="font-mono text-[13.5px] leading-relaxed">{`builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
app.Run();`}</code>
          </pre>
          <p className="mt-5 text-sm text-muted">
            Needs PostgreSQL. Content, authentication, roles, workflow and the delivery API come with
            it.
          </p>
        </div>
      </section>

      {/* Specific behaviours, not feature words. */}
      <section className="mx-auto max-w-5xl px-6 py-20">
        <h2 className="font-display text-3xl tracking-tight">What you inherit</h2>
        <div className="mt-10 grid gap-x-12 gap-y-9 sm:grid-cols-2">
          {[
            {
              h: 'Records defined at runtime',
              p: 'Add a field without a rebuild or a migration. Types are data, not classes you deploy.',
            },
            {
              h: 'Sensitivity per field',
              p: 'Hide a phone number without hiding the record. The public API emits an allowlist, so a renamed field cannot leak by accident.',
            },
            {
              h: 'Publishing is a decision',
              p: 'Nothing is served anonymously until a type opts in. The default is private, because a default is what you get when nobody chooses.',
            },
            {
              h: 'Real history',
              p: 'Event sourced on Marten and PostgreSQL, so a record has a past rather than a last-writer-wins row.',
            },
            {
              h: 'Multi-tenant',
              p: 'One deployment, many customers, one database, scoped per tenant.',
            },
            {
              h: 'Modules, not a monolith',
              p: 'Accounting, semantic search, feature flags, file storage, social sign-in. Take what you need; the core does not grow.',
            },
          ].map((f) => (
            <div key={f.h}>
              <h3 className="font-medium">{f.h}</h3>
              <p className="mt-1.5 text-[15px] text-ink-2 leading-relaxed">{f.p}</p>
            </div>
          ))}
        </div>
      </section>

      {/* Running it. The honest ops story, including that it is deliberately not a PaaS. */}
      <section className="border-t border-rule bg-surface">
        <div className="mx-auto max-w-5xl px-6 py-20">
          <h2 className="font-display text-3xl tracking-tight">And a way to run it</h2>
          <p className="mt-4 text-ink-2 max-w-[64ch] leading-relaxed">
            A base is only useful if you can keep it running. <strong className="font-medium">BaryoVM</strong>{' '}
            is a companion CLI that deploys a stack to a VM you already own, over SSH, with no agent
            to install. It takes a backup before it updates, checks the site is healthy afterwards,
            and puts the previous images back if it is not.
          </p>
          <p className="mt-4 text-ink-2 max-w-[64ch] leading-relaxed">
            Optional, and separate on purpose — BarakoCMS is an ordinary .NET app and runs anywhere
            .NET runs.
          </p>
          <a
            href={BARYOVM}
            className="mt-7 inline-block text-sm rounded-md border border-rule px-4 py-2.5 hover:border-bean hover:text-bean"
          >
            BaryoVM on GitHub
          </a>
        </div>
      </section>

      {/* Contribution invitation. */}
      <section className="mx-auto max-w-5xl px-6 py-20">
        <h2 className="font-display text-3xl tracking-tight">Built in the open</h2>
        <p className="mt-4 text-ink-2 max-w-[64ch] leading-relaxed">
          Issues labelled <em>good first issue</em> carry enough context to start without asking, and
          not all of them are C#. Module icons, documentation and examples all count — a README is
          the package page on NuGet, so a clearer one is a real improvement, not a cosmetic one.
        </p>
        <p className="mt-4 text-ink-2 max-w-[64ch] leading-relaxed">
          Building a module is the other way in. Publish it under your own name with the{' '}
          <code className="font-mono text-[13px] bg-surface border border-rule rounded px-1.5 py-0.5">
            barakocms-module
          </code>{' '}
          tag and it appears in the marketplace automatically. There is no submission queue.
        </p>
        <div className="mt-7 flex flex-wrap gap-3">
          <a
            href={`${GITHUB}/issues?q=is%3Aopen+label%3A%22good+first+issue%22`}
            className="text-sm rounded-md border border-rule px-4 py-2.5 hover:border-bean hover:text-bean"
          >
            Good first issues
          </a>
          <a
            href={`${GITHUB}/blob/master/CONTRIBUTING.md`}
            className="text-sm rounded-md border border-rule px-4 py-2.5 hover:border-bean hover:text-bean"
          >
            How to contribute
          </a>
          <a
            href="https://discord.gg/M2BuZn6X3"
            className="text-sm rounded-md border border-rule px-4 py-2.5 hover:border-bean hover:text-bean"
          >
            Ask on Discord
          </a>
        </div>
      </section>
    </>
  );
}
