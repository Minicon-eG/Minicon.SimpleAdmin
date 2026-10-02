using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.WebUI.Services;

namespace Minicon.SimpleAdmin.WebUI;

/// <summary>
/// Extension methods that wire up the complete SimpleAdmin admin UI (controllers,
/// views, REST API and services) into a host ASP.NET Core application. A host
/// application only needs:
/// <code>
/// var builder = WebApplication.CreateBuilder(args);
/// builder.Services.AddSimpleAdminWebUI(builder.Configuration);
/// var app = builder.Build();
/// app.UseSimpleAdminWebUI();
/// app.Run();
/// </code>
/// All UI logic lives in this Razor Class Library; the host contributes only
/// configuration (appsettings.json with <c>ConfigPath</c>, <c>StatusDirectory</c>,
/// optionally <c>BasePath</c>).
/// </summary>
public static class SimpleAdminWebUIExtensions
{
    private const string StatusHttpClientName = "SimpleAdminStatus";

    /// <summary>
    /// Directory holding config.json: the "ConfigPath" setting, or — when empty — the application
    /// directory itself (e.g. the IIS app folder C:\inetpub\wwwroot\SimpleAdmin).
    /// </summary>
    internal static string ResolveConfigDirectory(IConfiguration configuration)
    {
        var configPath = configuration.GetValue<string>("ConfigPath");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configPath) ? AppContext.BaseDirectory : configPath);
    }

    /// <summary>
    /// Registers MVC, the embedded SimpleAdmin controllers/views and all backing
    /// services (configuration management, status reading, acknowledges).
    /// </summary>
    public static IServiceCollection AddSimpleAdminWebUI(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllersWithViews(options =>
        {
            options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
        })
        // Make the controllers compiled into this RCL discoverable by the host.
        .AddApplicationPart(typeof(SimpleAdminWebUIExtensions).Assembly);

        // Secure the admin UI. Every controller/view route requires an authenticated
        // Windows user (Negotiate/Kerberos) via a global fallback policy — so the
        // SSRF-capable test endpoints (/api/sqlquerytest, /api/emailtest) and the
        // config CRUD are never reachable anonymously. Static files (incl. the
        // status/*.json the notifier and peer servers pull over HTTP) are served by
        // UseStaticFiles BEFORE the auth middleware and stay publicly accessible.
        // Secure by default; a host without a Windows-auth environment can opt out
        // with "Authentication:Enabled": false in appsettings.json.
        if (IsAuthenticationEnabled(configuration))
        {
            services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
                    .AddNegotiate();

            var requiredRole = configuration.GetValue<string>("Authentication:RequiredRole");
            services.AddAuthorizationBuilder()
                    .SetFallbackPolicy(BuildFallbackPolicy(requiredRole));
        }

        // Register ConfigurationService as Singleton (in-memory config management)
        services.AddSingleton<ConfigurationService>();

        // Register StatusReaderService - read status directory from config or use default
        var statusDirectory = Path.GetFullPath(
            configuration.GetValue<string>("StatusDirectory")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "status"));
        services.AddHttpClient(StatusHttpClientName, c => c.Timeout = TimeSpan.FromSeconds(5));
        services.AddSingleton(sp =>
            new StatusReaderService(
                statusDirectory,
                sp.GetRequiredService<IAcknowledgeService>(),
                sp.GetRequiredService<ConfigurationService>(),
                sp.GetRequiredService<ILogger<StatusReaderService>>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(StatusHttpClientName)));

        // Register AcknowledgeService - store acknowledges alongside config.json
        var acknowledgeDirectory = ResolveConfigDirectory(configuration);
        services.AddSingleton<IAcknowledgeService>(sp =>
            new AcknowledgeService(acknowledgeDirectory, sp.GetRequiredService<ILogger<AcknowledgeService>>()));

        return services;
    }

    /// <summary>
    /// Loads the configured <c>config.json</c>, configures the request pipeline
    /// (base path for IIS sub-applications, forwarded headers, static files,
    /// routing) and maps the default controller route.
    /// </summary>
    public static WebApplication UseSimpleAdminWebUI(this WebApplication app)
    {
        // Auto-load config.json from the configured directory
        var startupConfigService = app.Services.GetRequiredService<ConfigurationService>();
        var configPath = ResolveConfigDirectory(app.Configuration);
        {
            var configFilePath = Path.GetFullPath(Path.Combine(configPath, "config.json"));
            if (File.Exists(configFilePath))
            {
                try
                {
                    var json = File.ReadAllText(configFilePath);
                    startupConfigService.LoadFromFile(json, configFilePath);
                    app.Logger.LogInformation("Configuration loaded from {ConfigFilePath}", configFilePath);
                }
                catch (System.Text.Json.JsonException)
                {
                    startupConfigService.LoadError = $"'{configFilePath}' enthält kein gültiges JSON.";
                    app.Logger.LogWarning("Invalid JSON in configuration file {ConfigFilePath}", configFilePath);
                }
                catch (Exception ex)
                {
                    startupConfigService.LoadError = $"'{configFilePath}' konnte nicht gelesen werden: {ex.Message}";
                    app.Logger.LogWarning(ex, "Failed to load configuration from {ConfigFilePath}", configFilePath);
                }
            }
            else
            {
                startupConfigService.LoadError = $"Datei nicht gefunden: '{configFilePath}'";
                app.Logger.LogWarning("config.json not found in ConfigPath directory '{ConfigPath}'", configPath);
            }
        }

        // Configure base path for IIS subdirectory deployment (e.g., "/SimpleAdmin")
        // Priority: 1. ASPNETCORE_PATHBASE env var, 2. BasePath config setting
        var basePath = Environment.GetEnvironmentVariable("ASPNETCORE_PATHBASE")
                       ?? app.Configuration.GetValue<string>("BasePath");

        if (!string.IsNullOrEmpty(basePath))
        {
            // Normalize basePath (ensure it starts with / and doesn't end with /)
            basePath = "/" + basePath.Trim('/');

            // UsePathBase strips the prefix from Request.Path AND moves it into
            // Request.PathBase. The previous hand-rolled middleware only set PathBase
            // without stripping the prefix, so endpoint routing kept matching against
            // "/<basePath>/..." and every request (incl. static assets) returned 404.
            app.UsePathBase(basePath);
        }

        // Forward headers from reverse proxy (IIS, nginx, etc.)
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        });

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Home/Error");
        }

        // Serve this RCL's static web assets (CSS/JS/fonts under
        // _content/Minicon.SimpleAdmin.WebUI/...). UseStaticFiles is intentionally
        // used instead of the .NET 9+ MapStaticAssets(): it only needs the physical
        // wwwroot/_content files (no endpoint manifest that must be deployed in sync),
        // it honours UsePathBase for IIS sub-application deployments, and it avoids the
        // pre-compression/ETag edge cases that produced empty/404 responses behind
        // proxies. Must run before UseRouting so assets short-circuit the pipeline.
        app.UseStaticFiles();

        app.UseRouting();

        // Authentication must run after routing and before authorization. When the
        // fallback policy is active (see AddSimpleAdminWebUI) an unauthenticated
        // request to any endpoint is challenged for Windows credentials here.
        if (IsAuthenticationEnabled(app.Configuration))
        {
            app.UseAuthentication();
            app.Logger.LogInformation(
                "SimpleAdmin WebUI: Windows authentication (Negotiate) is ENABLED — all endpoints require an authenticated user.");
        }
        else
        {
            app.Logger.LogWarning(
                "SimpleAdmin WebUI: authentication is DISABLED (Authentication:Enabled=false) — every endpoint, incl. the SQL/email connection tests, is publicly reachable.");
        }

        app.UseAuthorization();

        app.MapControllerRoute(
            name: "default",
            pattern: "{controller=Home}/{action=Index}/{id?}");

        return app;
    }

    /// <summary>
    /// Authentication is on unless a host explicitly opts out via
    /// <c>Authentication:Enabled = false</c> (secure by default).
    /// </summary>
    private static bool IsAuthenticationEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool?>("Authentication:Enabled") ?? true;

    /// <summary>
    /// Builds the global fallback authorization policy: every request must carry an
    /// authenticated Windows identity, optionally restricted to a single AD role
    /// (<c>Authentication:RequiredRole</c>, e.g. <c>DOMAIN\SimpleAdmin-Admins</c>).
    /// </summary>
    private static AuthorizationPolicy BuildFallbackPolicy(string? requiredRole)
    {
        var builder = new AuthorizationPolicyBuilder(NegotiateDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser();

        if (!string.IsNullOrWhiteSpace(requiredRole))
            builder.RequireRole(requiredRole.Trim());

        return builder.Build();
    }
}
