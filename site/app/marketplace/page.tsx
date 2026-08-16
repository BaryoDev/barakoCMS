import type { Metadata } from 'next';
import { fetchModules, formatDownloads, displayName, DISCOVERY_TAG } from '@/lib/nuget';

export const metadata: Metadata = {
  title: 'Modules',
  description:
    'Optional packages that extend BarakoCMS: accounting, semantic search, feature flags, file storage, social sign-in and more. Anything published to NuGet with the barakocms-module tag appears here.',
};

const GITHUB = 'https://github.com/BaryoDev/barakoCMS';

export default async function Marketplace() {
  const { modules, live } = await fetchModules();
  const official = modules.filter((m) => m.official);
  const community = modules.filter((m) => !m.official);

  return (
    <>
      <section className="border-b border-rule bg-surface">
        <div className="mx-auto max-w-5xl px-6 py-16">
          <h1 className="font-display text-[clamp(32px,5vw,46px)] leading-tight tracking-tight">
            Modules
          </h1>
          <p className="mt-5 text-lg text-ink-2 max-w-[62ch] leading-relaxed">
            Add a capability without growing the core. Each module is a separate package. Install
            it, register it, and it wires in its own endpoints, storage and permissions.
          </p>
          <p className="mt-4 text-[15px] text-muted max-w-[62ch] leading-relaxed">
            This list is NuGet. Anything published with the{' '}
            <code className="font-mono text-[13px] bg-ground border border-rule rounded px-1.5 py-0.5">
              {DISCOVERY_TAG}
            </code>{' '}
            tag shows up here, including modules built by other people. There is no submission queue
            and nobody approves anything.
          </p>
        </div>
      </section>

      <div className="mx-auto max-w-5xl px-6 py-14">
        {!live && (
          <p className="mb-10 rounded-lg border border-rule bg-surface px-5 py-4 text-[15px] text-ink-2 leading-relaxed">
            <strong className="font-medium">Awaiting the first tagged release.</strong> These are the
            modules in the repository. They appear on NuGet with download counts once a release
            publishes them, and this page picks that up on its own.
          </p>
        )}

        <Group
          title="Official"
          blurb="Published by the BarakoCMS project."
          modules={official}
        />

        {community.length > 0 ? (
          <Group
            title="Community"
            blurb="Published by other people. Listed as found, with no endorsement implied. Read the source before you trust it with your data."
            modules={community}
          />
        ) : (
          <section className="mt-16 border-t border-rule pt-10">
            <h2 className="font-display text-2xl tracking-tight">Community</h2>
            <p className="mt-3 text-[15px] text-ink-2 max-w-[62ch] leading-relaxed">
              Nothing here yet. Publish a package to NuGet with the{' '}
              <code className="font-mono text-[13px] bg-surface border border-rule rounded px-1.5 py-0.5">
                {DISCOVERY_TAG}
              </code>{' '}
              tag and it will be listed automatically. You keep it in your own repository and
              control its releases.
            </p>
            <a
              href={`${GITHUB}/blob/master/CONTRIBUTING.md#writing-a-module`}
              className="mt-6 inline-block text-sm rounded-md border border-rule px-4 py-2.5 hover:border-bean hover:text-bean"
            >
              How to write a module
            </a>
          </section>
        )}
      </div>
    </>
  );
}

function Group({
  title,
  blurb,
  modules,
}: {
  title: string;
  blurb: string;
  modules: Awaited<ReturnType<typeof fetchModules>>['modules'];
}) {
  if (modules.length === 0) return null;
  return (
    <section className="mt-16 first:mt-0">
      <div className="flex items-baseline gap-3 border-b border-rule pb-3">
        <h2 className="font-display text-2xl tracking-tight">{title}</h2>
        <span className="font-mono text-xs text-muted tabular-nums">{modules.length}</span>
      </div>
      <p className="mt-3 text-[15px] text-muted max-w-[62ch] leading-relaxed">{blurb}</p>

      <ul className="mt-8 grid gap-px bg-rule border border-rule rounded-lg overflow-hidden sm:grid-cols-2">
        {modules.map((m) => (
          <li key={m.id} className="bg-ground p-5 flex flex-col transition-colors hover:bg-surface">
            <div className="flex items-start gap-3">
              {m.iconUrl ? (
                // eslint-disable-next-line @next/next/no-img-element
                <img
                  src={m.iconUrl}
                  alt=""
                  width={32}
                  height={32}
                  loading="lazy"
                  className="mt-0.5 size-8 shrink-0 rounded"
                />
              ) : (
                <span className="mt-0.5 size-8 shrink-0 rounded bg-raised" aria-hidden="true" />
              )}
              <h3 className="font-medium leading-snug flex-1">{displayName(m.id)}</h3>
              <span className="font-mono text-[11px] text-muted tabular-nums whitespace-nowrap pt-1">
                {m.version}
              </span>
            </div>

            <p className="mt-2 text-[14px] text-ink-2 leading-relaxed flex-1">{m.description}</p>

            <div className="mt-4 flex items-center gap-4 text-[12px] text-muted">
              <code className="font-mono text-[11.5px] truncate">{m.id}</code>
              <span className="flex-1" />
              {m.pending ? (
                <span className="whitespace-nowrap">not yet published</span>
              ) : (
                <span className="whitespace-nowrap tabular-nums">
                  {formatDownloads(m.totalDownloads)} downloads
                </span>
              )}
            </div>

            {!m.pending && (
              <a
                href={`https://www.nuget.org/packages/${m.id}`}
                className="mt-3 text-[13px] text-bean hover:underline"
              >
                View on NuGet
              </a>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}
