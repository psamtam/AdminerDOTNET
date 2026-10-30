using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace AdminerDOTNET.Services;

public static class ExternalConnectionConfiguration
{
    public static void Add(IConfigurationBuilder configuration, string baseDirectory)
    {
        // Insert after appsettings JSON, ahead of User Secrets, environment, and command-line providers.
        var position = configuration.Sources.Select((source,index) => (source,index))
            .Where(pair => pair.source is JsonConfigurationSource json &&
                Path.GetFileName(json.Path ?? "").StartsWith("appsettings.",StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.index).DefaultIfEmpty(-1).Max() + 1;
        var path = Path.Combine(baseDirectory,"connection.json");
        var source = new JsonConfigurationSource
        {
            FileProvider = new PhysicalFileProvider(baseDirectory), Path = "connection.json",
            Optional = true, ReloadOnChange = false,
            OnLoadException = _ => throw new InvalidOperationException($"Cannot read '{path}'. Check file permissions and valid JSON, then restart the app.")
        };
        configuration.Sources.Insert(position,source);
    }
}
