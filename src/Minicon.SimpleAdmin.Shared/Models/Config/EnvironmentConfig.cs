using System;
using System.Collections.Generic;
using System.Linq;

namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Environment configuration defining which servers belong to each service type
/// </summary>
public class EnvironmentConfig
{
    /// <summary>
    /// Gets or sets the list of transfer server names in this environment
    /// </summary>
    public List<string> Transfer { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of services server names in this environment
    /// </summary>
    public List<string> Services { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of biztalk server names in this environment
    /// </summary>
    public List<string> Biztalk { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of database server names in this environment
    /// </summary>
    public List<string> Database { get; set; } = new();

    /// <summary>
    /// Gets all server names in this environment
    /// </summary>
    public IEnumerable<string> GetAllServerNames()
    {
        return Transfer
            .Concat(Services)
            .Concat(Biztalk)
            .Concat(Database)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
