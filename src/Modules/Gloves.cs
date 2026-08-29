using Armory.Data;
using Armory.Services;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;

namespace Armory.Modules;

internal class Gloves : IArmoryService
{
    private readonly InterfaceBridge _bridge;
    private readonly IPlayerCache    _cache;
    private readonly ILogger<Gloves> _logger;

    private readonly unsafe delegate* unmanaged<nint, byte, void> CCSPlayerPawn_SetGlovesBodyGroup;

    public Gloves(InterfaceBridge bridge, IPlayerCache cache, ILogger<Gloves> logger)
    {
        _bridge = bridge;
        _cache  = cache;
        _logger = logger;

        unsafe
        {
            CCSPlayerPawn_SetGlovesBodyGroup = (delegate* unmanaged<IntPtr, byte, void>) FindSetGlovesBodyGroup();
        }
    }

    public bool Init()
    {
        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);

        return true;
    }

    public void Shutdown()
    {
        _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);
    }

    private void OnPlayerSpawnPost(IPlayerSpawnForwardParams @params)
    {
        var client = @params.Client;

        if (client.IsFakeClient)
        {
            return;
        }

        var pawn = @params.Pawn;

        if (_cache.GetLoadoutItem(client, pawn.Team, CosmeticSlot.Gloves) is not { } glovesDef
            || _cache.GetWeaponSkin(client, glovesDef) is not { } cosmetics)
        {
            return;
        }

        pawn.GiveGloves((EconGlovesId) glovesDef, cosmetics.PaintId, cosmetics.Wear, cosmetics.Seed);

        unsafe
        {
            if (CCSPlayerPawn_SetGlovesBodyGroup is null)
            {
                _logger.LogWarning("SetGlovesBodyGroup not found, gloves may not render correctly");

                return;
            }

            CCSPlayerPawn_SetGlovesBodyGroup(pawn.GetAbsPtr(), 0);
        }
    }

    /// <summary>
    ///     CCSPlayerPawn::SetGlovesBodyGroup has no symbol; locate it by finding the single read-only
    ///     reference to the "first_or_third_person" string token in server.dll.
    /// </summary>
    private nint FindSetGlovesBodyGroup()
    {
        var server = _bridge.ModuleManager.Server;
        var token  = _bridge.ModSharp.MakeStringToken("first_or_third_person");

        var tokenAddress = server.FindData(BitConverter.GetBytes(token), false);

        if (tokenAddress == nint.Zero)
        {
            _logger.LogWarning("Cannot find first_or_third_person token (0x{token:X})", token);

            return nint.Zero;
        }

        var refs = server.GetReferencesFromPointer(tokenAddress);

        // Group by the FUNCTION the reference lives in, not by the reference itself. Several
        // reads of the token inside one function are normal, and counting raw references made
        // this bail with "expected one reader but got 6" after a CS2 update - so the pointer
        // stayed null and gloves quietly stopped rendering correctly on spawn.
        Dictionary<nint, nint> byFunction = [];

        foreach (var @ref in refs)
        {
            if (!server.GetFunctionRange(@ref, out var start, out _))
            {
                continue;
            }

            var (isRead, isWritten) = MemoryUtilities.AnalyzeInstructionAccess(@ref);

            if (isRead && !isWritten)
            {
                byFunction.TryAdd(start, @ref);
            }
        }

        if (byFunction.Count == 0)
        {
            _logger.LogWarning("No read-only reader of the first_or_third_person token");

            return nint.Zero;
        }

        if (byFunction.Count > 1)
        {
            _logger.LogWarning("first_or_third_person token is read in {count} functions, taking the first",
                               byFunction.Count);
        }

        return byFunction.Values.First();
    }
}
