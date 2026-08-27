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
rm -rf .next out && npm run build
COPYFILE_DISABLE=1 baryovm stack release barakocms-site
```

BaryoVM syncs `out/` and does not build it, so the build has to happen first. `rm -rf` rather than a
plain rebuild because the sync uses `--delete`: a file removed from the source is removed from the
webroot, and a stale artifact left in `out/` would be published as though it were current.

`COPYFILE_DISABLE=1` on macOS, or AppleDouble `._` files ride along in the sync.

The release verifies itself afterwards and fails if barakocms.com does not answer, which needs
BaryoVM 0.2.0 or later. `deploy.sh` is the older raw-ssh path and is kept only for reference.

Rebuild after a package release so the marketplace picks up new packages and download counts.

## nginx

`nginx-barakocms.com.conf` is the only server config for this site and is the source of truth. It
serves `barakocms.com` and redirects `www.barakocms.com` and the older `barakocms.baryo.dev` to it,
so all three hostnames are defined in one file. Two files defining the same `server_name` would make
nginx pick whichever loaded first, which is how a redirect quietly stops redirecting.
