using System.Runtime.CompilerServices;

namespace WeredaConnect.Server.Tests;

/// <summary>
/// Module initializer that sets required environment variables for test execution.
/// Runs before any test class is instantiated.
/// </summary>
internal static class TestEnvironmentSetup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // PaginationTokenHelper requires this env var (no longer has a hardcoded default).
        Environment.SetEnvironmentVariable("PAGINATION_SECRET", "test-pagination-secret-for-unit-tests");
    }
}
