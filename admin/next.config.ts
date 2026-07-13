import type { NextConfig } from "next";

// Set NEXT_BASE_PATH at build time to serve the admin under a sub-path
// (e.g. "/barakocms" behind a shared reverse proxy). Leave it unset to serve
// from the domain root, which is what the published `latest` image does.
const basePath = process.env.NEXT_BASE_PATH || undefined;

const nextConfig: NextConfig = {
  output: "standalone",
  reactCompiler: true,
  ...(basePath ? { basePath, assetPrefix: basePath } : {}),
};

export default nextConfig;
