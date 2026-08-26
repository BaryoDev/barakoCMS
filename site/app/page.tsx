import Link from 'next/link';
import { Bean } from './bean';
import { Ledger } from './ledger';
import { fetchModules } from '@/lib/nuget';
import { contributors, describe } from '@/lib/contributors';

const GITHUB = 'https://github.com/BaryoDev/barakoCMS';
const BARYOVM = 'https://github.com/BaryoDev/BaryoVM';

export default async function Home() {
  const { modules } = await fetchModules();
  const moduleCount = modules.filter((m) => m.id !== 'BarakoCMS').length;
  const people = contributors();

  return (
    <>
      {/*
        The seam. Editorial argument on the left in crema, the system it runs on to the right in
        roast, meeting at a hard edge. On a phone there is no room for two columns side by side, so
        the seam becomes horizontal and the ledger sits underneath the claim it supports.
      */}
      <section className="grid border-b border-rule lg:grid-cols-[1.05fr_1fr]">
        <div className="flex flex-col justify-center px-6 py-20 sm:px-10 md:py-28 lg:pl-[max(1.5rem,calc((100vw-72rem)/2))]">
          <Bean size={52} className="bean-in mb-8" />
          <h1 className="max-w-[16ch] text-balance font-display text-[clamp(40px,6.4vw,68px)] font-semibold leading-[0.98] tracking-[-0.03em]">
            The backend your next project already needs
          </h1>
          <p className="mt-7 max-w-[54ch] text-[17px] leading-relaxed text-ink-2">
            Content, users, roles, permissions, workflow, audit history and multi-tenancy. Every
            project needs them. BarakoCMS is an open-source .NET 8 base that brings them along on day
            one, so your time goes to what the customer asked for.
          </p>

          <div className="mt-9 flex flex-wrap items-center gap-3">
            <code className="rounded-sm border border-rule bg-surface px-4 py-2.5 font-mono text-[13px]">
              dotnet add package BarakoCMS
            </code>
            <Link
              href="/marketplace/"
              className="rounded-sm bg-bean px-4 py-2.5 text-sm font-medium text-white transition-opacity hover:opacity-90"
            >
              Browse {moduleCount} modules
            </Link>
          </div>
        </div>

        <div className="on-roast flex flex-col justify-center bg-roast px-6 py-16 sm:px-10 md:py-20 lg:pr-[max(1.5rem,calc((100vw-72rem)/2))]">
          <Ledger />
        </div>
      </section>

      <section className="mx-auto max-w-5xl px-6 py-24">
        <p className="eyebrow text-muted">Not only for content sites</p>
        <h2 className="mt-4 max-w-[20ch] font-display text-[clamp(28px,3.6vw,40px)] font-semibold leading-[1.06] tracking-[-0.025em]">
          A blog and a treasury have the same foundations
        </h2>
        <div className="mt-7 grid gap-x-12 gap-y-5 md:grid-cols-2">
          <p className="leading-relaxed text-ink-2">
            A blog, an events platform, a membership system and a treasury are different products
            with the same foundations underneath. Each one needs somewhere to define records, decide
            who may read which field, keep an audit trail, and serve it over an API.
          </p>
          <p className="leading-relaxed text-ink-2">
            BarakoCMS is that layer. Your frontend stays yours, in whatever framework you like, and
            so do your domain rules. What you inherit is the part that comes out the same every
            time.
          </p>
        </div>

        {/* The strongest claim on the page, so it gets the accent edge rather than a box. */}
        <div className="mt-10 border-l-2 border-bean bg-surface p-6">
          <p className="text-[15px] leading-relaxed">
            <strong className="font-semibold">The accounting module is the proof.</strong> A
            double-entry ledger with accounts, balanced journal entries, immutable postings and
            statements is not a content feature by any reading. It turned out to be a content type
            with rules attached, and it needed no changes to the core.
          </p>
        </div>
      </section>

      {/* Roast block: what it is and what it gives you, in the material that means "system". */}
      <section className="on-roast bg-roast text-on-roast">
        <div className="mx-auto max-w-5xl px-6 py-24">
          <p className="eyebrow text-on-roast-3">Three lines to start</p>
          <h2 className="mt-4 max-w-[22ch] font-display text-[clamp(28px,3.6vw,40px)] font-semibold leading-[1.06] tracking-[-0.025em]">
            A service registration, not a scaffold
          </h2>
          <p className="mt-5 max-w-[58ch] leading-relaxed text-on-roast-2">
            No scaffolding step and no generated project to maintain. It is a service registration in
            an ASP.NET app you already have.
          </p>

          <pre className="mt-8 overflow-x-auto rounded-sm border border-roast-rule bg-roast-2 p-6">
            <code className="font-mono text-[13.5px] leading-relaxed text-on-roast">{`builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
app.Run();`}</code>
          </pre>
          <p className="mt-5 text-sm text-on-roast-3">
            Needs PostgreSQL. Content, authentication, roles, workflow and the delivery API come with
            it.
          </p>

          <hr className="my-16 border-roast-rule" />

          <p className="eyebrow text-on-roast-3">What you inherit</p>
          <div className="mt-9 grid gap-px overflow-hidden rounded-sm border border-roast-rule bg-roast-rule sm:grid-cols-2">
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
              <div key={f.h} className="bg-roast p-6">
                <h3 className="font-display text-[17px] font-semibold tracking-[-0.015em]">
                  {f.h}
                </h3>
                <p className="mt-2 text-[14.5px] leading-relaxed text-on-roast-2">{f.p}</p>
              </div>
            ))}
          </div>
        </div>
      </section>

      <section className="mx-auto max-w-5xl px-6 py-24">
        <p className="eyebrow text-muted">If you build for other people</p>
        <h2 className="mt-4 max-w-[22ch] font-display text-[clamp(28px,3.6vw,40px)] font-semibold leading-[1.06] tracking-[-0.025em]">
          The weeks no client ever asked you to spend
        </h2>
        <div className="mt-7 grid gap-x-12 gap-y-5 md:grid-cols-2">
          <p className="leading-relaxed text-ink-2">
            An agency starts most projects the same way. A database, an admin, logins, roles, rules
            about who may edit what, and an API for the frontend. It is weeks of work that no client
            ever asked for and none of them can tell apart.
          </p>
          <p className="leading-relaxed text-ink-2">
            Run one deployment and give each customer a tenant. Model what they asked for as content
            types, without a rebuild. Point any frontend at the delivery API, and hand the whole
            thing over as an export when the engagement ends.
          </p>
        </div>

        <ul className="mt-10 grid gap-x-10 gap-y-7 sm:grid-cols-2">
          {[
            ['One deployment, many customers', 'Tenants share a database and stay scoped apart.'],
            ['A new client is an API call', 'POST /api/tenants, then model their content.'],
            ['Their site, their content', 'Editors work in the admin, so you are not the bottleneck.'],
            ['Nobody is locked in', 'Export content and schema as JSON at any time.'],
          ].map(([h, p]) => (
            <li key={h} className="border-t border-rule pt-4">
              <span className="font-medium">{h}</span>
              <span className="mt-1 block text-[15px] leading-relaxed text-ink-2">{p}</span>
            </li>
          ))}
        </ul>

        <p className="mt-10 max-w-[64ch] text-[15px] leading-relaxed text-muted">
          Blueprints, form submissions, SEO fields and redirects are the pieces still missing from
          that story. They are{' '}
          <a href={`${GITHUB}/issues/108`} className="text-bean hover:underline">
            tracked in the open
          </a>
          .
        </p>
      </section>

      <section className="border-t border-rule bg-surface">
        <div className="mx-auto max-w-5xl px-6 py-24">
          <p className="eyebrow text-muted">What tends to sit behind a licence</p>
          <h2 className="mt-4 max-w-[24ch] font-display text-[clamp(28px,3.6vw,40px)] font-semibold leading-[1.06] tracking-[-0.025em]">
            The comparison, made fairly
          </h2>
          <p className="mt-6 max-w-[64ch] leading-relaxed text-ink-2">
            Umbraco is the closest reference point in .NET and a good one. The CMS itself is MIT and
            free. What an agency runs into is that several pieces a client project needs are separate
            commercial products.
          </p>
          <p className="mt-4 max-w-[64ch] text-[15px] leading-relaxed text-muted">
            This compares <strong className="font-medium text-ink-2">self-hosted</strong> Umbraco,
            which is the like-for-like case. Umbraco Cloud is a paid platform that bundles Forms and
            Deploy into its plans, so those two rows do not apply if you host there.
          </p>

          <div className="mt-9 overflow-x-auto">
            <table className="w-full border-collapse text-[15px]">
              <thead>
                <tr className="border-b border-rule text-left">
                  {['Capability', 'Umbraco', 'BarakoCMS'].map((h) => (
                    <th key={h} className="eyebrow py-3 pr-6 font-normal text-muted">
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
                    <td className="py-3.5 pr-6">{cap}</td>
                    <td className="py-3.5 pr-6 text-ink-2">{umb}</td>
                    {/* Green is the unroasted bean, used only where it marks something included. */}
                    <td className="py-3.5 font-medium text-good">{ours}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <p className="mt-8 max-w-[64ch] text-[15px] leading-relaxed text-muted">
            To be fair about it, those licences are how Umbraco funds a mature product with real
            support behind it, and their add-ons are further along than ours. BarakoCMS is younger
            and comes with no support contract. Licensing details come from Umbraco&rsquo;s own
            documentation and can change, so check theirs before you decide.
          </p>
        </div>
      </section>

      <section className="on-roast bg-roast text-on-roast">
        <div className="mx-auto max-w-5xl px-6 py-24">
          <p className="eyebrow text-on-roast-3">And a way to run it</p>
          <h2 className="mt-4 max-w-[22ch] font-display text-[clamp(28px,3.6vw,40px)] font-semibold leading-[1.06] tracking-[-0.025em]">
            Deploys to a VM you already own
          </h2>
          <div className="mt-7 grid gap-x-12 gap-y-5 md:grid-cols-2">
            <p className="leading-relaxed text-on-roast-2">
              A base is only useful if you can keep it running.{' '}
              <strong className="font-semibold text-on-roast">BaryoVM</strong> is a companion CLI
              that deploys a stack to a VM you already own, over SSH, with no agent to install. It
              takes a backup before it updates, checks the site is healthy afterwards, and puts the
              previous images back if it is not.
            </p>
            <p className="leading-relaxed text-on-roast-2">
              Optional, and separate on purpose. BarakoCMS is an ordinary .NET app and runs anywhere
              .NET runs.
            </p>
          </div>
          <a
            href={BARYOVM}
            className="mt-8 inline-block rounded-sm border border-roast-rule px-4 py-2.5 text-sm transition-colors hover:border-bean-soft hover:text-bean-soft"
          >
            BaryoVM on GitHub
          </a>
        </div>
      </section>

      <section className="mx-auto max-w-5xl px-6 py-24">
        <p className="eyebrow text-muted">Built in the open</p>
        <h2 className="mt-4 max-w-[22ch] font-display text-[clamp(28px,3.6vw,40px)] font-semibold leading-[1.06] tracking-[-0.025em]">
          There is no submission queue
        </h2>
        <div className="mt-7 grid gap-x-12 gap-y-5 md:grid-cols-2">
          <p className="leading-relaxed text-ink-2">
            Issues labelled <em>good first issue</em> carry enough context to start without asking,
            and not all of them are C#. Module icons, documentation and examples all count. A README
            is the package page on NuGet, so a clearer one is a real improvement.
          </p>
          <p className="leading-relaxed text-ink-2">
            Building a module is the other way in. Publish it under your own name with the{' '}
            <code className="rounded border border-rule bg-surface px-1.5 py-0.5 font-mono text-[13px]">
              barakocms-module
            </code>{' '}
            tag and it appears in the marketplace on its own.
          </p>
        </div>

        {people.length > 0 && (
          <div className="mt-10 border-t border-rule pt-8">
            <h3 className="font-display text-[19px] font-semibold tracking-[-0.015em]">
              People who have made this better
            </h3>
            <p className="mt-2 max-w-[64ch] text-[15px] leading-relaxed text-ink-2">
              Not only code. A bug report that stops the wrong thing being built counts as much here
              as a pull request, and so far it has counted for more.
            </p>
            <ul className="mt-7 flex flex-wrap gap-x-8 gap-y-5">
              {people.map((p) => (
                <li key={p.login}>
                  <a href={p.profile} rel="noopener noreferrer" className="group flex items-center gap-3">
                    {/* eslint-disable-next-line @next/next/no-img-element */}
                    <img
                      src={`https://avatars.githubusercontent.com/${encodeURIComponent(p.login)}?s=72`}
                      alt=""
                      width={36}
                      height={36}
                      loading="lazy"
                      referrerPolicy="no-referrer"
                      className="size-9 rounded-full"
                    />
                    <span className="text-[15px]">
                      <span className="font-medium group-hover:text-bean">{p.name}</span>
                      <span className="block text-[13px] text-muted">
                        {describe(p.contributions)}
                      </span>
                    </span>
                  </a>
                </li>
              ))}
            </ul>
          </div>
        )}

        <div className="mt-10 flex flex-wrap gap-3">
          {[
            ['Good first issues', `${GITHUB}/issues?q=is%3Aopen+label%3A%22good+first+issue%22`],
            ['How to contribute', `${GITHUB}/blob/master/CONTRIBUTING.md`],
            ['Ask on Discord', 'https://discord.gg/7GYKzDx7Z2'],
          ].map(([label, href]) => (
            <a
              key={label}
              href={href}
              className="rounded-sm border border-rule px-4 py-2.5 text-sm transition-colors hover:border-bean hover:text-bean"
            >
              {label}
            </a>
          ))}
        </div>
      </section>
    </>
  );
}
