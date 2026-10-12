- **Every tenant's feed and sitemap linked to the one `Feeds:SiteUrl` of the deployment.** They now
  link to the `Url` of the tenant's published `site` entry, through one resolver, and fall back to
  `Feeds:SiteUrl` and `App:BaseUrl` as before when a tenant sets none. A new `{{links.public}}`
  template placeholder gives an entry's address on that site, and is empty unless delivery serves
  the entry at a slug (#923).

  **Upgrading:** a published site entry's `Url` now takes precedence over `Feeds:SiteUrl`. If you
  set both and they differ, the feed and sitemap links move to the site entry's address. Check that
  they agree before upgrading.
