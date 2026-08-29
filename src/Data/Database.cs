using Microsoft.Extensions.Logging;
using SqlSugar;

namespace Armory.Data;

internal class Database
{
    private readonly DatabaseConfig    _config;
    private readonly ILogger<Database> _logger;
    private readonly SqlSugarScope     _db;

    public Database(ArmoryConfig config, ILogger<Database> logger)
    {
        _config = config.Database;
        _logger = logger;
        _db     = CreateScope(_config.ConnectionString);
    }

    public SqlSugarScope Client => _db;

    private static SqlSugarScope CreateScope(string connectionString)
        => new(new ConnectionConfig
        {
            ConnectionString      = connectionString,
            DbType                = DbType.MySql,
            IsAutoCloseConnection = true,
            InitKeyType           = InitKeyType.Attribute,
            MoreSettings = new()
            {
                DisableNvarchar = true,
            },
            LanguageType = LanguageType.English,
        });

    public bool EnsureSchema()
    {
        try
        {
            // the database itself may not exist yet — bootstrap with a server-level connection
            var server = CreateScope(_config.ServerConnectionString);

            server.Ado.ExecuteCommand($"CREATE DATABASE IF NOT EXISTS `{_config.Database}` " +
                                      "CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
            server.Dispose();

            _db.Ado.ExecuteCommand("""
                                   CREATE TABLE IF NOT EXISTS weapon_skins (
                                       steam_id     BIGINT UNSIGNED   NOT NULL,
                                       item_def     INT               NOT NULL,
                                       paint_id     SMALLINT UNSIGNED NOT NULL DEFAULT 0,
                                       wear         FLOAT             NOT NULL DEFAULT 0.000001,
                                       seed         INT               NOT NULL DEFAULT 0,
                                       stattrak     INT               NULL,
                                       name_tag     VARCHAR(64)       NULL,
                                       stickers     JSON              NULL,
                                       keychain     JSON              NULL,
                                       custom_model VARCHAR(255)      NULL,
                                       PRIMARY KEY (steam_id, item_def)
                                   )
                                   """);

            _db.Ado.ExecuteCommand("""
                                   CREATE TABLE IF NOT EXISTS loadouts (
                                       steam_id BIGINT UNSIGNED NOT NULL,
                                       team     TINYINT         NOT NULL,
                                       slot     ENUM('knife','gloves','agent','music','medal') NOT NULL,
                                       item_def INT             NOT NULL,
                                       PRIMARY KEY (steam_id, team, slot)
                                   )
                                   """);

            _db.Ado.ExecuteCommand("""
                                   CREATE TABLE IF NOT EXISTS player_models (
                                       steam_id   BIGINT UNSIGNED NOT NULL,
                                       team       TINYINT         NOT NULL,
                                       model_path VARCHAR(255)    NOT NULL,
                                       PRIMARY KEY (steam_id, team)
                                   )
                                   """);

            _db.Ado.ExecuteCommand("""
                                   CREATE TABLE IF NOT EXISTS precache_models (
                                       model_path VARCHAR(255) NOT NULL PRIMARY KEY
                                   )
                                   """);

            _logger.LogInformation("Schema ready on {host}:{port}/{db}",
                                   _config.Host, _config.Port, _config.Database);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure database schema");

            return false;
        }
    }
}
