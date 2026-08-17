import type { NextConfig } from 'next';

const config: NextConfig = {
  // Static export: the site is served by nginx from a directory, same as the other
  // baryo.dev sites. Nothing here needs a server — the marketplace reads NuGet, which
  // sends Access-Control-Allow-Origin: *, so the browser can call it directly.
  output: 'export',
  images: { unoptimized: true },
  trailingSlash: true,
};

export default config;
