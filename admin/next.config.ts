import type { NextConfig } from "next";

// Set NEXT_BASE_PATH at build time to serve the admin under a sub-path
// (e.g. "/barakocms" behind a shared reverse proxy). Leave it unset to serve
// from the domain root, which is what the published `latest` image does.
const basePath = process.env.NEXT_BASE_PATH || undefined;

const nextConfig: NextConfig = {
  output: "standalone",
  reactCompiler: true,
  // Next 16.1 began blocking cross-origin requests for dev-server resources. The end-to-end suite
  // drives http://127.0.0.1:3100 while the dev server treats localhost as its origin, so every
  // /_next/* chunk was refused: the app never hydrated, clicks did nothing, and 28 tests failed
  // looking like a routing regression. Development only — a production build serves its own assets
  // and ignores this.
  allowedDevOrigins: ["127.0.0.1"],
  ...(basePath ? { basePath, assetPrefix: basePath } : {}),
};

export default nextConfig;
