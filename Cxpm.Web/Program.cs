using Cxpm.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IPackageObjectStorage, FileSystemPackageObjectStorage>();
builder.Services.AddSingleton<CxpmRepositoryStore>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");
api.MapGet("/", () => Results.Ok(new
{
    name = "cxpm.web",
    version = "0.1.0",
    health = "/api/health",
}));
api.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "cxpm.web",
}));
api.MapRepositoryEndpoints();

app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new { error = "API route not found." });
        return;
    }

    var indexPath = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");
    if (!File.Exists(indexPath))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("The Cxpm.Web frontend has not been built yet.");
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(indexPath);
});

app.Run();

public partial class Program { }
