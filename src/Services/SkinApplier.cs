using Sharp.Shared.GameObjects;

namespace Armory.Services;

/// <summary>
///     Writes econ attributes onto a weapon's item view - paint, wear, seed, stickers, keychains.
/// </summary>
internal interface ISkinApplier
{
    /// <summary>Set one named econ attribute. Every skin property goes through this.</summary>
    void SetAttribute(IEconItemView view, ReadOnlySpan<byte> name, float value);

    /// <summary>
    ///     Give the view an identity of its own. Without this the client treats the item as the
    ///     player's real inventory entry and ignores the attributes below it.
    /// </summary>
    void ClaimItem(IEconItemView view, uint accountId);

    /// <summary>Paint, wear and pattern seed - the three that make a finish.</summary>
    void ApplyPaint(IEconItemView view, int paintId, float wear, int seed);
}

/// <summary>
///     The attribute-writing primitives, extracted so the loadout system and the in-game arsenal
///     browser share ONE implementation.
///     <br /><br />
///     This is the riskiest code in the plugin: it resolves a raw engine function
///     (<c>CAttributeList::SetOrAddAttributeValueByName</c>) from gamedata and writes through a
///     schema offset into <c>CEconItemView::m_NetworkedDynamicAttributes</c>. Duplicating it would
///     mean two copies to keep in step with every CS2 update, so it lives here and nowhere else.
/// </summary>
internal sealed class SkinApplier : ISkinApplier, IArmoryService
{
    // ReSharper disable InconsistentNaming
    private readonly int CEconItemView_m_NetworkedDynamicAttributesOffset;

    private readonly unsafe delegate* unmanaged<nint, byte*, float, void>
        CAttributeList_SetOrAddAttributeValueByName;
    // ReSharper restore InconsistentNaming

    /// <summary>
    ///     A counter, not a real inventory id. Each previewed or equipped item needs a DISTINCT
    ///     high id or the client caches the first one it saw and later changes appear to do nothing.
    /// </summary>
    private static uint _fakeItemIdHigh = 16384;

    public SkinApplier(InterfaceBridge bridge)
    {
        CEconItemView_m_NetworkedDynamicAttributesOffset
            = bridge.SchemaManager.GetNetVarOffset("CEconItemView", "m_NetworkedDynamicAttributes");

        unsafe
        {
            CAttributeList_SetOrAddAttributeValueByName
                = (delegate* unmanaged<nint, byte*, float, void>) bridge.ModSharp.GetGameData()
                    .GetAddress("CAttributeList::SetOrAddAttributeValueByName");
        }
    }

    public bool Init()
        => true;

    public unsafe void SetAttribute(IEconItemView view, ReadOnlySpan<byte> name, float value)
    {
        fixed (byte* ptr = name)
        {
            CAttributeList_SetOrAddAttributeValueByName(
                view.GetAbsPtr() + CEconItemView_m_NetworkedDynamicAttributesOffset,
                ptr,
                value);
        }
    }

    public void ClaimItem(IEconItemView view, uint accountId)
    {
        view.SetAccountIdLocal(accountId);
        view.SetItemIdLowLocal(uint.MaxValue);
        view.SetItemIdHighLocal(_fakeItemIdHigh++);
    }

    public void ApplyPaint(IEconItemView view, int paintId, float wear, int seed)
    {
        SetAttribute(view, "set item texture prefab"u8, paintId);
        SetAttribute(view, "set item texture wear"u8, wear);
        SetAttribute(view, "set item texture seed"u8, seed);
    }
}
