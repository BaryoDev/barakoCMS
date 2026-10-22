# Alba pilot (#1024)

**Decision: Alba dropped.** The owner accepted the recommendation for 5.0, and the pilot commit was
reverted, so the package is gone and `ApiContractVersionHeaderTests` is back on
`IntegrationTestFixture`. New HTTP tests stay on that fixture with FluentAssertions. The comparison
below is kept as the record of why; its snippets show the pilot as it was.

## What was done

- `Alba` 8.5.3 (Apache-2.0, published 2026-07-11) pinned in `Directory.Packages.props` and
  referenced by `BarakoCMS.Tests` only. `BarakoCMS.Testing.csproj` and its lock file are unchanged,
  so the published test host does not depend on it.
- `IntegrationTestFixture.ApplyTestHostConfiguration(IWebHostBuilder)` applies the fixture's own
  `ConfigureWebHost` to a builder it does not own. Nothing else in the fixture changed, so the other
  classes run as before.
- `ApiContractVersionHeaderTests` runs as two `host.Scenario(...)` calls on
  `AlbaHost.For<Program>(factory.ApplyTestHostConfiguration)`: the same settings, the same test
  doubles, the same Postgres container.

## Authentication: `CreateToken`, not `JwtSecurityStub`

The pilot class makes no authenticated request, so neither was needed. For a class that does, the
choice is `IntegrationTestFixture.CreateToken` sent as an `Authorization` header
(`s.WithRequestHeader(...)`). `JwtSecurityStub` replaces bearer validation with a stub, which skips
exactly what barakoCMS's integration tests exist to cover: the real signing key, the session epoch
check, tenant claims and the role and capability gates. It also does not help with the real problem,
which is that FastEndpoints keeps the signing key and its service resolver in process-wide statics.

## The cost: a second host per test

Alba 8 cannot wrap an existing `WebApplicationFactory`; every `AlbaHost` builds its own. So each
test boots a second barakoCMS against the shared database, with its own projection daemon and
background services, and that boot replaces FastEndpoints' static service resolver. The pilot keeps
the host's lifetime inside `FastEndpointsResolver.Keep()`, which puts the fixture's resolver back on
dispose; without it every class that runs afterwards in the same process resolves services from a
disposed provider. The signing key is the same value (`IntegrationTestFixture.JwtKey`), so the
static key being written again is harmless here, but only because both hosts share the fixture's
configuration.

Measured on a 4-core box, the class alone, fixture start included:

| Style | Wall time for the class |
| --- | --- |
| Before, `_factory.CreateClient()` | 10.8 s |
| Alba, one `AlbaHost` per test | 16.4 s |

A class fixture could share one `AlbaHost` across a class's tests, which halves nothing that
matters: it is still a second host, and the resolver has to be kept around the whole class.

## Lines per test

| Test | Before | Alba |
| --- | --- | --- |
| `An_ordinary_response_carries_the_api_contract_version_header` | 11 | 8 |
| `The_header_is_present_even_on_the_meta_endpoints_own_401` | 11 | 8 |
| File | 46 | 67 |

Each test is two or three lines shorter. The file is longer, because of the host lifecycle every
Alba class has to carry.

## Same assertions, and each goes red

Both tests keep exactly their assertions. The first never checked a status, so it calls
`s.IgnoreStatusCode()` rather than taking Alba's implicit expectation of a 200. Each assertion was
broken on purpose under both styles and failed, then put back.

Header expected `999` in the first test:

```
Alba:    Alba.ScenarioAssertionException : Expected a single header value of
         'X-Api-Contract-Version'='999', but the actual value was '7'
           /_/src/Alba/ScenarioAssertionException.cs(28,0): at Alba.ScenarioAssertionException.AssertAll()
           /_/src/Alba/Scenario.cs(300,0): at Alba.Scenario.RunAssertions(HttpContext context)

Before:  Expected values![0] to be "999" with a length of 3, but "7" has a length of 1,
         differs near "7" (index 0).
           ApiContractVersionHeaderTests.cs(29,0): at BarakoCMS.Tests.ApiContractVersionHeaderTests.An_ordinary_...
```

Status expected 403 in the second test:

```
Alba:    Alba.ScenarioAssertionException : Expected status code 403, but was 401
Before:  Expected response.StatusCode to be HttpStatusCode.Forbidden {value: 403}, but found
         HttpStatusCode.Unauthorized {value: 401}.
           ApiContractVersionHeaderTests.cs(40,0): at BarakoCMS.Tests.ApiContractVersionHeaderTests.The_header_...
```

The header assertion of the second test, broken with the status left correct, failed the same way
as the first.

Alba's message is better at one thing: it names the header. It also reports every failed
expectation of a scenario together rather than stopping at the first. Its stack trace ends inside
Alba and never names the test's line; FluentAssertions points at the line that failed.

## Why drop it

What Alba adds here is a few lines per test and a header-aware message. What it costs is a second
application per test against the shared database, a process-wide resolver that has to be saved and
restored around every host, slower classes, and a stack trace that stops at the library. The
fixture already gives a configured client, real tokens and `WithSetting`. The trade is not worth a
dependency.
