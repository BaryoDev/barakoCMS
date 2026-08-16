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
      <section className="border-b border-rule bg-surface">
        <div className="mx-auto max-w-5xl px-6 py-20 md:py-28">
          <Bean size={56} className="bean-in mb-8" />
          <h1 className="font-display text-[clamp(38px,6vw,62px)] leading-[1.04] tracking-tight text-balance max-w-[17ch]">
            The backend your next project already needs
          </h1>
          <p className="mt-6 text-lg text-ink-2 max-w-[58ch] leading-relaxed">
            Content, users, roles, permissions, workflow, audit history and multi-tenancy. Every
            project needs them. BarakoCMS is an open-source .NET 8 base that brings them along on day
            one, so your time goes to what the customer asked for.
          </p>

          <div className="mt-9 flex flex-wrap items-center gap-3">
            <code className="font-mono text-sm bg-ground border border-rule rounded-md px-4 py-2.5">
              dotnet add package BarakoCMS
            </code>
            <Link
              href="/marketplace/"
              className="text-sm font-medium rounded-md bg-bean text-ground px-4 py-2.5 transition-opacity hover:opacity-90"
            >
              Browse {moduleCount} modules
            </Link>
          </div>
        </div>
      </section>

      <section className="mx-auto max-w-5xl px-6 py-20">
        <h2 className="font-display text-3xl tracking-tight">Not only for content sites</h2>
        <p className="mt-4 text-ink-2 max-w-[64ch] leading-relaxed">
          A blog, an events platform, a membership system and a treasury are different products with
          the same foundations underneath. Each one needs somewhere to define records, decide who may
          read which field, keep an audit trail, and serve it over an API.
        </p>
        <p className="mt-4 text-ink-2 max-w-[64ch] leading-relaxed">
          BarakoCMS is that layer. Your frontend stays yours, in whatever framework you like, and so
          do your domain rules. What you inherit is the part that comes out the same every time.
        </p>

        <div className="mt-9 rounded-lg border border-rule bg-surface p-6">
          <p className="text-[15px] leading-relaxed">
            <strong className="font-medium">The accounting module is the proof.</strong> A
            double-entry ledger with accounts, balanced journal entries, immutable postings and
            statements is not a content feature by any reading. It turned out to be a content type
            with rules attached, and it needed no changes to the core.
          </p>
        </div>
      </section>

      <section className="border-t border-rule bg-surface">
        <div className="mx-auto max-w-5xl px-6 py-20">
          <h2 className="font-display text-3xl tracking-tight">Especially if you build for other people</h2>
          <p className="mt-4 text-ink-2 max-w-[64ch] leading-relaxed">
            An agency starts most projects the same way. A database, an admin, logins, roles, rules
            about who may edit what, and an API for the frontend. It is weeks of work that no client
            ever asked for and none of them can tell apart.
          </p>
          <p className="mt-4 text-ink-2 max-w-[64ch] leading-relaxed">
            Run one deployment and give each customer a tenant. Model what they asked for as content
            types, without a rebuild. Point any frontend at the delivery API, and hand the whole
            thing over as an export when the engagement ends.
          </p>

          <ul className="mt-9 grid gap-x-10 gap-y-5 sm:grid-cols-2 text-[15px]">
            {[
              ['One deployment, many customers', 'Tenants share a database and stay scoped apart.'],
              ['A new client is an API call', 'POST /api/tenants, then model their content.'],
              ['Their site, their content', 'Editors work in the admin, so you are not the bottleneck.'],
              ['Nobody is locked in', 'Export content and schema as JSON at any time.'],
            ].map(([h, p]) => (
              <li key={h}>
                <span className="font-medium">{h}</span>
                <span className="block text-ink-2 mt-0.5 leading-relaxed">{p}</span>
              </li>
            ))}
          </ul>

          <p className="mt-9 text-[15px] text-muted max-w-[64ch] leading-relaxed">
            Blueprints, form submissions, SEO fields and redirects are the pieces still missing from
            that story. They are{' '}
            <a href={`${GITHUB}/issues/108`} className="text-bean hover:underline">
              tracked in the open
            </a>
            .
          </p>
        </div>
      </section>

      <section className="mx-auto max-w-5xl px-6 py-20">
        <h2 className="font-display text-3xl tracking-tight">What tends to sit behind a licence</h2>
        <p className="mt-4 text-ink-2 max-w-[64ch] leading-relaxed">
          Umbraco is the closest reference point in .NET and a good one. The CMS itself is MIT and
          free. What an agency runs into is that several pieces a client project needs are separate
          commercial products.
        </p>
        <p className="mt-4 text-[15px] text-muted max-w-[64ch] leading-relaxed">
          This compares <strong className="font-medium text-ink-2">self-hosted</strong> Umbraco,
          which is the like-for-like case. Umbraco Cloud is a paid platform that bundles Forms and
          Deploy into its plans, so those two rows do not apply if you host there.
        </p>

        <div className="mt-8 overflow-x-auto">
          <table className="w-full text-[15px] border-collapse">
            <thead>
              <tr className="border-b border-rule text-left">
                {['Capability', 'Umbraco', 'BarakoCMS'].map((h) => (
                  <th
                    key={h}
                    className="py-2.5 pr-6 font-mono text-[10.5px] tracking-[.11em] uppercase text-muted font-normal"
                  >
                    {h}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {[
                ['The CMS itself', 'Free, MIT', 'Free, MPL-2.0'],
                ['Editorial workflow', 'Umbraco Workflow, licensed', 'In the core'],
                ['Environment and content transfer', 'Umbraco Deploy, licensed', 'Portability module'],
                ['Analytics', 'Umbraco Engage, licensed', 'Analytics module'],
                ['Forms', 'Umbraco Forms, licensed', 'Planned, and it will be free'],
              ].map(([cap, umb, ours]) => (
                <tr key={cap} className="border-b border-rule align-top">
                  <td className="py-3 pr-6">{cap}</td>
                  <td className="py-3 pr-6 text-ink-2">{umb}</td>
                  <td className="py-3 text-ink-2">{ours}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        <p className="mt-7 text-[15px] text-muted max-w-[64ch] leading-relaxed">
          To be fair about it, those licences are how Umbraco funds a mature product with real
          support behind it, and their add-ons are further along than ours. BarakoCMS is younger and
          comes with no support contract. Licensing details come from Umbraco&rsquo;s own
          documentation and can change, so check theirs before you decide.
        </p>
      </section>

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
              p: 'Nothing is served anonymously until a content type opts in. Private is the default, so forgetting to choose is safe.',
            },
            {
              h: 'Real history',
              p: 'Event sourced on Marten and PostgreSQL, so a record has a past rather than one row that keeps being overwritten.',
            },
            {
              h: 'Multi-tenant',
              p: 'One deployment, many customers, one database, scoped per tenant.',
            },
            {
              h: 'Modules, not a monolith',
              p: 'Accounting, semantic search, feature flags, file storage, social sign-in. Take what you need and the core stays small.',
            },
          ].map((f) => (
            <div key={f.h}>
              <h3 className="font-medium">{f.h}</h3>
              <p className="mt-1.5 text-[15px] text-ink-2 leading-relaxed">{f.p}</p>
            </div>
          ))}
        </div>
      </section>

      <section className="border-t border-rule bg-surface">
        <div className="mx-auto max-w-5xl px-6 py-20">
          <h2 className="font-display text-3xl tracking-tight">And a way to run it</h2>
          <p className="mt-4 text-ink-2 max-w-[64ch] leading-relaxed">
            A base is only useful if you can keep it running.{' '}
            <strong className="font-medium">BaryoVM</strong> is a companion CLI that deploys a stack
            to a VM you already own, over SSH, with no agent to install. It takes a backup before it
            updates, checks the site is healthy afterwards, and puts the previous images back if it
            is not.
          </p>
          <p className="mt-4 text-ink-2 max-w-[64ch] leading-relaxed">
            Optional, and separate on purpose. BarakoCMS is an ordinary .NET app and runs anywhere
            .NET runs.
          </p>
          <a
            href={BARYOVM}
            className="mt-7 inline-block text-sm rounded-md border border-rule px-4 py-2.5 transition-colors hover:border-bean hover:text-bean"
          >
            BaryoVM on GitHub
          </a>
        </div>
      </section>

      <section className="mx-auto max-w-5xl px-6 py-20">
        <h2 className="font-display text-3xl tracking-tight">Built in the open</h2>
        <p className="mt-4 text-ink-2 max-w-[64ch] leading-relaxed">
          Issues labelled <em>good first issue</em> carry enough context to start without asking, and
          not all of them are C#. Module icons, documentation and examples all count. A README is the
          package page on NuGet, so a clearer one is a real improvement.
        </p>
        <p className="mt-4 text-ink-2 max-w-[64ch] leading-relaxed">
          Building a module is the other way in. Publish it under your own name with the{' '}
          <code className="font-mono text-[13px] bg-surface border border-rule rounded px-1.5 py-0.5">
            barakocms-module
          </code>{' '}
          tag and it appears in the marketplace on its own. There is no submission queue.
        </p>
        <div className="mt-7 flex flex-wrap gap-3">
          {[
            ['Good first issues', `${GITHUB}/issues?q=is%3Aopen+label%3A%22good+first+issue%22`],
            ['How to contribute', `${GITHUB}/blob/master/CONTRIBUTING.md`],
            ['Ask on Discord', 'https://discord.gg/M2BuZn6X3'],
          ].map(([label, href]) => (
            <a
              key={label}
              href={href}
              className="text-sm rounded-md border border-rule px-4 py-2.5 transition-colors hover:border-bean hover:text-bean"
            >
              {label}
            </a>
          ))}
        </div>
      </section>
    </>
  );
}
