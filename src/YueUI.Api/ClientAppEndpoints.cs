namespace YueUI.Api;

/// <summary>
/// Serves the built Vue frontend under <see cref="BasePath"/>. Static assets come from the static-file
/// middleware (<c>wwwroot/ui/assets/…</c>); every other path below <c>/ui</c> gets <c>index.html</c>.
/// </summary>
/// <remarks>
/// The build only exists after <c>dotnet publish</c> (or when copied to <c>wwwroot/ui</c>). During development
/// SpaProxy redirects the browser to the Vite dev server instead, which serves <c>/ui</c> itself.
/// </remarks>
public static class ClientAppEndpoints
{
    public const string BasePath = "/ui";

    /// <summary>Browsers use GET; uptime monitors and link checkers often use HEAD. Kestrel omits the body for HEAD.</summary>
    public static readonly string[] GetAndHead = [HttpMethods.Get, HttpMethods.Head];

    public static WebApplication MapClientApp(this WebApplication app)
    {
        app.MapMethods("/", GetAndHead, () => Results.Redirect($"{BasePath}/")).ExcludeFromDescription();
        // "nonfile" leaves paths with a file extension to the static-file middleware, which skips any request
        // that already matched an endpoint. A missing asset thus becomes a 404 instead of the app shell.
        app.MapMethods($"{BasePath}/{{**path:nonfile}}", GetAndHead, ServeIndex).ExcludeFromDescription();
        return app;
    }

    private static IResult ServeIndex(IWebHostEnvironment environment, HttpContext context)
    {
        var index = environment.WebRootFileProvider.GetFileInfo("ui/index.html");
        if (!index.Exists || index.PhysicalPath is null)
        {
            return Results.Problem(
                title: "Web interface not built",
                detail: "Run 'dotnet publish src/YueUI.Api', or during development start the API with 'dotnet run' so that SpaProxy launches the Vite dev server.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Asset file names contain a content hash; only the shell must always be fresh.
        context.Response.Headers.CacheControl = "no-cache";
        return Results.File(index.PhysicalPath, "text/html; charset=utf-8");
    }
}
