using System.IO.Compression;
using Cxpm.Core.Models;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Cxpm.Core;

public sealed partial class PackageManager
{
    private const int MaxArchiveEntryCount = 100_000;
    private const int MaxArchivePathLength = 1024;
    private const long MaxCompressedArchiveBytes = 512L * 1024 * 1024;
    private const long MaxUncompressedEntryBytes = 512L * 1024 * 1024;
    private const long MaxUncompressedPackageBytes = 2L * 1024 * 1024 * 1024;
    private static readonly DateTimeOffset ZipEpoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static string Build(string projectPath)
    {
        projectPath = FindProject(projectPath);
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var project = CxProject.Load(projectPath);
        ValidatePackageId(project.Name);
        var version = SemVersion.Parse(project.Version).ToString();
        var outputDirectory = Path.Combine(projectDirectory, ".dist");
        Directory.CreateDirectory(outputDirectory);
        var archivePath = Path.Combine(outputDirectory, $"{project.Name}.{version}.zip");
        var temporaryArchivePath = Path.Combine(outputDirectory, $".{Guid.NewGuid():N}.tmp");

        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        entries[ManifestFileName] = projectPath;
        var selection = project.Package ?? new CxProjectPackageSection();
        var sourceRoot = selection.Root switch
        {
            "." => projectDirectory,
            ".." when Path.GetFileName(projectDirectory).Equals("src", StringComparison.OrdinalIgnoreCase)
                => Directory.GetParent(projectDirectory)!.FullName,
            _ => throw new CxpmException("package.root must be '.' or '..' for a manifest in src/.")
        };
        AddGlobSelections(entries, sourceRoot, selection.Sources, "source", excludeBuildOutput: true);

        foreach (var rid in selection.Binary.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ValidateRid(rid);
            var binaryDirectory = Path.Combine(projectDirectory, "binary", rid);
            if (!Directory.Exists(binaryDirectory))
                throw new CxpmException($"Selected binary RID '{rid}' has no input folder at '{binaryDirectory}'.");
            AddDirectoryFiles(entries, binaryDirectory, $"bin/{rid}");
        }

        if (entries.Count > MaxArchiveEntryCount)
            throw new CxpmException($"Package contains more than {MaxArchiveEntryCount} files.");
        long inputBytes = 0;
        var packageEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (archiveName, sourcePath) in entries)
        {
            ValidateArchivePath(archiveName, packageEntryNames);
            var length = new FileInfo(sourcePath).Length;
            if (length > MaxUncompressedEntryBytes)
                throw new CxpmException($"Package file '{archiveName}' exceeds the {MaxUncompressedEntryBytes / (1024 * 1024)} MiB file limit.");
            inputBytes = checked(inputBytes + length);
            if (inputBytes > MaxUncompressedPackageBytes)
                throw new CxpmException($"Package contents exceed the {MaxUncompressedPackageBytes / (1024 * 1024)} MiB total size limit.");
        }

        using (var archive = ZipFile.Open(temporaryArchivePath, ZipArchiveMode.Create))
        {
            foreach (var (archiveName, sourcePath) in entries)
            {
                var entry = archive.CreateEntry(archiveName, CompressionLevel.Optimal);
                entry.LastWriteTime = ZipEpoch;
                using var input = File.OpenRead(sourcePath);
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }
        if (new FileInfo(temporaryArchivePath).Length > MaxCompressedArchiveBytes)
        {
            File.Delete(temporaryArchivePath);
            throw new CxpmException($"Package archive exceeds the {MaxCompressedArchiveBytes / (1024 * 1024)} MiB size limit.");
        }
        File.Move(temporaryArchivePath, archivePath, overwrite: true);
        return archivePath;
    }

    private static void ReplacePackageDirectory(string packagesRoot, string destination, string archivePath,
        IReadOnlyList<string> runtimeIdentifiers)
    {
        var fullRoot = Path.GetFullPath(packagesRoot) + Path.DirectorySeparatorChar;
        var fullDestination = Path.GetFullPath(destination);
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullDestination.StartsWith(fullRoot, pathComparison))
            throw new CxpmException("Refusing to extract a package outside the project packages folder.");

        Directory.CreateDirectory(packagesRoot);
        var stagingDirectory = Path.Combine(packagesRoot, $".cxpm-{Guid.NewGuid():N}.tmp");
        var backupDirectory = Path.Combine(packagesRoot, $".cxpm-{Guid.NewGuid():N}.bak");
        var destinationExisted = Directory.Exists(fullDestination);
        var movedOldDirectory = false;
        var archiveFileLength = new FileInfo(archivePath).Length;
        if (archiveFileLength > MaxCompressedArchiveBytes)
            throw new CxpmException($"Package archive exceeds the {MaxCompressedArchiveBytes / (1024 * 1024)} MiB size limit.");
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaxArchiveEntryCount)
            throw new CxpmException($"Package archive contains more than {MaxArchiveEntryCount} entries.");
        var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declaredUncompressedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            ValidateArchiveEntry(entry, entryNames, ref declaredUncompressedBytes);
        }

        var selectedRids = SelectPackageRids(archive.Entries, runtimeIdentifiers);

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            var fullPackageDirectory = Path.GetFullPath(stagingDirectory) + Path.DirectorySeparatorChar;
            long extractedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                if (!ShouldExtractEntry(entry.FullName, selectedRids))
                    continue;
                var entryPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var outputPath = Path.GetFullPath(Path.Combine(stagingDirectory, entryPath));
                if (!outputPath.StartsWith(fullPackageDirectory, pathComparison))
                    throw new CxpmException($"Package archive contains an unsafe path '{entry.FullName}'.");
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(outputPath);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                ExtractArchiveEntry(entry, outputPath, ref extractedBytes);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
            if (destinationExisted)
            {
                MoveDirectoryWithRetry(fullDestination, backupDirectory);
                movedOldDirectory = true;
            }
            MoveDirectoryWithRetry(stagingDirectory, fullDestination);
            if (movedOldDirectory)
                Directory.Delete(backupDirectory, recursive: true);
        }
        catch
        {
            if (movedOldDirectory && !Directory.Exists(fullDestination) && Directory.Exists(backupDirectory))
                MoveDirectoryWithRetry(backupDirectory, fullDestination);
            throw;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
            if (Directory.Exists(backupDirectory) && Directory.Exists(fullDestination))
                Directory.Delete(backupDirectory, recursive: true);
        }
    }

    private static void MoveDirectoryWithRetry(string source, string destination)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException exception) when (attempt < maxAttempts && IsTransientWindowsFileLock(exception))
            {
                Thread.Sleep(25 * (1 << (attempt - 1)));
            }
        }
    }

    private static bool IsTransientWindowsFileLock(IOException exception)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        var windowsError = exception.HResult & 0xFFFF;
        return windowsError is 5 or 32 or 33;
    }

    private static void ValidateArchiveEntry(ZipArchiveEntry entry, ISet<string> entryNames,
        ref long declaredUncompressedBytes)
    {
        var name = entry.FullName;
        var path = ValidateArchivePath(name, entryNames);
        if (entry.Length > MaxUncompressedEntryBytes)
            throw new CxpmException($"Package archive entry '{name}' exceeds the {MaxUncompressedEntryBytes / (1024 * 1024)} MiB size limit.");
        if (name.EndsWith("/", StringComparison.Ordinal) && entry.Length != 0)
            throw new CxpmException($"Package archive directory entry '{name}' contains data.");
        declaredUncompressedBytes = checked(declaredUncompressedBytes + entry.Length);
        if (declaredUncompressedBytes > MaxUncompressedPackageBytes)
            throw new CxpmException($"Package archive expands beyond the {MaxUncompressedPackageBytes / (1024 * 1024)} MiB package limit.");

        // Unix ZIP archives can mark an entry as a symbolic link in the upper mode bits.
        var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixFileType == 0xA000)
            throw new CxpmException($"Package archive contains a symbolic link '{name}'.");
    }

    private static string ValidateArchivePath(string name, ISet<string> entryNames)
    {
        var path = name.EndsWith("/", StringComparison.Ordinal) ? name[..^1] : name;
        var segments = path.Split('/');
        if (string.IsNullOrWhiteSpace(path) || name.Length > MaxArchivePathLength
            || name.Contains('\\') || Path.IsPathRooted(name)
            || segments.Any(segment => segment.Length == 0 || segment.Length > 255
                || segment is "." or ".." || segment.Contains(':')))
            throw new CxpmException($"Package archive contains an unsafe path '{name}'.");
        if (!entryNames.Add(path))
            throw new CxpmException($"Package archive contains duplicate paths that differ only by case or directory notation: '{name}'.");
        return path;
    }

    private static void ExtractArchiveEntry(ZipArchiveEntry entry, string outputPath, ref long extractedBytes)
    {
        using var input = entry.Open();
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.SequentialScan);
        var buffer = new byte[81920];
        long entryBytes = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            entryBytes += read;
            extractedBytes += read;
            if (entryBytes > MaxUncompressedEntryBytes || extractedBytes > MaxUncompressedPackageBytes)
                throw new CxpmException("Package archive expands beyond the configured extraction size limit.");
            output.Write(buffer, 0, read);
        }
        if (entryBytes != entry.Length)
            throw new CxpmException($"Package archive entry '{entry.FullName}' expanded to an unexpected size.");
    }

    private static IReadOnlySet<string> SelectPackageRids(IEnumerable<ZipArchiveEntry> entries,
        IEnumerable<string> runtimeIdentifiers)
    {
        const string binaryPrefix = "bin/";
        var availableRids = entries
            .Select(entry => entry.FullName)
            .Where(path => path.StartsWith(binaryPrefix, StringComparison.Ordinal))
            .Select(path =>
            {
                var ridEnd = path.IndexOf('/', binaryPrefix.Length);
                return ridEnd > 0 ? path[binaryPrefix.Length..ridEnd] : null;
            })
            .Where(rid => rid is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var runtimeIdentifier in runtimeIdentifiers)
        {
            var packageRid = RuntimeIdentifierGraph.GetFallbacks(runtimeIdentifier)
                .FirstOrDefault(rid => availableRids.Contains(rid));
            if (packageRid is not null)
                selected.Add(packageRid);
        }
        return selected;
    }

    private static bool ShouldExtractEntry(string entryPath, IReadOnlySet<string> selectedRids)
    {
        const string binaryPrefix = "bin/";
        if (entryPath == ManifestFileName || entryPath.StartsWith("source/", StringComparison.Ordinal))
            return true;
        if (!entryPath.StartsWith(binaryPrefix, StringComparison.Ordinal))
            throw new CxpmException($"Package archive entry '{entryPath}' is not supported. The first release supports source/ and bin/<rid>/ payloads.");
        var ridEnd = entryPath.IndexOf('/', binaryPrefix.Length);
        if (ridEnd < 0)
            return false;
        var packageRid = entryPath[binaryPrefix.Length..ridEnd];
        return selectedRids.Contains(packageRid);
    }

    private static void AddGlobSelections(IDictionary<string, string> entries, string root,
        IEnumerable<string> patterns, string destinationPrefix, bool excludeBuildOutput)
    {
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern) || Path.IsPathRooted(pattern) || pattern.Contains("..", StringComparison.Ordinal))
                throw new CxpmException($"Invalid package file pattern '{pattern}'. Patterns must be relative to the project.");
            var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(pattern);
            if (!pattern.Contains('/') && !pattern.Contains('\\'))
                matcher.AddInclude($"**/{pattern}");
            if (excludeBuildOutput)
            {
                matcher.AddExclude(".packages/**");
                matcher.AddExclude("**/.packages/**");
                matcher.AddExclude("**/.obj/**");
                matcher.AddExclude("**/.bin/**");
                matcher.AddExclude("**/.dist/**");
                matcher.AddExclude("packages/**");
                matcher.AddExclude(".dist/**");
                matcher.AddExclude("dist/**");
                matcher.AddExclude("bin/**");
                matcher.AddExclude("obj/**");
                matcher.AddExclude("binary/**");
                matcher.AddExclude("assets/**");
            }
            var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(root)));
            foreach (var file in result.Files)
            {
                var relativePath = file.Path.Replace('\\', '/');
                var source = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
                entries[$"{destinationPrefix}/{relativePath}"] = source;
            }
        }
    }

    private static void AddDirectoryFiles(IDictionary<string, string> entries, string root, string prefix)
    {
        foreach (var source in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(root, source).Replace('\\', '/');
            entries[$"{prefix}/{relativePath}"] = source;
        }
    }
}
