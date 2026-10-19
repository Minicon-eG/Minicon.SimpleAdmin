using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.HttpSys;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Minicon.SimpleAdmin.WebUI.Hosting;

namespace Minicon.SimpleAdmin.WebUI;

/// <summary>
/// Reusable bootstrapper for the SimpleAdmin WebUI. Wraps the install/uninstall/start/stop
/// subcommands, the Windows-service host detection, HttpSys configuration (for Negotiate
/// without IIS) and the normal WebApplication build. A host application needs only:
/// <code>
/// return await SimpleAdminWebUIHost.RunAsync(args);
/// </code>
/// </summary>
public static class SimpleAdminWebUIHost
{
    /// <summary>
    /// Runs the WebUI end-to-end. Handles the <c>install</c>/<c>uninstall</c>/<c>start</c>/<c>stop</c>
    /// subcommands synchronously before building the ASP.NET Core host.
    /// </summary>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, _ => { });

    /// <summary>
    /// Overload for host applications that want to tweak the <see cref="WebApplicationBuilder"/>
    /// (e.g. add extra config sources) before <see cref="SimpleAdminWebUIExtensions.AddSimpleAdminWebUI"/>
    /// runs.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, Action<WebApplicationBuilder> configure)
    {
        if (args.Length > 0 && TryGetInstallerCommand(args[0], out var command))
        {
            var exe = Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
            return ServiceInstaller.Dispatch(command, args.Skip(1).ToArray(), exe, Console.Out);
        }

        var isWindowsService = OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService();

        var builderOptions = new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = isWindowsService ? AppContext.BaseDirectory : null
        };
        var builder = WebApplication.CreateBuilder(builderOptions);

        if (isWindowsService)
        {
            builder.Host.UseWindowsService(o => o.ServiceName = InstallerOptions.DefaultServiceName);

            // Running outside IIS: use HttpSys so Negotiate still works. Port comes from
            // the "Urls" config (ASPNETCORE_URLS or appsettings.json Kestrel/Urls) or
            // falls back to http://*:5080.
            var urls = builder.Configuration["Urls"];
            var listenPrefix = string.IsNullOrWhiteSpace(urls)
                ? $"http://*:{InstallerOptions.DefaultPort}/"
                : urls!;

#pragma warning disable CA1416 // isWindowsService above already guarantees we are on Windows.
            builder.WebHost.UseHttpSys(options =>
            {
                options.Authentication.Schemes = AuthenticationSchemes.Negotiate | AuthenticationSchemes.NTLM;
                options.Authentication.AllowAnonymous = true; // auth gating happens in the pipeline
                foreach (var prefix in listenPrefix.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    options.UrlPrefixes.Add(prefix);
            });
#pragma warning restore CA1416
        }

        builder.Services.AddSimpleAdminWebUI(builder.Configuration);
        configure(builder);

        var app = builder.Build();
        app.UseSimpleAdminWebUI();
        await app.RunAsync();
        return 0;
    }

    private static bool TryGetInstallerCommand(string first, out string command)
    {
        command = first.ToLowerInvariant();
        return command is "install" or "uninstall" or "start" or "stop";
    }
}
