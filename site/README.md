# barakocms.baryo.dev

The project's landing page and module marketplace. A static Next.js site — nginx serves a directory,
same as the other baryo.dev sites.

```bash
npm install
npm run dev     # http://localhost:3200
npm run build   # static export to out/
```

## The marketplace has no backend

It reads NuGet. Every package carries the `barakocms-module` tag, so one search returns the whole
set — including modules published by other people, with no submission step. Umbraco's marketplace
works the same way off `umbraco-marketplace`.

`lib/nuget.ts` resolves the search endpoint from the service index rather than hardcoding it, because
NuGet moves it and a hardcoded host is how a marketplace quietly goes blank a year later.

NuGet sends `Access-Control-Allow-Origin: *`, so this would also work as a browser fetch. It runs at
build time instead, so the page needs no JavaScript to show its content and is instant.

### When NuGet has nothing

Until the first tagged release, the search returns zero results. Rather than render an empty page —
which reads as a dead project — it falls back to `data/modules.json`, generated from the repository's
`.csproj` files, and labels those entries as not yet published. Regenerate it when packages change:

```bash
python3 scripts/generate-modules-manifest.py
```

## Deploying

```bash
./deploy.sh
```

Builds, rsyncs `out/` to the VM, and reloads nginx. Needs SSH access. Rebuild after a release so the
marketplace picks up new packages and download counts.
