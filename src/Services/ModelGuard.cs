using Armory.Data;
using Microsoft.Extensions.Logging;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Listeners;

namespace Armory.Services;

internal interface IModelGuard
{
    /// <summary>True if the model was precached this map and is safe to apply.</summary>
    bool IsSafe(string modelPath);

    /// <summary>SetModel that refuses un-precached paths instead of crashing the server.</summary>
    bool TrySetModel(IBaseEntity entity, string modelPath);

    /// <summary>Re-read the precache set from the DB (applies on next map load).</summary>
    void ReloadFromDatabase();
}

/// <summary>
///     Builds the precache set from the database on every map load (precache_models table plus every
///     model referenced by weapon_skins/player_models/wings) and tracks what was actually precached,
///     so a model added mid-map is rejected with a log line rather than crashing the server.
/// </summary>
internal class ModelGuard : IModelGuard, IArmoryService, IGameListener
{
    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    private readonly InterfaceBridge     _bridge;
    private readonly InventoryRepository _repository;
    private readonly ILogger<ModelGuard> _logger;

    private HashSet<string> _wanted    = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _precached = new(StringComparer.OrdinalIgnoreCase);

    public ModelGuard(InterfaceBridge bridge, InventoryRepository repository, ILogger<ModelGuard> logger)
    {
        _bridge     = bridge;
        _repository = repository;
        _logger     = logger;
    }

    public bool Init()
    {
        try
        {
            _wanted = _repository.GetAllModelPaths().GetAwaiter().GetResult();

            _logger.LogInformation("Loaded {count} model path(s) for precaching", _wanted.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load precache set from database");
        }

        _bridge.ModSharp.InstallGameListener(this);

        return true;
    }

    public void Shutdown()
    {
        _bridge.ModSharp.RemoveGameListener(this);
    }

    public void OnResourcePrecache()
    {
        // re-read at every map load so rows added since boot (or by a migration that
        // finished after Init) are picked up; fall back to the last known set on failure
        try
        {
            _wanted = _repository.GetAllModelPaths().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh precache set from database, using previous set");
        }

        var precached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in _wanted)
        {
            try
            {
                _bridge.ModSharp.PrecacheResource(model);
                precached.Add(model);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to precache {model}: {msg}", model, ex.Message);
            }
        }

        _precached = precached;

        _logger.LogInformation("Precached {count} custom model(s)", precached.Count);
    }

    public bool IsSafe(string modelPath)
        => _precached.Contains(modelPath);

    public bool TrySetModel(IBaseEntity entity, string modelPath)
    {
        if (!IsSafe(modelPath))
        {
            _logger.LogWarning("Refusing SetModel: '{model}' is not precached on this map " +
                               "(added after map load? reload happens on next map change)", modelPath);

            return false;
        }

        entity.SetModel(modelPath);

        return true;
    }

    public void ReloadFromDatabase()
    {
        Task.Run(async () =>
        {
            try
            {
                var wanted = await _repository.GetAllModelPaths().ConfigureAwait(false);

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    _wanted = wanted;
                    _logger.LogInformation("Precache set reloaded: {count} model path(s), effective next map", wanted.Count);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload precache set");
            }
        });
    }
}
