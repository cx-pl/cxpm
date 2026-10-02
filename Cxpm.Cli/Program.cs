using Cxpm.Core;
using Cxpm.Core.Models;

try
{
    if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
    {
        PrintHelp();
        return;
    }

    var command = args[0].ToLowerInvariant();
    var values = args.Skip(1).ToArray();
    switch (command)
    {
        case "init":
        {
            var directory = GetOption(values, "--directory") ?? Directory.GetCurrentDirectory();
            var path = PackageManager.Initialize(directory, GetOption(values, "--name"));
            Console.WriteLine($"Created {path}");
            break;
        }
        case "build":
        {
            var archive = PackageManager.Build(GetOption(values, "--project") ?? Directory.GetCurrentDirectory());
            Console.WriteLine($"Packed {archive}");
            break;
        }
        case "publish":
        {
            var project = GetOption(values, "--project") ?? Directory.GetCurrentDirectory();
            var repository = PackageManager.ResolvePublishFeed(project, GetOption(values, "--feed"));
            var destination = PackageManager.Publish(project, repository);
            Console.WriteLine($"Published {destination}");
            break;
        }
        case "add":
        {
            var dependency = values.FirstOrDefault(value => !value.StartsWith("-", StringComparison.Ordinal))
                ?? throw new CxpmException("Usage: cxpm add <package-id@range> [--project <path>]");
            var project = GetOption(values, "--project") ?? Directory.GetCurrentDirectory();
            PackageManager.AddDependency(project, dependency);
            Console.WriteLine($"Added {dependency} to {project}");
            Console.WriteLine("Run 'cxpm restore' to resolve packages, or pass --feed <path-or-url> to select a feed.");
            break;
        }
        case "remove":
        {
            var packageId = values.FirstOrDefault(value => !value.StartsWith("-", StringComparison.Ordinal))
                ?? throw new CxpmException("Usage: cxpm remove <package-id> [--project <path>]");
            var project = GetOption(values, "--project") ?? Directory.GetCurrentDirectory();
            PackageManager.RemoveDependency(project, packageId);
            Console.WriteLine($"Removed {packageId} from {project}");
            Console.WriteLine("Run 'cxpm restore' to update the resolved dependency graph.");
            break;
        }
        case "restore":
        case "update":
        {
            var project = GetOption(values, "--project") ?? Directory.GetCurrentDirectory();
            var feeds = PackageManager.ResolveFeeds(project, GetOption(values, "--feed"));
            var target = GetOption(values, "--target");
            var locked = values.Contains("--locked", StringComparer.Ordinal);
            var result = PackageManager.Restore(project, feeds, update: command == "update",
                runtimeIdentifier: target, locked: locked);
            Console.WriteLine($"Restored {result.PackageCount} package(s) to {result.PackagesDirectory}" +
                (result.UsedLockFile ? " using cxpm.lock" : "; updated cxpm.lock") +
                $" for RID(s) {string.Join(", ", result.RuntimeIdentifiers)}.");
            break;
        }
        case "list":
        {
            var project = GetOption(values, "--project") ?? Directory.GetCurrentDirectory();
            var items = PackageManager.List(project);
            if (items.Count == 0)
                Console.WriteLine("No package dependencies.");
            else
                foreach (var item in items)
                    Console.WriteLine(item);
            break;
        }
        case "clean":
        {
            var project = GetOption(values, "--project") ?? Directory.GetCurrentDirectory();
            PackageManager.Clean(project);
            Console.WriteLine("Removed restored packages tracked by cxpm.lock.");
            break;
        }
        default:
            throw new CxpmException($"Unknown command '{command}'. Run 'cxpm --help' for usage.");
    }
}
catch (Exception exception)
    when (exception is CxpmException or IOException)
{
    Console.Error.WriteLine($"cxpm: {exception.Message}");
    Environment.ExitCode = 1;
}

static string? GetOption(string[] arguments, string name)
{
    var index = Array.IndexOf(arguments, name);
    if (index < 0)
        return null;
    if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        throw new CxpmException($"GetOption '{name}' needs a value.");
    return arguments[index + 1];
}

static void PrintHelp()
{
    Console.WriteLine("cxpm — package manager for CX");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  init [--directory <path>] [--name <id>]");
    Console.WriteLine("  build [--project <path>]");
    Console.WriteLine("  publish [--feed <path-or-url>] [--project <path>]");
    Console.WriteLine("  add <package-id@range> [--project <path>]");
    Console.WriteLine("  remove <package-id> [--project <path>]");
    Console.WriteLine("  restore [--feed <path-or-url>] [--project <path>] [--target <rid>] [--locked]");
    Console.WriteLine("  update [--feed <path-or-url>] [--project <path>] [--target <rid>]");
    Console.WriteLine("  list [--project <path>]");
    Console.WriteLine("  clean [--project <path>]");
}
