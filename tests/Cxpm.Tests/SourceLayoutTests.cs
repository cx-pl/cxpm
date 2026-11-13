using System.IO.Compression;
using Cxpm.Core;
using Cxpm.Core.Models;
using Xunit;

namespace Cxpm.Tests;

public sealed class SourceLayoutTests
{
    [Fact]
    public void RepositoryRootPackagePreservesNativeSourcesAndRestoresFromSrcManifest()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-source-layout-").FullName;
        try
        {
            var library = Path.Combine(root, "library");
            var source = Path.Combine(library, "src");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(Path.Combine(library, "native"));
            Directory.CreateDirectory(Path.Combine(source, ".obj"));
            File.WriteAllText(Path.Combine(source, "layout.cxproj"), """
                name: layout
                version: 1.0.0
                type: Library
                package:
                  root: ..
                  sources: ['src/**/*.cx', 'native/**/*.c']
                """);
            File.WriteAllText(Path.Combine(source, "api.cx"), "public class Value {}");
            File.WriteAllText(Path.Combine(source, ".obj", "unexpected.cx"), "generated");
            File.WriteAllText(Path.Combine(library, "native", "adapter.c"), "int adapter(void) { return 1; }");
            var archivePath = PackageManager.Build(library);
            Assert.StartsWith(Path.Combine(source, ".dist"), archivePath);
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                Assert.Contains(archive.Entries, e => e.FullName == "source/src/api.cx");
                Assert.Contains(archive.Entries, e => e.FullName == "source/native/adapter.c");
                Assert.DoesNotContain(archive.Entries, e => e.FullName.Contains(".obj"));
            }
            var feed = Path.Combine(root, "feed");
            PackageManager.Publish(library, feed);
            var consumer = Path.Combine(root, "consumer");
            Directory.CreateDirectory(consumer);
            File.WriteAllText(Path.Combine(consumer, "consumer.cxproj"),
                "name: consumer\nversion: 1.0.0\ndependencies: ['layout@1.0.0']\n");
            PackageManager.Restore(consumer, feed);
            Assert.True(File.Exists(Path.Combine(consumer, ".packages", "layout", "1.0.0", "source", "native", "adapter.c")));
            Assert.True(PackageManager.Restore(consumer, feed, locked: true).UsedLockFile);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("../..")]
    [InlineData("/")]
    public void PackageCannotSelectAnArbitraryRoot(string packageRoot)
    {
        var root = Directory.CreateTempSubdirectory("cxpm-invalid-layout-").FullName;
        try
        {
            var source = Path.Combine(root, "src");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "layout.cxproj"),
                $"name: layout\nversion: 1.0.0\npackage:\n  root: '{packageRoot}'\n  sources: ['*.cx']\n");
            Assert.Throws<CxpmException>(() => PackageManager.Build(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
