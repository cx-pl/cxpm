# cxpm

`cxpm` is the CX package manager CLI, implemented in C# on .NET 10. The initial
vertical slice supports YAML `.cxproj` files, deterministic ZIP packages, local
and HTTP repositories, package dependencies, and project-local restore.

## Build

From this directory:

```powershell
dotnet build Cxpm.slnx
dotnet test Cxpm.slnx
dotnet run --project Cxpm.Cli -- --help
Set-Location Cxpm.Web/clientapp; pnpm install --frozen-lockfile; pnpm run build; Set-Location ../..
dotnet run --project Cxpm.Web
```

`Cxpm.Web` serves the Vue single-page app at `/` and exposes Minimal API routes
under `/api`. See [Cxpm.Web/README.md](Cxpm.Web/README.md) for frontend setup
and build instructions. Its file-backed feed can be used as the HTTP repository
URL `http://localhost:5150/api`. Package reads are public; set `CXPM_TOKEN` on
the server and as the client environment variable to publish packages.
The web app can also run in Docker with `docker compose up --build`; package
data is persisted in a named volume. See the web README for cloud deployment
constraints and storage guidance.

## Package and consume locally

Create a package project:

```powershell
cxpm init --directory . --name cxhello
```

Edit `cxhello.cxproj` to describe package files. For example:

```yaml
name: cxhello
version: 0.1.0
description: Hello package
licence: MIT
targets:
- win-x64
- linux-x64
feeds:
  - ../local-feed
package:
  sources:
  - '*.cx'
  binary:
  - win-x64
dependencies: []
```

`targets` lists the RIDs the project intends to build for; the compiler reads
this list, and `cxpm restore` uses it to restore the nearest compatible native
dependency binaries for each target. Passing `cxpm restore --target <rid>`
overrides the target list for that restore. Without `targets`, restore defaults
to the current process RID. Use RIDs from the [.NET RID catalog](https://learn.microsoft.com/en-us/dotnet/core/rid-catalog).

`package.sources` is a list of include-only glob patterns relative to the project
directory (`*` and `**` are supported; exclusions are not in the first release).
`package.binary` is a list of RIDs; native binary inputs are read from
`binary/<rid>/` and stored in package ZIPs under `bin/<rid>/`. Empty or omitted
selections include no files. Package assets are deferred from the first release.

Build a ZIP in `.dist/`, then publish it to a local repository:

```powershell
cxpm build
cxpm publish --feed ..\local-feed
```

The root-level `feeds` array lists package sources in priority order. Restore
combines versions from all listed feeds; if a version exists in more than one,
the first listed feed supplies it. Local paths are relative to the `.cxproj`
file; HTTP(S) URLs are also supported. `--feed <path-or-url>` overrides the
list with one feed for that command. Publish uses the only configured feed, or
requires `--feed` when more than one feed is configured.

In a consumer project, add and restore the dependency:

```powershell
cxpm add 'cxhello@^0.1.0'
cxpm remove cxhello
cxpm restore --target win-x64
cxpm restore --locked
```

Restore writes `cxpm.lock` beside the project and extracts the full package under
`.packages/<package-id>/<version>/`. The compiler's recursive `.cx` source scan
automatically discovers package source files under `.packages/`. The compiler
can use this stable project-local location for dependency linking as that
support is added. Run `cxpm clean` to remove package versions recorded in the
lock file; untracked files in `.packages/` are preserved. `.packages/` is
ignored by Git.

Use `cxpm update` to recalculate versions within the declared constraints.
`cxpm restore --locked` requires an existing lock file that matches the current
`.cxproj`; it fails instead of resolving or rewriting dependencies when the lock
is missing or stale. `cxpm list` shows direct dependencies and the last resolved
graph.

## Current boundaries

- One or more feeds are supported per project. Local folders and static
  HTTP(S) feeds use the same package-ID/version archive layout. HTTP feeds expose
  `GET {feed}/{lowercase-id}/index.json` with `{"versions":["1.2.3"]}` and
  archives at `{feed}/{lowercase-id}/{version}/{lowercase-id}.{version}.zip`.
  HTTP publishing uses conditional `PUT` requests for immutable archives and
  ETag-protected index updates. Set `CXPM_TOKEN` to send a bearer token. Remote
  archives are cached under the user's local application data directory.
  `Cxpm.Web` implements this feed protocol using a file store and powers the
  package catalog UI. It is designed for a single server instance; credential
  helpers and account management are not implemented.
- Package versions use an in-project SemVer 2.0.0 implementation, with no
  `NuGet.Versioning` dependency. Selection supports exact versions, `^` and `~`
  ranges, NuGet-style bracket ranges (including shortened bounds such as
  `[1.2,2.0)`), and floating patterns such as `1.*`, `1.*-*`, and
  `1.2.0-rc.*`. Bare three-part versions are exact. It selects the lowest
  applicable version except for floating versions, which select the highest
  match. Prereleases are considered when requested in the graph; floating
  patterns include them only when their pattern opts in.
- Restore uses `targets` (or the requested `--target` override) and Microsoft's
  .NET 10 portable RID graph to select the nearest available `bin/<rid>/`
  subtree for each target. It defaults to the current process RID when the
  project has no targets. If no compatible binary exists for a target, shared
  sources still restore and package binaries are skipped for it.
- Restore validates archive entry paths, rejects duplicate paths and symbolic
  links, and extracts to a staging directory before replacing an existing package.
- Package archives are capped at 512 MiB compressed, 100,000 entries, 512 MiB
  per file, and 2 GiB expanded across the package. HTTP downloads and web uploads
  enforce the same compressed-size cap.
- When dependencies change, restore removes obsolete package versions that were
  recorded by the previous lock file. Untracked content under `.packages/` is kept.
- Restored CX sources are visible to the current compiler's recursive scan.
  `targets` is now part of the compiler project model. Native toolchain builds,
  dependency binary linking, and package assets are not implemented yet.
