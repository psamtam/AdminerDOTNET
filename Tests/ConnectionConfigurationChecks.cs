using System.Text.Json;
using AdminerDOTNET.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

public static class ConnectionConfigurationChecks
{
    public static void Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"AdminerConfig_"+Guid.NewGuid().ToString("N"));
        var executable=Path.Combine(root,"exe");
        var settings=Path.Combine(root,"settings");
        var elsewhere=Path.Combine(root,"elsewhere");
        Directory.CreateDirectory(executable); Directory.CreateDirectory(settings); Directory.CreateDirectory(elsewhere);
        var prefix="ADMINER_CONFIG_TEST_"+Guid.NewGuid().ToString("N")+"_";
        var envKey=prefix+"ConnectionStrings__DefaultConnection";
        var originalDirectory=Directory.GetCurrentDirectory();
        static string Value(string database)=>$"Server=(localdb)\\NeverOpened;Database={database};Integrated Security=true";
        static void Write(string path,string database)=>File.WriteAllText(path,JsonSerializer.Serialize(new { ConnectionStrings=new { DefaultConnection=Value(database) } }));
        try
        {
            Write(Path.Combine(settings,"appsettings.json"),"AppSettings");
            Write(Path.Combine(settings,"appsettings.Development.json"),"EnvironmentJson");
            Write(Path.Combine(settings,"secrets.json"),"UserSecrets");
            Write(Path.Combine(executable,"connection.json"),"ExternalFile");
            Write(Path.Combine(elsewhere,"connection.json"),"WrongWorkingDirectory");
            Environment.SetEnvironmentVariable(envKey,Value("EnvironmentVariable"));
            ConfigurationRoot Build(int tier)
            {
                var builder=new ConfigurationBuilder().SetBasePath(settings).AddJsonFile("appsettings.json",false,false).AddJsonFile("appsettings.Development.json",false,false);
                if(tier>=1) builder.AddJsonFile("secrets.json",false,false);
                if(tier>=2) builder.AddEnvironmentVariables(prefix);
                if(tier>=3) builder.AddCommandLine(["--ConnectionStrings:DefaultConnection="+Value("CommandLine")]);
                ExternalConnectionConfiguration.Add(builder,executable);
                check(!builder.Sources.OfType<JsonConfigurationSource>().Single(source=>source.Path=="connection.json").ReloadOnChange,"External connection file has no hot reload");
                return (ConfigurationRoot)builder.Build();
            }
            Directory.SetCurrentDirectory(elsewhere);
            using(var config=Build(0))
            {
                var connection=new DatabaseConnection(config);
                check(connection.DatabaseName=="ExternalFile","Connection file resolves beside executable independently of working directory");
                Write(Path.Combine(executable,"connection.json"),"EditedFile");
                check(connection.DatabaseName=="ExternalFile","Editing external JSON does not change a running connection");
                config.Reload();
                check(connection.DatabaseName=="ExternalFile","Connection snapshot survives explicit configuration-provider reload");
                check(new DatabaseConnection(config).DatabaseName=="EditedFile","Restart/new connection snapshot picks up edited file");
            }
            foreach(var (tier,expected) in new[]{(1,"UserSecrets"),(2,"EnvironmentVariable"),(3,"CommandLine")})
            {
                using var config=Build(tier);
                check(new DatabaseConnection(config).DatabaseName==expected,"Connection precedence: "+expected);
            }
            File.Delete(Path.Combine(executable,"connection.json"));
            using(var config=Build(1)) check(new DatabaseConnection(config).DatabaseName=="UserSecrets","Missing connection file permits explicit override");
            using(var config=(ConfigurationRoot)new ConfigurationBuilder().Build())
            {
                try { new DatabaseConnection(config).Settings(); check(false,"Missing connection guidance"); }
                catch(InvalidOperationException ex) { check(ex.Message.Contains("connection.json") && ex.Message.Contains("restart"),"Missing connection names external file and restart"); }
            }
            File.WriteAllText(Path.Combine(executable,"connection.json"),"{\"ConnectionStrings\":{\"DefaultConnection\":\"DO_NOT_LOG_SECRET\",\"broken\"}}");
            try { using var invalid=Build(0); check(false,"Malformed connection file must fail"); }
            catch(InvalidOperationException ex) { check(ex.Message.Contains("connection.json") && !ex.ToString().Contains("DO_NOT_LOG_SECRET"),"Malformed external JSON error contains no source/credential excerpt"); }
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            Environment.SetEnvironmentVariable(envKey,null);
            var absolute=Path.GetFullPath(root);
            if(!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected test cleanup path.");
            Directory.Delete(absolute,true);
        }
    }
}
