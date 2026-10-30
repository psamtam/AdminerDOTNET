using AdminerDOTNET.Services;
using System.Globalization;
using System.Net;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
try { ExternalConnectionConfiguration.Add(builder.Configuration, AppContext.BaseDirectory); }
catch (InvalidOperationException ex)
{
    // Do not log JSON parser exceptions: their source excerpts can contain credentials.
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
    return;
}
builder.WebHost.UseUrls("http://127.0.0.1:5186");
// Nested filters are carried in URLs; the application separately limits their decoded size/depth.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestLineSize = 65536);
builder.Services.AddRazorPages();
builder.Services.AddDataProtection();
builder.Services.AddSingleton<DatabaseConnection>();
builder.Services.AddScoped<MetadataService>();
builder.Services.AddScoped<TableService>();
builder.Services.AddSingleton<QueryService>();
var app = builder.Build();
// Freeze the effective connection at startup, including when other configuration providers reload.
_ = app.Services.GetRequiredService<DatabaseConnection>();
// A local database tool must not accept requests forwarded from another machine.
app.Use(async (context, next) =>
{
    var remote = context.Connection.RemoteIpAddress;
    if (remote is null || !IPAddress.IsLoopback(remote))
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsync("Access denied: this database tool only accepts connections from this computer's loopback address.");
        return;
    }
    if (context.Request.Method != "GET" && context.Request.Method != "HEAD")
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site" ||
            (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
              !string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase) ||
              uri.Scheme != context.Request.Scheme)))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Access denied: the form origin does not match this app. Open the app directly, reload the form, and retry from the same address.");
            return;
        }
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    // no-referrer makes browsers send Origin:null on native POST navigation, rejecting valid record forms.
    // same-origin retains local form origins without sending referrers to other origins.
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    context.Response.Headers.CacheControl = "no-cache, no-store";
    context.Response.Headers.Pragma = "no-cache";
    await next();
});
app.UseExceptionHandler("/Error");
app.UseStatusCodePages(async status =>
{
    if (status.HttpContext.Response.StatusCode == 400)
        await status.HttpContext.Response.WriteAsync("The form request was rejected. Reload the page to refresh its verification token, check the entered values, and retry.");
});
app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.Run();
