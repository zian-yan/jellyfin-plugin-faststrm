using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace FastSTRM.Test;

public class PluginMetadataTests
{
    [Fact]
    public void PluginGuid_IsParsableNonEmptyGuid()
    {
        var guid = Guid.Parse(Plugin.PluginGuid);
        Assert.NotEqual(Guid.Empty, guid);
    }

    [Fact]
    public void PluginGuid_MatchesMetaJson()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "meta.json")));
        var metaGuid = doc.RootElement.GetProperty("guid").GetString();

        Assert.Equal(Plugin.PluginGuid, metaGuid, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Version_MatchesAcrossCsprojMetaJsonAndBuildYaml()
    {
        var root = RepoRoot();
        var csprojVersion = ReadCsprojVersion(root);
        var metaVersion = ReadMetaJsonVersion(root);
        var buildVersion = ReadBuildYamlVersion(root);

        Assert.False(string.IsNullOrWhiteSpace(csprojVersion));
        Assert.Equal(Normalize(csprojVersion), Normalize(metaVersion));
        Assert.Equal(Normalize(csprojVersion), Normalize(buildVersion));
    }

    [Fact]
    public void TargetAbi_MatchesBuildYamlFramework()
    {
        var root = RepoRoot();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "meta.json")));
        var targetAbi = doc.RootElement.GetProperty("targetAbi").GetString();

        Assert.False(string.IsNullOrWhiteSpace(targetAbi));
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", targetAbi!);
    }

    [Fact]
    public void Manifest_MatchesGuidAndKeepsVersionsOrdered()
    {
        var root = RepoRoot();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));

        var plugin = doc.RootElement[0];
        Assert.Equal(Plugin.PluginGuid, plugin.GetProperty("guid").GetString(), StringComparer.OrdinalIgnoreCase);

        var versions = plugin.GetProperty("versions").EnumerateArray().ToList();
        Assert.NotEmpty(versions);

        var previous = new Version(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue);
        foreach (var entry in versions)
        {
            var version = new Version(entry.GetProperty("version").GetString()!);
            Assert.True(version <= previous, $"manifest.json 版本未按从新到旧排列：{version} 出现在 {previous} 之后");
            previous = version;

            var checksum = entry.TryGetProperty("checksum", out var checksumElement)
                ? checksumElement.GetString()
                : null;

            if (!string.IsNullOrEmpty(checksum))
            {
                Assert.Matches("^[0-9a-f]{32}$", checksum);
            }
        }
    }

    private static string ReadCsprojVersion(string root)
    {
        var csproj = Path.Combine(root, "Jellyfin.Plugin.FastSTRM", "Jellyfin.Plugin.FastSTRM.csproj");
        var version = XDocument.Load(csproj).Root?.Element("PropertyGroup")?.Element("Version")?.Value;

        return version ?? throw new InvalidOperationException($"<Version> not found in {csproj}");
    }

    private static string ReadMetaJsonVersion(string root)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "meta.json")));

        return doc.RootElement.GetProperty("version").GetString()
               ?? throw new InvalidOperationException("meta.json version is null");
    }

    private static string ReadBuildYamlVersion(string root)
    {
        var text = File.ReadAllText(Path.Combine(root, "build.yaml"));
        var match = Regex.Match(text, "^version:\\s*\"?([^\"\\r\\n]+)\"?", RegexOptions.Multiline);

        return match.Success
            ? match.Groups[1].Value.Trim()
            : throw new InvalidOperationException("version not found in build.yaml");
    }

    private static string Normalize(string version)
    {
        var match = Regex.Match(version.Trim(), @"^(\d+\.\d+\.\d+)\.\d+$");

        return match.Success ? match.Groups[1].Value : version.Trim();
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.FastSTRM.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException($"solution file not found above {sourceFile}");
    }
}
