using Cxpm.Web;
using Microsoft.AspNetCore.Mvc;

namespace Microsoft.AspNetCore.Builder;

public static class RepositoryEndpoints
{
    public static RouteGroupBuilder MapRepositoryEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/packages", async (CxpmRepositoryStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.ListPackagesAsync(cancellationToken)));

        api.MapGet("/packages/{packageId}", async (string packageId, CxpmRepositoryStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var details = await store.GetPackageDetailsAsync(packageId, cancellationToken);
                return details is null ? Results.NotFound() : Results.Ok(details);
            }
            catch (RepositoryRequestException exception)
            {
                return Error(exception);
            }
        });

        api.MapGet("/{packageId}/index.json", async (string packageId, HttpContext context,
            CxpmRepositoryStore store, CancellationToken cancellationToken) =>
        {
            try
            {
                var index = await store.GetIndexAsync(packageId, cancellationToken);
                if (index is null)
                    return Results.NotFound();
                context.Response.Headers.ETag = index.ETag.ToString();
                return Results.Bytes(index.Json, "application/json");
            }
            catch (RepositoryRequestException exception)
            {
                return Error(exception);
            }
        });

        api.MapPut("/{packageId}/index.json", async (string packageId, PackageVersionIndex index,
            HttpContext context, CxpmRepositoryStore store, CancellationToken cancellationToken) =>
        {
            if (RequireWriteAuthorization(context, store) is { } rejection)
                return rejection;
            try
            {
                var ifNoneMatch = context.Request.Headers.IfNoneMatch.Any(value => value == "*");
                var ifMatch = context.Request.Headers.IfMatch.ToString();
                var stored = await store.StoreIndexAsync(packageId, index.Versions ?? [], ifMatch,
                    ifNoneMatch, cancellationToken);
                context.Response.Headers.ETag = stored.ETag.ToString();
                return Results.NoContent();
            }
            catch (RepositoryRequestException exception)
            {
                return Error(exception);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(1024 * 1024));

        api.MapGet("/{packageId}/{version}/{archiveName}", async (string packageId, string version,
            string archiveName, CxpmRepositoryStore store, CancellationToken cancellationToken) =>
        {
            try
            {
                var stream = await store.OpenArchiveAsync(packageId, version, archiveName, cancellationToken);
                return stream is null
                    ? Results.NotFound()
                    : Results.Stream(stream, "application/zip");
            }
            catch (RepositoryRequestException exception)
            {
                return Error(exception);
            }
        });

        api.MapPut("/{packageId}/{version}/{archiveName}", async (string packageId, string version,
            string archiveName, HttpContext context, CxpmRepositoryStore store,
            CancellationToken cancellationToken) =>
        {
            if (RequireWriteAuthorization(context, store) is { } rejection)
                return rejection;
            try
            {
                var ifNoneMatch = context.Request.Headers.IfNoneMatch.Any(value => value == "*");
                await store.StoreArchiveAsync(packageId, version, archiveName, context.Request.Body,
                    context.Request.ContentLength, ifNoneMatch, cancellationToken);
                return Results.Created($"/api/{Uri.EscapeDataString(packageId.ToLowerInvariant())}/{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(archiveName)}", null);
            }
            catch (RepositoryRequestException exception)
            {
                return Error(exception);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(512L * 1024 * 1024));

        return api;
    }

    private static IResult? RequireWriteAuthorization(HttpContext context, CxpmRepositoryStore store)
    {
        if (!store.IsWriteAuthorizationConfigured)
            return Results.Problem("Set CXPM_TOKEN on the server before enabling package publishing.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        return store.IsWriteAuthorized(context.Request.Headers.Authorization)
            ? null
            : Results.Unauthorized();
    }

    private static IResult Error(RepositoryRequestException exception) =>
        Results.Problem(exception.Message, statusCode: exception.StatusCode);
}
