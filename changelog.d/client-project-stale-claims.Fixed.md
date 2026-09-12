- **`delivering-a-client-project` told readers a custom role could not reach four admin surfaces.**
  It said everything outside the migration table was still gated on a seeded role name, so a custom
  role could not create a content type, toggle public delivery, change field sensitivity or export a
  Portability bundle "whatever capabilities you put on it". Since #443 closed, no core or module
  endpoint gates on a role name: all four take a capability, and granting it is what opens them. The
  page now says which capability does what, and names `manage_roles` as the grant that actually
  keeps a deployment yours. The same section listed seven shipped features as missing: SEO fields
  (#111), URL redirects (#112), blueprints (#109), webhook deliveries (#95), the event stream (#96)
  and image variants (#100), the last two with the detail that `Delivery:Events:Enabled` defaults to
  false. Only the media library screen is still absent, and uploading has taken a capability rather
  than a role name for some time.
