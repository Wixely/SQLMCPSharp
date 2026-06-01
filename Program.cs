using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;
using SQLMCPSharp.Configuration;
using SQLMCPSharp.Hosting;
using SQLMCPSharp.Services;

namespace SQLMCPSharp;

public static class Program
{
    public static int Main(string[] args)
    {
        var contentRoot = GetContentRoot();
        var isService = WindowsServiceHelpers.IsWindowsService();
        if (!isService)
        {
            McpSharpIcon.ApplyConsoleWindowIcon();
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(contentRoot, "logs", "sqlmcp-bootstrap-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .CreateBootstrapLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = contentRoot,
            });

            builder.Configuration
                .SetBasePath(contentRoot)
                .AddJsonFile(ResolveConfigFile(contentRoot, "appsettings.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, "appsettings.Local.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, "SQLMCPSharp.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, $"SQLMCPSharp.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, "SQLMCPSharp.Local.json"), optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .AddEnvironmentVariables(prefix: "SQLMCP_")
                .AddCommandLine(args);

            if (isService)
            {
                var svcOptions = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();
                builder.Host.UseWindowsService(o => o.ServiceName = svcOptions.WindowsServiceName);
            }

            builder.Host.UseSerilog((ctx, services, cfg) => cfg
                .ReadFrom.Configuration(ctx.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext());

            builder.Services.Configure<SqlOptions>(builder.Configuration.GetSection(SqlOptions.SectionName));
            builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));
            builder.Services.AddSingleton<SqlServerRegistry>();

            // Conditional registration: each engine's tool class is only loaded into the MCP
            // server when its bundle is Enabled. Keeps the tool surface presented to the agent
            // tightly scoped — e.g. a SQLite-only deployment doesn't advertise 60+ mssql/mysql/
            // oracle tools the agent would never be allowed to call.
            var sqlCfg = builder.Configuration.GetSection(SqlOptions.SectionName).Get<SqlOptions>() ?? new SqlOptions();
            var mcpBuilder = builder.Services.AddMcpServer().WithHttpTransport();
            if (sqlCfg.Mssql.Enabled)  mcpBuilder.WithTools<Tools.MssqlTools>();
            if (sqlCfg.Mysql.Enabled)  mcpBuilder.WithTools<Tools.MysqlTools>();
            if (sqlCfg.Sqlite.Enabled) mcpBuilder.WithTools<Tools.SqliteTools>();
            if (sqlCfg.Oracle.Enabled) mcpBuilder.WithTools<Tools.OracleTools>();

            var server = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();
            builder.WebHost.ConfigureKestrel(k =>
            {
                if (string.Equals(server.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    k.ListenLocalhost(server.Port);
                }
                else if (IPAddress.TryParse(server.Host, out var ip))
                {
                    k.Listen(ip, server.Port);
                }
                else
                {
                    k.ListenAnyIP(server.Port);
                }
            });

            var app = builder.Build();
            app.UseSerilogRequestLogging();

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception in AppDomain");
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error(e.Exception, "Unobserved task exception");
                e.SetObserved();
            };

            var reg = app.Services.GetRequiredService<SqlServerRegistry>();
            LogStartup(
                "SQLMCPSharp",
                $"http://{server.Host}:{server.Port}{server.Path}",
                "HTTP",
                isService ? "WindowsService" : "Console",
                contentRoot,
                $"Read-only: {reg.IsReadOnly}",
                $"Allow dangerous: {reg.AllowDangerous}",
                $"MSSQL : {(reg.IsEnabled(SqlEngine.Mssql)  ? $"enabled ({reg.ListAliases(SqlEngine.Mssql).Count} server(s))"  : "disabled")}",
                $"MySQL : {(reg.IsEnabled(SqlEngine.Mysql)  ? $"enabled ({reg.ListAliases(SqlEngine.Mysql).Count} server(s))"  : "disabled")}",
                $"SQLite: {(reg.IsEnabled(SqlEngine.Sqlite) ? $"enabled ({reg.ListAliases(SqlEngine.Sqlite).Count} server(s))" : "disabled")}",
                $"Oracle: {(reg.IsEnabled(SqlEngine.Oracle) ? $"enabled ({reg.ListAliases(SqlEngine.Oracle).Count} server(s))" : "disabled")}");

            app.UseMiddleware<McpPasswordMiddleware>();
            app.MapFavicon();
            app.MapGet("/healthz", () => new
            {
                status = "ok",
                server = "SQLMCPSharp",
                path = server.Path,
                readOnly = reg.IsReadOnly,
                engines = new
                {
                    mssql = new { enabled = reg.IsEnabled(SqlEngine.Mssql), count = reg.ListAliases(SqlEngine.Mssql).Count },
                    mysql = new { enabled = reg.IsEnabled(SqlEngine.Mysql), count = reg.ListAliases(SqlEngine.Mysql).Count },
                    sqlite = new { enabled = reg.IsEnabled(SqlEngine.Sqlite), count = reg.ListAliases(SqlEngine.Sqlite).Count },
                    oracle = new { enabled = reg.IsEnabled(SqlEngine.Oracle), count = reg.ListAliases(SqlEngine.Oracle).Count },
                },
                timeUtc = DateTimeOffset.UtcNow,
            });
            app.MapMcp(server.Path);

            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Server terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void LogStartup(string serviceName, string endpoint, string transport, string mode, string contentRoot, params string[] details)
    {
        var startupLog = Log.ForContext("SourceContext", serviceName + ".Startup");
        startupLog.Information("{ServiceName} startup", serviceName);
        startupLog.Information("  Endpoint: {Endpoint}", endpoint);
        startupLog.Information("  Transport: {Transport}", transport);
        startupLog.Information("  Mode: {Mode}", mode);
        foreach (var detail in details) startupLog.Information("  {Detail}", detail);
        startupLog.Information("  Content root: {ContentRoot}", contentRoot);
    }

    private static string GetContentRoot() =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    private static string ResolveConfigFile(string contentRoot, string fileName)
    {
        if (File.Exists(Path.Combine(contentRoot, fileName))) return fileName;
        try
        {
            var match = Directory.EnumerateFiles(contentRoot, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(p => string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase));
            return match is null ? fileName : Path.GetFileName(match);
        }
        catch (DirectoryNotFoundException) { return fileName; }
    }
}
