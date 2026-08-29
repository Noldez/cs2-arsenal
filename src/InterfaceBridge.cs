using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;

namespace Armory;

internal interface IArmoryService
{
    bool Init();

    void Shutdown()
    {
    }
}

internal class InterfaceBridge
{
    private readonly ISharedSystem _sharedSystem;

    public InterfaceBridge(string dllPath, string sharpPath, Version version, ISharedSystem sharedSystem)
    {
        DllPath       = dllPath;
        SharpPath     = sharpPath;
        Version       = version;
        _sharedSystem = sharedSystem;

        ModSharp        = sharedSystem.GetModSharp();
        ConVarManager   = sharedSystem.GetConVarManager();
        EventManager    = sharedSystem.GetEventManager();
        ClientManager   = sharedSystem.GetClientManager();
        EntityManager   = sharedSystem.GetEntityManager();
        FileManager     = sharedSystem.GetFileManager();
        HookManager     = sharedSystem.GetHookManager();
        SchemaManager   = sharedSystem.GetSchemaManager();
        TransmitManager = sharedSystem.GetTransmitManager();
        ModuleManager   = sharedSystem.GetLibraryModuleManager();
        EconItemManager = sharedSystem.GetEconItemManager();
        PhysicsQuery    = sharedSystem.GetPhysicsQueryManager();
        ParticleManager = sharedSystem.GetParticleManager();
        SoundManager    = sharedSystem.GetSoundManager();
    }

    public string  DllPath   { get; }
    public string  SharpPath { get; }
    public Version Version   { get; }

    public IModSharp             ModSharp        { get; }
    public IConVarManager        ConVarManager   { get; }
    public IEventManager         EventManager    { get; }
    public IClientManager        ClientManager   { get; }
    public IEntityManager        EntityManager   { get; }
    public IEconItemManager      EconItemManager { get; }
    public IFileManager          FileManager     { get; }
    public IHookManager          HookManager     { get; }
    public ISchemaManager        SchemaManager   { get; }
    public ITransmitManager      TransmitManager { get; }
    public ILibraryModuleManager ModuleManager   { get; }
    public IPhysicsQueryManager  PhysicsQuery    { get; }
    public IParticleManager      ParticleManager { get; }
    public ISoundManager         SoundManager    { get; }

    public ILoggerFactory LoggerFactory => _sharedSystem.GetLoggerFactory();
}
