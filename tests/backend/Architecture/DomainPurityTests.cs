using System.Reflection;
using NetArchTest.Rules;

namespace ArchitectureTests;

/// <summary>
/// Asserts CLAUDE.md's "Domain must have zero external dependencies" rule as an actual
/// failing test, not just documentation. Loads assemblies by name (not by referencing a
/// concrete type) so this keeps working before any real types exist in Domain yet.
/// </summary>
public class DomainPurityTests
{
    private static readonly string[] InfrastructureNamespacePrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Dapper",
        "StackExchange.Redis",
        "MassTransit",
        "Microsoft.AspNetCore",
    ];

    [Fact]
    public void BuildingBlocksDomain_Should_Not_Depend_On_Infrastructure_Packages()
    {
        var assembly = Assembly.Load("BuildingBlocks.Domain");

        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(InfrastructureNamespacePrefixes)
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void BuildingBlocksApplication_Should_Not_Depend_On_Infrastructure_Packages()
    {
        var assembly = Assembly.Load("BuildingBlocks.Application");

        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(InfrastructureNamespacePrefixes)
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Theory]
    [InlineData("BuildingBlocks.Infrastructure")]
    [InlineData("BuildingBlocks.Messaging")]
    public void BuildingBlocksInfrastructureLayer_Should_Not_Depend_On_EntityFrameworkCore(string assemblyName)
    {
        // Data access is Dapper + Dapper Contrib only, repo-wide — see docs/conventions/data-access.md.
        // No EF Core reference is permitted anywhere, including Infrastructure/Messaging, which
        // previously carried it for the write-side DbContext and the EF-based outbox respectively.
        var assembly = Assembly.Load(assemblyName);

        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }
}
