using Xunit;

namespace BarakoCMS.Tests;

// Written by scripts/assign-test-collections.py. Do not edit by hand.
// One collection, so one IntegrationTestFixture serves every integration class in the process.

[CollectionDefinition("Integration1")]
public class IntegrationCollection1 : ICollectionFixture<IntegrationTestFixture>;
