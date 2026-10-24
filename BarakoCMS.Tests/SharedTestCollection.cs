using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Classes that swap Serilog's process-wide <c>Log.Logger</c> run alone, so a parallel class cannot
/// replace the logger mid-test and swallow the event being asserted.
/// </summary>
[CollectionDefinition(nameof(GlobalLoggerCollection), DisableParallelization = true)]
public class GlobalLoggerCollection;
