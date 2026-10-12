using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Minicon.SimpleAdmin.WebUI.Services.Scripts;

/// <summary>
/// Guards every script-runner endpoint: the feature must not be switched off in the host's
/// appsettings (<c>ScriptRunner:Enabled</c>, default on) and, if <c>ScriptRunner:RequiredRole</c> is set,
/// the user must be member of that AD group.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ScriptRunnerGateAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var options = context.HttpContext.RequestServices.GetRequiredService<ScriptRunnerOptions>();
        string? reason = null;
        var status = StatusCodes.Status404NotFound;

        if (!options.Enabled)
        {
            reason = options.DisabledReason ?? "Die Skript-Ausführung ist auf diesem Host abgeschaltet.";
        }
        else if (!string.IsNullOrWhiteSpace(options.RequiredRole))
        {
            var user = context.HttpContext.User;
            if (user.Identity?.IsAuthenticated != true || !user.IsInRole(options.RequiredRole.Trim()))
            {
                reason = $"Für die Skript-Ausführung ist die Gruppe '{options.RequiredRole}' erforderlich.";
                status = StatusCodes.Status403Forbidden;
            }
        }

        if (reason == null) return;

        if (context.HttpContext.Request.Path.StartsWithSegments("/api"))
        {
            context.Result = new ObjectResult(new { message = reason }) { StatusCode = status };
            return;
        }

        var viewData = new ViewDataDictionary(
            context.HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Mvc.ModelBinding.IModelMetadataProvider>(),
            context.ModelState)
        {
            ["Reason"] = reason
        };
        context.Result = new ViewResult { ViewName = "~/Views/Scripts/Disabled.cshtml", ViewData = viewData, StatusCode = status };
    }
}
