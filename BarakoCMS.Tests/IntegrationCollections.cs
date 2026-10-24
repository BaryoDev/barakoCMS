using Xunit;

namespace BarakoCMS.Tests;

// Written by scripts/assign-test-collections.py. Do not edit by hand.
// Each parallel collection owns an IntegrationTestFixture (its own Postgres container and host),
// and xUnit runs the collections side by side. IntegrationSerial runs alone, after them.

[CollectionDefinition("Integration1")]
public class IntegrationCollection1 : ICollectionFixture<IntegrationTestFixture>;

[CollectionDefinition("IntegrationSerial", DisableParallelization = true)]
public class IntegrationSerialCollection : ICollectionFixture<IntegrationTestFixture>;
