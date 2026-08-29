using Microsoft.Extensions.Configuration;

namespace Armory;

/// <summary>
///     Where the inventory lives. Nothing here is secret in itself: the real values come from
///     <c>game/sharp/configs/arsenal.jsonc</c> on the server, which is not in this repository
///     and should never be, because it carries the password in plain text.
/// </summary>
internal class DatabaseConfig
{
    public string Host     { get; set; } = "127.0.0.1";
    public int    Port     { get; set; } = 3306;
    public string Database { get; set; } = "arsenal";
    public string User     { get; set; } = "root";
    public string Password { get; set; } = string.Empty;

    public string ConnectionString
        => $"Server={Host};Port={Port};Database={Database};User ID={User};Password={Password};";

    /// <summary>
    ///     Names no database on purpose. The schema bootstrap has to connect BEFORE the database
    ///     exists so it can create it, and a connection string naming a database that is not there
    ///     fails outright. A blank MySQL instance is therefore enough to start from.
    /// </summary>
    public string ServerConnectionString
        => $"Server={Host};Port={Port};User ID={User};Password={Password};";
}

internal class ArmoryConfig
{
    public DatabaseConfig Database { get; set; } = new();

    /// <summary>
    ///     Read once at start up, so an edit needs a restart. The file is NOT optional: a missing
    ///     or malformed one stops the module loading rather than quietly falling back to the
    ///     defaults above and connecting somewhere unintended. Defaults only fill in keys the
    ///     file leaves out.
    /// </summary>
    public static ArmoryConfig Load(string sharpPath)
    {
        var configDir = Path.Combine(Path.GetFullPath(sharpPath), "configs");

        var root = new ConfigurationBuilder()
                   .SetBasePath(configDir)
                   .AddJsonFile("arsenal.jsonc", false, false)
                   .Build();

        var config = new ArmoryConfig();
        root.Bind(config);

        return config;
    }
}
