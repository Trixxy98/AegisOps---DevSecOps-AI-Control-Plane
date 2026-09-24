using System.Reflection;

namespace AegisOps.Architecture.Tests;

public sealed class LayerDependencyTests
{
    [Fact]
    public void Domain_does_not_reference_outer_layers()
    {
        AssertNoReference(
            "AegisOps.Domain",
            "AegisOps.Application",
            "AegisOps.Infrastructure",
            "AegisOps.Api",
            "AegisOps.Worker");
    }

    [Fact]
    public void Application_does_not_reference_infrastructure_or_hosts()
    {
        AssertNoReference(
            "AegisOps.Application",
            "AegisOps.Infrastructure",
            "AegisOps.Api",
            "AegisOps.Worker");
    }

    [Fact]
    public void Infrastructure_does_not_reference_hosts()
    {
        AssertNoReference(
            "AegisOps.Infrastructure",
            "AegisOps.Api",
            "AegisOps.Worker");
    }

    private static void AssertNoReference(string assemblyName, params string[] forbidden)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
        var referenced = Assembly.LoadFrom(path)
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToHashSet();

        var hits = forbidden.Where(referenced.Contains).ToArray();

        Assert.True(
            hits.Length == 0,
            $"{assemblyName} must not reference: {string.Join(", ", hits)}");
    }
}