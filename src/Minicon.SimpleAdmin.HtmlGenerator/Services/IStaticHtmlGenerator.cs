using System.Collections.Generic;
using System.Threading.Tasks;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;

namespace Minicon.SimpleAdmin.HtmlGenerator;

/// <summary>
/// Interface for generating static HTML files from runtime status
/// </summary>
public interface IStaticHtmlGenerator
{
    /// <summary>
    /// Gets the wwwroot directory path where HTML files are generated
    /// </summary>
    string WwwrootDirectory { get; }

    /// <summary>
    /// Generates all HTML files
    /// </summary>
    /// <param name="statuses">List of runtime statuses to generate pages for</param>
    /// <param name="config">Configuration object</param>
    /// <param name="currentHostname">Current server hostname (used to determine environment for all.html)</param>
    /// <param name="activeAcknowledges">Active acknowledges used for problem rendering</param>
    Task GenerateAllAsync(
        List<RuntimeStatus> statuses,
        Config config,
        string currentHostname,
        IReadOnlyCollection<Acknowledge> activeAcknowledges);
}
