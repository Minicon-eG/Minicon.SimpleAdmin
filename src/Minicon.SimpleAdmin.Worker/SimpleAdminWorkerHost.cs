using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.HtmlGenerator;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.Services.PlatformMetrics;
using System.IO.Abstractions;

namespace Minicon.SimpleAdmin.Worker;

/// <summary>
/// Reusable bootstrapper for the SimpleAdmin console worker. Wraps the full
/// dependency-injection wiring and run loop so a host application only needs a
/// one-line <c>Main</c>:
/// <code>
/// await SimpleAdminWorkerHost.RunAsync(args);
/// </code>
/// All monitoring logic lives in the Minicon.SimpleAdmin.* packages; the host
/// application contributes only configuration files (appsettings.json,
/// config.json) and deployment-specific paths.
/// </summary>
public static class SimpleAdminWorkerHost
{
    /// <summary>
    /// Parsed command-line/host paths used to bootstrap the worker.
    /// </summary>
    public sealed record HostPaths(string ConfigPath, string StatusDirectory, string WwwrootDirectory, bool ForceProbe);

    /// <summary>
    /// Parses the worker's positional arguments and the <c>--force-probe</c> flag.
    /// Argument order: [configPath] [statusDirectory] [wwwrootDirectory].
    /// The central notifier is config-driven (features.notifications.notifierServers) — no flag needed.
    /// </summary>
    public static HostPaths ParseArgs(string[] args)
    {
        var defaultWwwroot = OperatingSystem.IsWindows() ? @"c:\inetpub\wwwroot" : "wwwroot";
        var forceProbe = args.Any(a => string.Equals(a, "--force-probe", StringComparison.OrdinalIgnoreCase));
        var positionalArgs = args.Where(a => !string.Equals(a, "--force-probe", StringComparison.OrdinalIgnoreCase)).ToArray();
        var configPath = positionalArgs.Length > 0 ? positionalArgs[0] : "config.json";
        var statusDirectory = positionalArgs.Length > 1 ? positionalArgs[1] : "status";
        var wwwrootDirectory = positionalArgs.Length > 2 ? positionalArgs[2] : defaultWwwroot;
        return new HostPaths(configPath, statusDirectory, wwwrootDirectory, forceProbe);
    }

    /// <summary>
    /// Registers every SimpleAdmin service into the supplied service collection.
    /// Exposed so host applications can add their own services or override
    /// individual registrations before building the host.
    /// </summary>
    public static IServiceCollection AddSimpleAdmin(
        this IServiceCollection services,
        IConfiguration configuration,
        HostPaths paths)
    {
        // Register file system abstraction
        services.AddSingleton<IFileSystem, FileSystem>();

        // Register services with factory methods to inject path parameters
        services.AddSingleton<IServiceConfigReader>(sp =>
            new ServiceConfigReader(paths.ConfigPath, sp.GetRequiredService<ILogger<ServiceConfigReader>>(), sp.GetRequiredService<IFileSystem>()));

        // Register platform-specific metrics provider
        services.AddSingleton<IPlatformMetricsProvider>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<IPlatformMetricsProvider>>();
            var provider = PlatformMetricsProviderFactory.Create(logger);
            provider.InitializeAsync().GetAwaiter().GetResult();
            return provider;
        });

        services.AddSingleton<IMetricsCollector, MetricsCollector>();
        services.AddSingleton<IStatusEvaluator, StatusEvaluator>();

        // Register AcknowledgeService - store acknowledges alongside the config file
        var configDirRaw = Path.GetDirectoryName(paths.ConfigPath);
        var configDirectory = Path.GetFullPath(string.IsNullOrEmpty(configDirRaw) ? "." : configDirRaw);
        services.AddSingleton<IAcknowledgeService>(sp =>
            new AcknowledgeService(configDirectory, sp.GetRequiredService<ILogger<AcknowledgeService>>()));

        // Register checkers
        services.AddSingleton<IAppPoolChecker, AppPoolChecker>();
        services.AddSingleton<IWindowsServiceChecker, WindowsServiceChecker>();
        services.AddSingleton<IEventLogChecker, EventLogChecker>();

        // Register Certificate checker
        services.AddSingleton<ICertificateChecker, CertificateChecker>();

        // Register BizTalk checker (Management API, integrated Windows auth)
        services.AddSingleton<IBizTalkApiClient>(sp =>
            new BizTalkApiClient(
                new HttpClient(new HttpClientHandler { UseDefaultCredentials = true }) { Timeout = TimeSpan.FromSeconds(30) },
                sp.GetRequiredService<ILogger<BizTalkApiClient>>()));
        services.AddSingleton<IBizTalkChecker, BizTalkChecker>();

        // Register file-share / log monitoring checker
        services.AddSingleton<IFileSystemAccess, FileSystemAccess>();
        services.AddSingleton<IFileMonitoringChecker, FileShareChecker>();

        // Register SQL Query checker (pass encryption key if configured)
        services.AddSingleton<ISqlQueryExecutor>(sp =>
            new DefaultSqlQueryExecutor(sp.GetRequiredService<ILogger<DefaultSqlQueryExecutor>>()));
        services.AddSingleton<SqlQueryChecker>(sp =>
        {
            var encKey = configuration["Encryption:ConnectionStringKey"] ?? "";
            return new SqlQueryChecker(
                sp.GetRequiredService<ILogger<SqlQueryChecker>>(),
                sp.GetRequiredService<ISqlQueryExecutor>(),
                encKey);
        });

        // Register date/time abstraction (production: real clock)
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();

        // Register email monitoring services
        services.AddSingleton<IEmailProbeSender>(sp =>
            new EmailProbeSender(
                sp.GetRequiredService<ILogger<EmailProbeSender>>(),
                sp.GetRequiredService<IDateTimeProvider>(),
                paths.StatusDirectory));
        services.AddSingleton<IEmailDeliveryChecker>(sp =>
            new EmailDeliveryChecker(
                sp.GetRequiredService<ILogger<EmailDeliveryChecker>>(),
                sp.GetRequiredService<IDateTimeProvider>()));

        // Register central notification services (--notify mode)
        services.AddSingleton<ISmtpMailSender, SmtpMailSender>();
        services.AddSingleton<NotificationStateStore>();
        services.AddSingleton<IHttpStatusReader>(sp =>
            new HttpStatusReader(
                new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
                sp.GetRequiredService<ILogger<HttpStatusReader>>()));
        services.AddSingleton<INotificationService>(sp =>
            new NotificationService(
                sp.GetRequiredService<ILogger<NotificationService>>(),
                sp.GetRequiredService<IHttpStatusReader>(),
                sp.GetRequiredService<NotificationStateStore>(),
                sp.GetRequiredService<ISmtpMailSender>(),
                sp.GetRequiredService<IDateTimeProvider>(),
                paths.StatusDirectory,
                configuration["Encryption:ConnectionStringKey"] ?? "",
                Environment.MachineName,
                paths.WwwrootDirectory));

        services.AddSingleton<IRuntimeStatusStore>(sp =>
            new RuntimeStatusStore(paths.StatusDirectory, sp.GetRequiredService<ILogger<RuntimeStatusStore>>()));

        services.AddSingleton<IStaticHtmlGenerator>(sp =>
            new StaticHtmlGenerator(
                paths.WwwrootDirectory,
                sp.GetRequiredService<ILogger<StaticHtmlGenerator>>()));

        // Register the application worker
        services.AddTransient<SimpleAdminWorker>();
        services.AddSingleton(new RuntimeOptions { ForceProbe = paths.ForceProbe });

        return services;
    }

    /// <summary>
    /// Builds a fully configured <see cref="IHost"/> for the worker.
    /// Host applications can call this when they need to customise the host
    /// (e.g. add extra configuration sources) before running.
    /// </summary>
    public static IHost BuildHost(string[] args)
    {
        var paths = ParseArgs(args);

        return Host.CreateDefaultBuilder(args)
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            })
            .ConfigureServices((context, services) =>
            {
                services.AddSimpleAdmin(context.Configuration, paths);
            })
            .Build();
    }

    /// <summary>
    /// Runs the worker end-to-end: builds the host, executes a single check
    /// cycle and exits. Returns the process exit code (0 on success, 1 on a
    /// fatal error). This is the single entry point a host application needs.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        var paths = ParseArgs(args);
        using var host = BuildHost(args);

        var logger = host.Services.GetRequiredService<ILogger<SimpleAdminWorker>>();
        logger.LogInformation("Minicon.SimpleAdmin starting... ForceProbe={ForceProbe}", paths.ForceProbe);

        try
        {
            var worker = host.Services.GetRequiredService<SimpleAdminWorker>();
            await worker.RunAsync();
            logger.LogInformation("Minicon.SimpleAdmin completed successfully");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "FATAL ERROR occurred");
            return 1;
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
