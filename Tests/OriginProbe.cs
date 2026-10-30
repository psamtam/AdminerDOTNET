using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

// Browser-only diagnostic; no database connections or commands.
public static class OriginProbe
{
    public static async Task RunAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:5187");
        await using var app = builder.Build();
        app.MapGet("/", async context =>
        {
            context.Response.Headers["Referrer-Policy"] = context.Request.Query["policy"] == "same-origin" ? "same-origin" : "no-referrer";
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html><title>Local Origin probe</title><body><h1>Native form Origin probe</h1><form method='post' action='/echo'><button>Submit probe</button></form></body></html>");
        });
        app.MapPost("/echo", async context =>
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync($"<html><title>Origin result</title><body><h1>Origin: {System.Net.WebUtility.HtmlEncode(context.Request.Headers.Origin.ToString())}</h1><p>Fetch site: {System.Net.WebUtility.HtmlEncode(context.Request.Headers["Sec-Fetch-Site"].ToString())}</p></body></html>");
        });
        Console.WriteLine("Origin probe at http://127.0.0.1:5187 — no database access.");
        await app.RunAsync();
    }
}
