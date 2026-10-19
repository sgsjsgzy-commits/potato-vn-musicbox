using System.Xml.Linq;
using GalgameManager.Helpers;

namespace GalgameManager.Test;

[TestFixture]
public class ProjectTest
{
    [Test]
    public void Version_ShouldMatch_PackageAppxmanifest()
    {
        var manifestVersion = ReadPackageAppxManifestIdentityVersion();

        var runtimeVersion = RuntimeHelper.GetVersion();
        Assert.That(runtimeVersion, Is.EqualTo(manifestVersion));

        var assemblyVersion = typeof(RuntimeHelper).Assembly.GetName().Version?.ToString();
        Assert.That(assemblyVersion, Is.Not.Null);
        Assert.That(assemblyVersion, Is.EqualTo(manifestVersion));
    }

    private static string ReadPackageAppxManifestIdentityVersion()
    {
        var manifestPath = FindPackageAppxManifestPath();

        var doc = XDocument.Load(manifestPath);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        var identity = doc.Root?.Elements(ns + "Identity").FirstOrDefault();
        var version = identity?.Attribute("Version")?.Value;

        if (string.IsNullOrWhiteSpace(version))
        {
            Assert.Fail($"未能从 {manifestPath} 读取 Identity Version。请检查 Package.appxmanifest 是否包含 <Identity Version=.../>。");
        }

        return version!;
    }

    private static string FindPackageAppxManifestPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "GalgameManager", "Package.appxmanifest");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Fail($"未找到 GalgameManager/Package.appxmanifest。当前测试目录: {AppContext.BaseDirectory}");
        return string.Empty;
    }
}
