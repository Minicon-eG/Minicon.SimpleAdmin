using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Interface for checking Windows Services status
/// </summary>
public interface IWindowsServiceChecker
{
    /// <summary>
    /// Gets the status of all configured Windows Services for a server
    /// </summary>
    Task<Dictionary<string, ServiceState>> CheckServicesAsync(
        List<ServiceCheckConfig> services,
        bool enabled);

    /// <summary>
    /// Gets the status of a single Windows Service
    /// </summary>
    Task<ServiceState> CheckServiceAsync(string serviceName);
}
