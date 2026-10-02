# Cxpm.Web

`Cxpm.Web` is the ASP.NET Core host for a file-backed CXPM repository and its
web UI. It targets .NET 10, serves the Vue single-page app from `/`, and exposes
the repository API under `/api`.

## Current endpoints

- `GET /api/` returns basic service metadata.
- `GET /api/health` returns the service health status.
- `GET /api/packages` lists package IDs and published versions for the UI.
- `GET /api/packages/{id}` returns the latest package metadata and its versions.
- `GET /api/{id}/index.json` returns the version index used by `cxpm restore`.
- `GET /api/{id}/{version}/{id}.{version}.zip` downloads a package archive.
- `PUT` to the index and archive routes supports the conditional publish
  protocol used by `cxpm publish`.
- Unknown `/api/*` paths return a JSON 404 response.

Packages are stored in `App_Data/packages/` by default using the existing
static-feed layout. Set `RepositoryStorage__Path` to choose another directory.
Reads are public; publishing requires setting `CXPM_TOKEN` on the server and
using the same bearer token for the `cxpm` client. Version archives are
immutable, uploads validate the ZIP paths and `package.cxpm` identity, and
version index changes require ETag conditional requests. The file store is
intended for one server instance; multi-instance coordination and accounts are
not implemented.

## Container and cloud deployment

Build and run the container locally from the repository root:

```sh
docker compose up --build
```

Open `http://localhost:8181/`. The compose setup persists package data in the
`cxpm-packages` named volume. Set `CXPM_TOKEN` in the environment before running
compose to enable authenticated publishing; without it, reads work and publish
requests are rejected. To stop the app while retaining data, use
`docker compose down`; remove the data as well only with
`docker compose down --volumes`.

The image is provider-neutral and can run in Azure Container Apps/App Service,
AWS ECS/App Runner, or Google Cloud Run. Configure `RepositoryStorage__Path` to
a writable persistent mount. The current file-backed store is intended for a
single app instance; do not scale it horizontally or use ephemeral container
storage for published packages. A database is not required for the current
static-feed protocol. Repository operations now depend on the
`IPackageObjectStorage` interface, and the filesystem implementation remains
single-instance only. Before multi-instance deployment, implement a shared
adapter (for example, Azure Blob Storage, Amazon S3, or Google Cloud Storage)
with atomic create-if-absent and ETag-conditional replacement. Then decide
whether package metadata needs a database. Keep provider SDKs behind this
application-owned interface so the API and CLI feed protocol stay unchanged.

`Cxpm.Web.Tests` runs the API in a Testcontainers-managed app container and
exercises the publish, download, and catalog flows. No database container is
included because the current repository does not use a database. When shared
storage or database adapters are added, integration tests should provision
those dependencies through Testcontainers as well.

Archive limits are 512 MiB compressed, 100,000 entries, 512 MiB per file, and
2 GiB total expanded content. The client applies the same limits during package
build, download, validation, and restore.

## Frontend development

The Vue app is in `clientapp/` and uses pnpm. With Node.js and pnpm installed:

```sh
cd clientapp
pnpm install --frozen-lockfile
pnpm dev
```

Vite serves the SPA on its development port and proxies `/api` to the ASP.NET
Core launch URL, `http://localhost:5150`.

## Production assets

Build the Vue app with:

```sh
cd clientapp
pnpm install --frozen-lockfile
pnpm run build
```

The generated static assets are written to `Cxpm.Web/wwwroot/`. Run the web
project with `dotnet run --project Cxpm.Web` to serve the SPA and API from the
same origin.
