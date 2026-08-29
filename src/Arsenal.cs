using System.Runtime.CompilerServices;
using Armory.Data;
using Armory.Modules;
using Armory.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Extensions.CommandManager;
using Sharp.Shared;
using Sharp.Shared.Abstractions;

[assembly: DisableRuntimeMarshalling]

namespace Armory;

/// <summary>
///     The module entry point. ModSharp constructs this, calls <see cref="Init" /> once the game
///     is up, and <see cref="Shutdown" /> on unload.
///     <br /><br />
///     Everything is a singleton behind <see cref="IArmoryService" />, and <b>registration order is
///     init order</b>. That matters more than it looks: the schema has to exist before anything
///     queries it, the catalogue has to be built before the browser can list anything, and the
///     player cache has to be up before the give hook asks it what a player owns.
/// </summary>
public class Arsenal : IModSharpModule
{
    string IModSharpModule.DisplayName   => "Arsenal";
    string IModSharpModule.DisplayAuthor => "Noldez";

    private readonly ServiceProvider   _serviceProvider;
    private readonly ILogger<Arsenal>  _logger;

    public Arsenal(ISharedSystem  sharedSystem,
                   string         dllPath,
                   string         sharpPath,
                   Version        version,
                   IConfiguration coreConfiguration,
                   bool           hotReload)
    {
        var loggerFactory = sharedSystem.GetLoggerFactory();
        _logger = loggerFactory.CreateLogger<Arsenal>();

        var bridge = new InterfaceBridge(dllPath, sharpPath, version, sharedSystem);
        var config = ArmoryConfig.Load(sharpPath);

        var services = new ServiceCollection();

        services.AddSingleton(bridge);
        services.AddSingleton(config);
        services.AddSingleton(loggerFactory);
        services.AddSingleton(sharedSystem);
        services.AddLogging();
        services.AddCommandManager(sharedSystem);   // gives ArsenalMenu its !skins client command

        services.AddSingleton<Database>();
        services.AddSingleton<InventoryRepository>();

        // first, because everything below it reads or writes those tables
        services.AddSingleton<IArmoryService, SchemaBootstrap>();

        services.AddSingleton<SkinApplier>();
        services.AddSingleton<ISkinApplier>(x => x.GetRequiredService<SkinApplier>());
        services.AddSingleton<IArmoryService>(x => x.GetRequiredService<SkinApplier>());

        services.AddSingleton<ModelGuard>();
        services.AddSingleton<IModelGuard>(x => x.GetRequiredService<ModelGuard>());
        services.AddSingleton<IArmoryService>(x => x.GetRequiredService<ModelGuard>());

        services.AddSingleton<PlayerCache>();
        services.AddSingleton<IPlayerCache>(x => x.GetRequiredService<PlayerCache>());
        services.AddSingleton<IArmoryService>(x => x.GetRequiredService<PlayerCache>());

        services.AddSingleton<GameFiles>();
        services.AddSingleton<IGameFiles>(x => x.GetRequiredService<GameFiles>());
        services.AddSingleton<IArmoryService>(x => x.GetRequiredService<GameFiles>());

        // reads items_game.txt and csgo_english.txt through GameFiles, so it comes after it
        services.AddSingleton<SchemaCatalogue>();
        services.AddSingleton<ISchemaCatalogue>(x => x.GetRequiredService<SchemaCatalogue>());
        services.AddSingleton<IArmoryService>(x => x.GetRequiredService<SchemaCatalogue>());

        services.AddSingleton<SqlArsenalStore>();
        services.AddSingleton<IArsenalStore>(x => x.GetRequiredService<SqlArsenalStore>());
        services.AddSingleton<IArmoryService>(x => x.GetRequiredService<SqlArsenalStore>());

        // the appliers: these put a saved choice on the item the game hands out
        services.AddSingleton<IArmoryService, WeaponSkins>();
        services.AddSingleton<IArmoryService, Gloves>();
        services.AddSingleton<IArmoryService, MusicKits>();
        services.AddSingleton<IArmoryService, Medals>();

        // and the browser itself, last, because it depends on all of the above
        services.AddSingleton<IArmoryService, ArsenalMenu>();

        _serviceProvider = services.BuildServiceProvider();
    }

    public bool Init()
    {
        foreach (var service in _serviceProvider.GetServices<IArmoryService>())
        {
            if (!service.Init())
            {
                _logger.LogError("Failed to init {service}", service.GetType().Name);

                return false;
            }

            _logger.LogInformation("{service} initialized", service.GetType().Name);
        }

        _serviceProvider.LoadAllSharpExtensions();

        return true;
    }

    public void Shutdown()
    {
        // reverse order, so nothing is torn down while something else still depends on it
        foreach (var service in _serviceProvider.GetServices<IArmoryService>().Reverse())
        {
            try
            {
                service.Shutdown();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error shutting down {service}", service.GetType().Name);
            }
        }

        _serviceProvider.ShutdownAllSharpExtensions();
    }

    /// <summary>Creates the database and its tables before anything queries them.</summary>
    private class SchemaBootstrap : IArmoryService
    {
        private readonly Database _database;

        public SchemaBootstrap(Database database)
            => _database = database;

        public bool Init()
            => _database.EnsureSchema();
    }
}
