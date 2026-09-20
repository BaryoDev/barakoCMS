- **A deployment that never set `CORS:AllowedOrigins` no longer accepts credentialed requests from
  localhost.** With no origins configured the CORS policy fell back to `http://localhost:3000`,
  `http://localhost:3001` and `https://localhost:7049` with `AllowCredentials()`, and it did that in
  every environment, not only Development. This API puts the refresh token in a cookie, so a page
  served on one of those three ports could drive any deployment that had forgotten the setting.
  Outside Development, no configured origins now means no cross-origin access at all: a deployment
  with no browser client needs none, and one with a client gets a CORS error naming the setting
  rather than a hole nobody looks for. Development is unchanged, so `dotnet run` and the quickstart
  behave exactly as before. If your deployment relied on the fallback, set `CORS:AllowedOrigins`
  (the `CORS__AllowedOrigins` environment variable) to the origins your console and site are served
  from. Two environment checks beside it were case-sensitive, so `ASPNETCORE_ENVIRONMENT=development`
  took Development's connection string but production's schema policy, no Swagger and HSTS; both now
  compare the way the rest of the code does.
