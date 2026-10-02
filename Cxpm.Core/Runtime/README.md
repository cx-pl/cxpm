# Runtime identifier graph

`PortableRuntimeIdentifierGraph.json` is copied from Microsoft's .NET runtime
repository, `release/10.0`, and embedded into `Cxpm.Core`. It defines the
portable RID fallback imports used when restoring package binaries. The graph is
maintained by the .NET project; update this snapshot from the matching .NET
release branch when upgrading the supported graph version.

Source: <https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/Microsoft.NETCore.Platforms/src/PortableRuntimeIdentifierGraph.json>
