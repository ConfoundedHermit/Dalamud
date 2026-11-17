using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using Dalamud.Interface.ImGuiBackend.Helpers;
using Dalamud.Interface.Internal.Unwrapper;
using Dalamud.Support;

using Xunit;

namespace Dalamud.Test.Interface;

/// <summary>Exercises ReShade module recognition and loader-reference lifetimes with isolated native DLLs.</summary>
public class ReShadeModuleDiscoveryTests
{
    private static readonly string[] GraphicsExports =
    [
        "CreateDXGIFactory", "D2D1CreateDevice", "D3D10CreateDevice", "D3D11CreateDevice",
        "D3D12CreateDevice", "glBegin", "vkCreateDevice",
    ];

    private static readonly string[] AddonExports =
    [
        "ReShadeRegisterAddon", "ReShadeUnregisterAddon", "ReShadeRegisterEvent", "ReShadeUnregisterEvent",
    ];

    [Theory]
    [InlineData("ReShade", true)]
    [InlineData("ReShade 6.0.0", true)]
    [InlineData("GShade", true)]
    [InlineData("reshade", false)]
    [InlineData("Ordinary graphics proxy", false)]
    [InlineData(null, false)]
    public void BackendPreservesDescriptionPolicyForRenamedProxy(string? description, bool expected)
    {
        using var fixture = new NativeModuleFixture(GraphicsExports, description);
        Assert.Equal(description, FileVersionInfo.GetVersionInfo(fixture.FileName).FileDescription);

        // Module filenames can vary independently of their version metadata.
        Assert.Equal("module-proxy.dll", Path.GetFileName(fixture.FileName));
        Assert.Equal(expected, ReShadePeeler.BelongsInReShadeDll(fixture.Address));
        Assert.False(ReShadeUnwrapper.BelongsInReShadeDll(fixture.Address));
        fixture.ReleaseOriginalReference();
        Assert.False(fixture.IsLoaded);
    }

    public static IEnumerable<object[]> MissingGraphicsExports() => GraphicsExports.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(MissingGraphicsExports))]
    public void BackendRequiresEveryGraphicsExport(string missingExport)
    {
        using var fixture = new NativeModuleFixture(GraphicsExports.Where(name => name != missingExport), "ReShade");
        Assert.False(ReShadePeeler.BelongsInReShadeDll(fixture.Address));
        fixture.ReleaseOriginalReference();
        Assert.False(fixture.IsLoaded);
    }

    public static IEnumerable<object[]> MissingAddonExports() => AddonExports.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(MissingAddonExports))]
    public void StartupRequiresEveryAddonExport(string missingExport)
    {
        using var fixture = new NativeModuleFixture(AddonExports.Where(name => name != missingExport), null);
        Assert.False(ReShadeUnwrapper.BelongsInReShadeDll(fixture.Address));
        fixture.ReleaseOriginalReference();
        Assert.False(fixture.IsLoaded);
    }

    [Fact]
    public void StartupAddonPolicyDoesNotRequireGraphicsExportsOrDescription()
    {
        using var fixture = new NativeModuleFixture(AddonExports, null);
        Assert.True(ReShadeUnwrapper.BelongsInReShadeDll(fixture.Address));
        Assert.False(ReShadePeeler.BelongsInReShadeDll(fixture.Address));
        fixture.ReleaseOriginalReference();
        Assert.False(fixture.IsLoaded);
    }

    [Theory]
    [InlineData("ReShade", true)]
    [InlineData("Ordinary graphics proxy", false)]
    public void RepeatedClassificationsDoNotLeakModuleReferences(string description, bool backendMatch)
    {
        using var fixture = new NativeModuleFixture(GraphicsExports.Concat(AddonExports), description);
        for (var i = 0; i < 32; i++)
        {
            Assert.Equal(backendMatch, ReShadePeeler.BelongsInReShadeDll(fixture.Address));
            Assert.True(ReShadeUnwrapper.BelongsInReShadeDll(fixture.Address));
        }

        fixture.ReleaseOriginalReference();
        Assert.False(fixture.IsLoaded);
        Assert.Null(ModuleReference.FromAddress(fixture.Address));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void InvalidAddressIsNotRecognized(long address)
    {
        Assert.Null(ModuleReference.FromAddress((nint)address));
        Assert.False(ReShadePeeler.BelongsInReShadeDll((nint)address));
        Assert.False(ReShadeUnwrapper.BelongsInReShadeDll((nint)address));
    }

    [Fact]
    public void OrdinarySystemModuleIsNotRecognized()
    {
        var handle = NativeLibrary.Load("kernel32.dll");
        try
        {
            var address = NativeLibrary.GetExport(handle, "GetCurrentProcess");
            Assert.False(ReShadePeeler.BelongsInReShadeDll(address));
            Assert.False(ReShadeUnwrapper.BelongsInReShadeDll(address));
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    [Fact]
    public async Task AcquiredReferenceSurvivesOriginalReleaseOnAnotherThread()
    {
        using var fixture = new NativeModuleFixture(GraphicsExports, "ReShade");
        using var module = ModuleReference.FromAddress(fixture.Address);
        Assert.NotNull(module);

        await Task.Factory.StartNew(
            fixture.ReleaseOriginalReference,
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Assert.True(fixture.IsLoaded);
        Assert.True(module.HasExport("CreateDXGIFactory"u8));
        Assert.False(module.HasExport("MissingExport"u8));
        Assert.Equal(fixture.FileName, module.GetFileName(), StringComparer.OrdinalIgnoreCase);
        Assert.True(ReShadePeeler.BelongsInReShadeDll(fixture.Address));

        module.Dispose();
        module.Dispose();
        Assert.False(fixture.IsLoaded);
        Assert.Null(ModuleReference.FromAddress(fixture.Address));
    }

    [Fact]
    public void ExceptionalInspectionReleasesAcquiredReference()
    {
        using var fixture = new NativeModuleFixture(GraphicsExports, "ReShade");
        Assert.Throws<InvalidOperationException>(new Action(() =>
        {
            using var module = ModuleReference.FromAddress(fixture.Address);
            Assert.NotNull(module);
            fixture.ReleaseOriginalReference();
            Assert.True(fixture.IsLoaded);
            throw new InvalidOperationException("Inspection failed.");
        }));

        Assert.False(fixture.IsLoaded);
    }

    [Fact]
    public void MissingMetadataFileIsANonMatchAndReleasesAcquiredReferences()
    {
        using var fixture = new NativeModuleFixture(GraphicsExports.Concat(AddonExports), "ReShade");
        var renamed = Path.Combine(Path.GetDirectoryName(fixture.FileName)!, "renamed.dll");
        File.Move(fixture.FileName, renamed);
        Assert.True(fixture.IsLoaded);
        Assert.Throws<FileNotFoundException>(() => FileVersionInfo.GetVersionInfo(fixture.FileName));

        // The loader's original path no longer resolves, but addon exports remain available in memory.
        for (var i = 0; i < 32; i++)
        {
            Assert.False(ReShadePeeler.BelongsInReShadeDll(fixture.Address));
            Assert.True(ReShadeUnwrapper.BelongsInReShadeDll(fixture.Address));
        }

        fixture.ReleaseOriginalReference();
        Assert.False(fixture.IsLoaded);
    }

    [Fact]
    public void ReloadedAddressIsClassifiedUsingCurrentModule()
    {
        nint oldAddress;
        using (var fixture = new NativeModuleFixture(GraphicsExports, "ReShade"))
        {
            oldAddress = fixture.Address;
            Assert.True(ReShadePeeler.BelongsInReShadeDll(oldAddress));
            fixture.ReleaseOriginalReference();
            Assert.False(fixture.IsLoaded);
            Assert.Null(ModuleReference.FromAddress(oldAddress));
        }

        // Address reuse is loader-dependent; classify the replacement independently of earlier lookups.
        using var replacement = new NativeModuleFixture(GraphicsExports, "Ordinary graphics proxy");
        Assert.False(ReShadePeeler.BelongsInReShadeDll(replacement.Address));
        replacement.ReleaseOriginalReference();
        Assert.False(replacement.IsLoaded);
    }

    [Fact]
    public void LongModulePathIsRetrievedWithoutPartialPathClassification()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Dalamud-module-long-path-" + Guid.NewGuid().ToString("N"));
        try
        {
            var nested = Path.Combine(directory, new string('a', 120), new string('b', 120));
            Directory.CreateDirectory(nested);
            using var fixture = new NativeModuleFixture(GraphicsExports, "GShade", nested);
            Assert.True(fixture.FileName.Length > 260);
            using var module = ModuleReference.FromAddress(fixture.Address);
            Assert.NotNull(module);
            Assert.Equal(fixture.FileName, module.GetFileName(), StringComparer.OrdinalIgnoreCase);
            Assert.True(ReShadePeeler.BelongsInReShadeDll(fixture.Address));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
