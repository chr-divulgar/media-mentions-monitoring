using System.Reflection;

using Xunit;

namespace MediaOpsCore.ArchitectureTests;

public sealed class ArchitectureBoundariesTests
{
    [Fact]
    public void Domain_assemblies_should_not_reference_application_or_infrastructure()
    {
        AssertNoReferences(
            typeof(MediaOpsCore.BuildingBlocks.Domain.DomainAssemblyMarker).Assembly,
            "MediaOpsCore.BuildingBlocks.Application",
            "MediaOpsCore.BuildingBlocks.Infrastructure");

        AssertNoReferences(
            typeof(MediaOpsCore.Modules.Capture.Domain.CaptureDomainAssemblyMarker).Assembly,
            "MediaOpsCore.Modules.Capture.Application",
            "MediaOpsCore.Modules.Capture.Infrastructure");

        AssertNoReferences(
            typeof(MediaOpsCore.Modules.Segmentation.Domain.SegmentationDomainAssemblyMarker).Assembly,
            "MediaOpsCore.Modules.Segmentation.Application",
            "MediaOpsCore.Modules.Segmentation.Infrastructure");

        AssertNoReferences(
            typeof(MediaOpsCore.Modules.ProcessGuardian.Domain.ProcessGuardianDomainAssemblyMarker).Assembly,
            "MediaOpsCore.Modules.ProcessGuardian.Application",
            "MediaOpsCore.Modules.ProcessGuardian.Infrastructure");
    }

    [Fact]
    public void Application_assemblies_should_not_reference_infrastructure()
    {
        AssertNoReferences(
            typeof(MediaOpsCore.BuildingBlocks.Application.ApplicationAssemblyMarker).Assembly,
            "MediaOpsCore.BuildingBlocks.Infrastructure");

        AssertNoReferences(
            typeof(MediaOpsCore.Modules.Capture.Application.CaptureApplicationAssemblyMarker).Assembly,
            "MediaOpsCore.Modules.Capture.Infrastructure");

        AssertNoReferences(
            typeof(MediaOpsCore.Modules.Segmentation.Application.SegmentationApplicationAssemblyMarker).Assembly,
            "MediaOpsCore.Modules.Segmentation.Infrastructure");

        AssertNoReferences(
            typeof(MediaOpsCore.Modules.ProcessGuardian.Application.ProcessGuardianApplicationAssemblyMarker).Assembly,
            "MediaOpsCore.Modules.ProcessGuardian.Infrastructure");
    }

    [Fact]
    public void Infrastructure_assemblies_should_reference_application_and_domain()
    {
        AssertReferences(
            typeof(MediaOpsCore.BuildingBlocks.Infrastructure.InfrastructureAssemblyMarker).Assembly,
            "MediaOpsCore.BuildingBlocks.Application",
            "MediaOpsCore.BuildingBlocks.Domain");

        AssertReferences(
            typeof(MediaOpsCore.Modules.Capture.Infrastructure.CaptureInfrastructureAssemblyMarker).Assembly,
            "MediaOpsCore.Modules.Capture.Application",
            "MediaOpsCore.Modules.Capture.Domain");

        AssertReferences(
            typeof(MediaOpsCore.Modules.Segmentation.Infrastructure.SegmentationInfrastructureAssemblyMarker).Assembly,
            "MediaOpsCore.Modules.Segmentation.Application",
            "MediaOpsCore.Modules.Segmentation.Domain");

        AssertReferences(
            typeof(MediaOpsCore.Modules.ProcessGuardian.Infrastructure.ProcessGuardianInfrastructureAssemblyMarker).Assembly,
            "MediaOpsCore.Modules.ProcessGuardian.Application",
            "MediaOpsCore.Modules.ProcessGuardian.Domain");
    }

    [Fact]
    public void Worker_should_not_reference_infrastructure_assemblies()
    {
        AssertNoReferences(
            typeof(MediaOpsCore.Workers.Operations.OperationsWorkerOptions).Assembly,
            "MediaOpsCore.BuildingBlocks.Infrastructure",
            "MediaOpsCore.Modules.Capture.Infrastructure",
            "MediaOpsCore.Modules.Segmentation.Infrastructure",
            "MediaOpsCore.Modules.ProcessGuardian.Infrastructure");
    }

    private static void AssertReferences(Assembly assembly, params string[] expectedReferences)
    {
        var referenceNames = GetReferenceNames(assembly);

        foreach (var expectedReference in expectedReferences)
        {
            Assert.Contains(expectedReference, referenceNames);
        }
    }

    private static void AssertNoReferences(Assembly assembly, params string[] forbiddenReferences)
    {
        var referenceNames = GetReferenceNames(assembly);

        foreach (var forbiddenReference in forbiddenReferences)
        {
            Assert.DoesNotContain(forbiddenReference, referenceNames);
        }
    }

    private static HashSet<string> GetReferenceNames(Assembly assembly)
    {
        return assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
    }
}