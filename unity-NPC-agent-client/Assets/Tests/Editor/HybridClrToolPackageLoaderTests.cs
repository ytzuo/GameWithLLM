using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class HybridClrToolPackageLoaderTests
{
    [Test]
    public void ProductionArtifactSet_DoesNotRequestDebugSymbols()
    {
        byte[] metadata = Encoding.UTF8.GetBytes("metadata");
        byte[] assembly = Encoding.UTF8.GetBytes("assembly");
        byte[] symbols = Encoding.UTF8.GetBytes("symbols");
        HotUpdateReleaseManifest manifest = Manifest("production-policy", metadata, assembly, symbols);
        var loader = new HybridClrToolPackageLoader(new DictionarySource(), false);

        string[] addresses = loader.GetRequiredArtifacts(manifest)
            .Select(item => item.address)
            .ToArray();

        CollectionAssert.AreEquivalent(new[] { "metadata", "assembly" }, addresses);
        Assert.That(addresses, Does.Not.Contain("symbols"));
    }

    [Test]
    public async Task ValidPackage_DiscoversToolsAndReusesLoadedPackage()
    {
        byte[] metadata = Encoding.UTF8.GetBytes("metadata-cache");
        byte[] assembly = Encoding.UTF8.GetBytes("assembly-cache");
        HotUpdateReleaseManifest manifest = Manifest("cache", metadata, assembly, null);
        var source = new DictionarySource(
            ("metadata", metadata),
            ("assembly", assembly));
        var loader = new HybridClrToolPackageLoader(source, false);

        IReadOnlyList<LoadedToolPack> first = await loader.LoadAsync(manifest, CancellationToken.None);
        IReadOnlyList<LoadedToolPack> second = await loader.LoadAsync(manifest, CancellationToken.None);

        Assert.That(second.Single(), Is.SameAs(first.Single()));
        CollectionAssert.AreEquivalent(
            new[] { "game_hotfix_smoke_query", "game_hotfix_smoke_package_info" },
            first.Single().DiscoveredTools.Select(tool => tool.Descriptor.Name).ToArray());
    }

    [Test]
    public void InvalidHash_ReturnsStableErrorCodeBeforeAssemblyLoad()
    {
        byte[] metadata = Encoding.UTF8.GetBytes("metadata-hash");
        byte[] assembly = Encoding.UTF8.GetBytes("assembly-hash");
        HotUpdateReleaseManifest manifest = Manifest("bad-hash", metadata, assembly, null);
        manifest.toolPackages[0].assembly.sha256 = new string('0', 64);
        var loader = new HybridClrToolPackageLoader(
            new DictionarySource(("metadata", metadata), ("assembly", assembly)),
            false);
        LogAssert.Expect(
            LogType.Error,
            new Regex("stage='artifact-validation' code='HOT_UPDATE_ARTIFACT_HASH_MISMATCH'"));

        HotUpdateLoadException error = Assert.ThrowsAsync<HotUpdateLoadException>(async () =>
            await loader.LoadAsync(manifest, CancellationToken.None));

        Assert.That(error.ErrorCode, Is.EqualTo(HotUpdateLoadErrorCodes.ArtifactHashMismatch));
        Assert.That(error.Stage, Is.EqualTo("artifact-validation"));
    }

    private static HotUpdateReleaseManifest Manifest(
        string suffix,
        byte[] metadata,
        byte[] assembly,
        byte[] symbols)
    {
        var package = new HotUpdateToolPackageDeclaration
        {
            packageId = "smoke-test-" + suffix,
            packageVersion = "1.0.0",
            assemblyName = HybridClrBootstrap.SmokeAssemblyName,
            assembly = Artifact("assembly", "smoke.dll.bytes", assembly),
            debugSymbols = symbols == null ? null : Artifact("symbols", "smoke.pdb.bytes", symbols)
        };
        return new HotUpdateReleaseManifest
        {
            releaseId = "release-" + suffix,
            aotMetadata = new[] { Artifact("metadata", "metadata.dll.bytes", metadata) },
            toolPackages = new[] { package },
            activeTools = new[]
            {
                new ToolReleaseDeclaration
                {
                    name = "game_hotfix_smoke_query",
                    source = "hot-update",
                    packageId = package.packageId,
                    packageVersion = package.packageVersion,
                    assemblyName = package.assemblyName,
                    assemblyHash = package.assembly.sha256
                }
            }
        };
    }

    private static HotUpdateArtifactDeclaration Artifact(
        string address,
        string fileName,
        byte[] bytes) => new HotUpdateArtifactDeclaration
        {
            address = address,
            fileName = fileName,
            length = bytes.LongLength,
            sha256 = Sha256(bytes)
        };

    private static string Sha256(byte[] bytes)
    {
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private sealed class DictionarySource : IHotUpdateArtifactSource
    {
        private readonly Dictionary<string, byte[]> _bytes;

        public DictionarySource(params (string address, byte[] bytes)[] entries) =>
            _bytes = entries.ToDictionary(item => item.address, item => item.bytes, StringComparer.Ordinal);

        public Task<byte[]> LoadBytesAsync(
            HotUpdateArtifactDeclaration artifact,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_bytes.TryGetValue(artifact.address, out byte[] bytes))
                throw new InvalidOperationException("Missing fake artifact: " + artifact.address);
            return Task.FromResult(bytes.ToArray());
        }
    }
}
