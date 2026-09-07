- **Two places pointed at the console as though this repository still owned it.** The issue
  template's console redirect went to `barakoBrew/issues/new/choose`, which offers no chooser
  because that repository has no templates yet (barakoBrew#29); it now points at the plain form
  and says so. The README and `docs/deploy-in-production.md` said `barako-admin` is "still
  published" or "built and released by" barakoBrew's own workflow; nothing has published it since
  the split (barakoBrew#23), so the wording now says where the image comes from without claiming a
  pipeline that does not exist, and names the amd64-only `3.21.0` tag as the last one built, with no
  `4.0` tag coming from here. `quickstart/.env.example` and `quickstart/docker-compose.yml` now say
  why `ALLOWED_ORIGINS` defaults to port 3000 when this repository's quickstart starts no console on
  it (#632, #633).
