import fallback from '@/data/modules.json';

/**
 * The marketplace is a view over NuGet, not a registry of its own.
 *
 * Every barakoCMS package carries the `barakocms-module` tag, so one search returns the whole set —
 * including modules published by other people, with no submission step. Umbraco's marketplace works
 * the same way off `umbraco-marketplace`.
 */
export const DISCOVERY_TAG = 'barakocms-module';

const SERVICE_INDEX = 'https://api.nuget.org/v3/index.json';

export type Module = {
  id: string;
  version: string;
  description: string;
  iconUrl?: string;
  totalDownloads?: number;
  authors?: string[];
  projectUrl?: string;
  tags?: string[];
  /** Published by the project itself, rather than by the wider community. */
  official: boolean;
  /** True when this came from the bundled manifest because NuGet had nothing to show. */
  pending?: boolean;
};

/** Owners we publish under. Checked against package ownership, not the authors field. */
const OFFICIAL_PREFIX = 'BarakoCMS';

/**
 * Icons are rendered from package metadata, and anyone can publish a package carrying our discovery
 * tag — that openness is the point of the marketplace, and it means `iconUrl` is attacker-controlled.
 * An arbitrary URL in an `<img src>` makes every visitor's browser call a host of the publisher's
 * choosing, which leaks visitor IP and user agent and works as a tracking pixel.
 *
 * NuGet re-hosts embedded icons on its own CDN, so every icon worth showing is already there:
 * checked across our packages and forty third-party ones, all of them serve from api.nuget.org.
 * Anything else gets no icon and falls back to a placeholder, which costs nothing.
 */
const ICON_HOSTS = new Set(['api.nuget.org']);

export function safeIconUrl(raw: string | undefined): string | undefined {
  if (!raw) return undefined;
  try {
    const url = new URL(raw);
    if (url.protocol !== 'https:') return undefined;
    return ICON_HOSTS.has(url.host) ? url.toString() : undefined;
  } catch {
    return undefined;
  }
}

/**
 * Resolve the search endpoint from the service index rather than hardcoding it — NuGet moves it,
 * and a hardcoded host is how a marketplace quietly goes blank a year later.
 */
async function searchEndpoint(): Promise<string> {
  // Not `no-store`: that marks the fetch dynamic, which `output: 'export'` refuses, so the whole
  // call throws and the page silently falls back to the manifest. deploy.sh clears .next before
  // building, which is what actually keeps this fresh.
  const res = await fetch(SERVICE_INDEX);
  if (!res.ok) throw new Error(`service index: ${res.status}`);
  const body = (await res.json()) as { resources: { '@id': string; '@type': string }[] };
  const svc = body.resources.find((r) => r['@type'].startsWith('SearchQueryService'));
  if (!svc) throw new Error('no SearchQueryService in the service index');
  return svc['@id'];
}

type NuGetHit = {
  id: string;
  version: string;
  description?: string;
  iconUrl?: string;
  totalDownloads?: number;
  authors?: string[];
  projectUrl?: string;
  tags?: string[];
};

/**
 * Everything published under the discovery tag. Falls back to the bundled manifest rather than
 * rendering an empty page: a marketplace that intermittently shows nothing reads as a dead project,
 * and until the first tagged release lands there is genuinely nothing on NuGet to show.
 */
export async function fetchModules(): Promise<{ modules: Module[]; live: boolean }> {
  try {
    const endpoint = await searchEndpoint();
    const url = `${endpoint}?q=${encodeURIComponent(`tags:${DISCOVERY_TAG}`)}&take=100&prerelease=false`;
    const res = await fetch(url);
    if (!res.ok) throw new Error(`search: ${res.status}`);
    const body = (await res.json()) as { data: NuGetHit[] };

    if (body.data?.length) {
      const modules = body.data
        .map((p) => ({
          id: p.id,
          version: p.version,
          description: p.description ?? '',
          iconUrl: safeIconUrl(p.iconUrl),
          totalDownloads: p.totalDownloads,
          authors: p.authors,
          projectUrl: p.projectUrl,
          tags: p.tags,
          official: p.id === OFFICIAL_PREFIX || p.id.startsWith(`${OFFICIAL_PREFIX}.`),
        }))
        // Downloads first: the one signal that is honest and that nobody here controls.
        .sort((a, b) => (b.totalDownloads ?? 0) - (a.totalDownloads ?? 0));
      return { modules, live: true };
    }
    console.warn(
      `[marketplace] NuGet returned no packages for tags:${DISCOVERY_TAG}; using the bundled manifest.`,
    );
  } catch (err) {
    // Falling back is intentional and must not fail the build — NuGet being unreachable should not
    // stop the site deploying. But it has to be visible: this catch previously hid a bug where
    // every build fell back and nobody could tell from the output.
    console.warn(
      `[marketplace] NuGet lookup failed, using the bundled manifest: ${
        err instanceof Error ? err.message : String(err)
      }`,
    );
  }

  return {
    modules: (fallback.modules as Module[]).map((m) => ({ ...m, pending: true })),
    live: false,
  };
}

export function formatDownloads(n?: number): string {
  if (n === undefined) return '—';
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}k`;
  return String(n);
}

/** "BarakoCMS.Analytics.Umami" -> "Analytics · Umami"; the core package keeps its name. */
export function displayName(id: string): string {
  if (id === 'BarakoCMS') return 'BarakoCMS';
  return id.replace(/^BarakoCMS\./, '').split('.').join(' · ');
}
