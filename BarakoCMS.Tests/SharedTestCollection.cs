using Xunit;

namespace BarakoCMS.Tests;

[CollectionDefinition("Sequential")]
public class SharedTestCollection : ICollectionFixture<IntegrationTestFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}

/// <summary>
/// Classes that swap Serilog's process-wide <c>Log.Logger</c> run alone, so a parallel class cannot
/// replace the logger mid-test and swallow the event being asserted.
/// </summary>
[CollectionDefinition(nameof(GlobalLoggerCollection), DisableParallelization = true)]
public class GlobalLoggerCollection;
