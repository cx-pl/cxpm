using System.IO.Compression;
using System.Text;
using Cxpm.Core;
using Cxpm.Core.Models;
using Xunit;

namespace Cxpm.Tests;

public sealed class ArchiveSafetyTests
{
    [Fact]
    public void RestoreRejectsArchiveChangedSinceLockWasWritten()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-lock-hash-test-").FullName;
        try
        {
            var library = Path.Combine(root, "library");
            var consumer = Path.Combine(root, "consumer");
            var repository = Path.Combine(root, "feed");
            Directory.CreateDirectory(library);
            Directory.CreateDirectory(consumer);
            File.WriteAllText(Path.Combine(library, "library.cxproj"), "name: library\nversion: 1.0.0\ndependencies: []\n");
            PackageManager.Publish(library, repository);
            File.WriteAllText(Path.Combine(consumer, "consumer.cxproj"), """
                name: consumer
                version: 0.1.0
                dependencies:
                - library@1.0.0
                """);
            PackageManager.Restore(consumer, repository, runtimeIdentifier: "win-x64");

            var archivePath = Directory.EnumerateFiles(repository, "*.zip", SearchOption.AllDirectories).Single();
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
            using (var writer = new StreamWriter(archive.CreateEntry("tampered.txt").Open(), Encoding.UTF8))
                writer.Write("modified after locking");

            var error = Assert.Throws<CxpmException>(() =>
                PackageManager.Restore(consumer, repository, runtimeIdentifier: "win-x64"));

            Assert.Contains("Hash mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(consumer, ".packages", "library", "1.0.0", "tampered.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnsafeArchivePathIsRejectedWithoutReplacingRestoredPackage()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-archive-test-").FullName;
        try
        {
            var library = Path.Combine(root, "library");
            var consumer = Path.Combine(root, "consumer");
            var repository = Path.Combine(root, "feed");
            Directory.CreateDirectory(library);
            Directory.CreateDirectory(consumer);
            File.WriteAllText(Path.Combine(library, "library.cxproj"), """
                name: library
                version: 1.0.0
                package:
                  sources:
                  - '*.cx'
                dependencies: []
                """);
            File.WriteAllText(Path.Combine(library, "main.cx"), "original source");
            PackageManager.Publish(library, repository);
            File.WriteAllText(Path.Combine(consumer, "consumer.cxproj"), """
                name: consumer
                version: 0.1.0
                dependencies:
                - library@1.0.0
                """);

            var restore = PackageManager.Restore(consumer, repository, runtimeIdentifier: "win-x64");
            var packageDirectory = Path.Combine(restore.PackagesDirectory, "library", "1.0.0");
            var sentinel = Path.Combine(packageDirectory, "keep.txt");
            File.WriteAllText(sentinel, "preserve previous package");

            var archivePath = Directory.EnumerateFiles(repository, "*.zip", SearchOption.AllDirectories).Single();
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
            using (var writer = new StreamWriter(archive.CreateEntry("source/../../escaped.txt").Open(), Encoding.UTF8))
                writer.Write("unsafe");

            var error = Assert.Throws<CxpmException>(() =>
                PackageManager.Restore(consumer, repository, update: true, runtimeIdentifier: "win-x64"));

            Assert.Contains("unsafe path", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("preserve previous package", File.ReadAllText(sentinel));
            Assert.False(File.Exists(Path.Combine(consumer, ".packages", "escaped.txt")));
            Assert.Empty(Directory.EnumerateDirectories(restore.PackagesDirectory, ".cxpm-*", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RestoreRejectsAssetPayloadsDeferredFromTheFirstRelease()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-unsupported-assets-test-").FullName;
        try
        {
            var library = Path.Combine(root, "library");
            var consumer = Path.Combine(root, "consumer");
            var repository = Path.Combine(root, "feed");
            Directory.CreateDirectory(library);
            Directory.CreateDirectory(consumer);
            File.WriteAllText(Path.Combine(library, "library.cxproj"), "name: library\nversion: 1.0.0\ndependencies: []\n");
            PackageManager.Publish(library, repository);
            File.WriteAllText(Path.Combine(consumer, "consumer.cxproj"), "name: consumer\nversion: 0.1.0\ndependencies:\n- library@1.0.0\n");

            var archivePath = Directory.EnumerateFiles(repository, "*.zip", SearchOption.AllDirectories).Single();
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
            using (var writer = new StreamWriter(archive.CreateEntry("assets/image.dat").Open(), Encoding.UTF8))
                writer.Write("deferred asset");

            var error = Assert.Throws<CxpmException>(() =>
                PackageManager.Restore(consumer, repository, update: true, runtimeIdentifier: "win-x64"));

            Assert.Contains("not supported", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(consumer, ".packages", "library", "1.0.0")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
