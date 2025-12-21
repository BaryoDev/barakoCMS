# Testing

BarakoCMS has a comprehensive test suite ensuring reliability and correctness across all features.

## Test Coverage

**Current Status: 180/181 tests passing (99.4%)**

- Integration Tests: 170+ tests
- Unit Tests: 10+ tests
- Coverage: Core features, RBAC, workflows, validation, event sourcing

## Test Infrastructure

### Technology Stack

- **xUnit**: Test framework
- **FluentAssertions**: Readable test assertions
- **TestContainers**: Docker-based PostgreSQL for integration tests
- **Moq/NSubstitute**: Mocking frameworks for unit tests

### Test Fixtures

The test suite uses shared fixtures for efficiency:

- `IntegrationTestFixture`: WebApplicationFactory with TestContainers PostgreSQL
- `TestHelpers`: Shared utilities for creating test data (admin users, etc.)
- Inline projections for immediate consistency in tests

## Running Tests

### Local Development

```bash
# Run all tests
dotnet test

# Run specific project
dotnet test BarakoCMS.Tests/BarakoCMS.Tests.csproj

# Run with detailed output
dotnet test --logger "console;verbosity=detailed"

# Run in Release mode
dotnet test --configuration Release
```

### Prerequisites

- Docker must be running (for TestContainers)
- .NET 8.0 SDK
- PostgreSQL image will be pulled automatically

## CI/CD Integration

### GitHub Actions

Two workflows ensure code quality:

**CI Workflow** (`.github/workflows/ci.yml`)
- Runs on every push to master and PRs
- Executes full test suite
- Provides test summary in GitHub Actions

**NuGet Publish Workflow** (`.github/workflows/nuget-publish.yml`)
- Runs tests before publishing
- Only publishes if all tests pass
- Automatic version bumping

### Test Environment

GitHub Actions runners have Docker pre-installed, allowing TestContainers to work seamlessly:

```yaml
- name: Test BarakoCMS.Tests
  run: dotnet test BarakoCMS.Tests/BarakoCMS.Tests.csproj --no-build --configuration Release
  env:
      DOCKER_HOST: unix:///var/run/docker.sock
```

## Test Organization

### Integration Tests

Located in `BarakoCMS.Tests/`, organized by feature:

- **Authentication & Authorization**: User registration, login, RBAC
- **Content Management**: CRUD operations, status changes
- **Validation**: ContentType validation, field type checking, PascalCase enforcement
- **Workflows**: Event-driven actions, email triggers
- **Concurrency**: Version control, optimistic locking
- **Event Sourcing**: Event streaming, projections

### Key Test Files

| File | Purpose |
|------|---------|
| `IntegrationTests.cs` | Core API integration tests |
| `ValidationIntegrationTests.cs` | ContentType & field validation |
| `SecurityTests.cs` | Authentication & authorization |
| `WorkflowTests.cs` | Workflow engine functionality |
| `ConcurrencyTests.cs` | Version control & conflicts |
| `EventRegistrationTests.cs` | End-to-end scenarios |

## Test Patterns

### Creating Admin Users

Use the shared `TestHelpers` for consistent test setup:

```csharp
var (token, userId) = await TestHelpers.CreateAdminUserAsync(_fixture);
_client.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", token);
```

### Separate HttpClient Instances

Avoid shared state by creating separate clients:

```csharp
var userClient = _factory.CreateClient();
userClient.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", userToken);

var adminClient = _factory.CreateClient();
adminClient.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", adminToken);
```

### Testing Idempotency

```csharp
_client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);

var response1 = await _client.PostAsJsonAsync("/api/contents", request);
response1.EnsureSuccessStatusCode();

var response2 = await _client.PostAsJsonAsync("/api/contents", request);
response2.StatusCode.Should().Be(HttpStatusCode.Conflict); // 409
```

## Recent Improvements (Phase 3 & 4)

### ContentType Validation

Added comprehensive validation for ContentType creation:
- Field type validation (string, int, bool, datetime, decimal, array, object)
- PascalCase naming enforcement with helpful suggestions
- Descriptive error messages

### Test Fixes

**Phase 3 Fixes:**
- ✅ Fixed 4 skipped ContentType validation tests
- ✅ Fixed `Auth_RBAC_Flow` flaky test (HttpClient isolation)
- ✅ Fixed `EventRegistration_EndToEnd_Scenario` (proper admin setup, idempotency expectations)

**Phase 4 Improvements:**
- ✅ Extracted `CreateAdminUserAsync` to shared `TestHelpers` class
- ✅ Fixed compiler warnings (async methods, null literals)
- ✅ Improved test infrastructure consistency

### Result: 99.4% Pass Rate

From 174 passing tests to 180 passing tests:
- **Before**: 174 passing, 7 skipped
- **After**: 180 passing, 1 skipped (intentionally - covered by integration tests)

## Troubleshooting

### Docker Issues

If tests fail with "Docker not available":

```bash
# macOS/Linux
docker ps

# Ensure Docker daemon is running
```

### Port Conflicts

TestContainers automatically assigns random ports. If you see port conflicts:
- Stop other PostgreSQL instances
- Restart Docker

### Test Isolation

Tests use the `[Collection("Sequential")]` attribute to prevent parallel execution conflicts with shared database state.

## Best Practices

1. **Always read before writing**: Use `Read` tool before `Edit` for existing test files
2. **Prefer integration tests**: They test real behavior, not mocks
3. **Use FluentAssertions**: Makes test failures more readable
4. **Isolate test clients**: Create separate HttpClient instances to avoid state pollution
5. **Clean up resources**: TestContainers handles cleanup automatically
6. **Test error cases**: Don't just test happy paths

## Next Steps

- Add API documentation tests
- Increase unit test coverage for complex business logic
- Add performance benchmarks
- Consider contract testing for API consumers
