using Xunit;

namespace MuSite.Tests;

/// <summary>
/// Serialises the database-backed suites.
///
/// PublicQueriesTests and RegistrationInvariantTests both re-apply StandInSchema.sql in their setup,
/// and that script DROPs and recreates the data, config and guild schemas. xUnit runs test classes
/// in parallel by default, so without this the two would tear each other's schema down mid-run -
/// failing in a way that looks random and has nothing to do with the code under test.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DatabaseCollection
{
    /// <summary>The collection name both database-backed suites join.</summary>
    public const string Name = "Database";
}
