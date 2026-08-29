namespace Armory.Modules;

/// <summary>Econ attribute names for sticker/keychain slots ("sticker slot N ...").</summary>
internal static class StickerSchemas
{
    internal readonly ref struct Sticker(
        ReadOnlySpan<byte> id,
        ReadOnlySpan<byte> wear,
        ReadOnlySpan<byte> scale,
        ReadOnlySpan<byte> rot,
        ReadOnlySpan<byte> x,
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> schema)
    {
        public readonly ReadOnlySpan<byte> Id       = id;
        public readonly ReadOnlySpan<byte> Wear     = wear;
        public readonly ReadOnlySpan<byte> Scale    = scale;
        public readonly ReadOnlySpan<byte> Rotation = rot;
        public readonly ReadOnlySpan<byte> OffsetX  = x;
        public readonly ReadOnlySpan<byte> OffsetY  = y;

        /// <summary>
        ///     Integer marker the client reads to decide HOW to interpret the slot. Without it the
        ///     scale/rotation/offset values may simply be ignored.
        /// </summary>
        public readonly ReadOnlySpan<byte> Schema = schema;
    }

    internal readonly ref struct Keychain(
        ReadOnlySpan<byte> id,
        ReadOnlySpan<byte> x,
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> z,
        ReadOnlySpan<byte> seed)
    {
        public readonly ReadOnlySpan<byte> Id      = id;
        public readonly ReadOnlySpan<byte> OffsetX = x;
        public readonly ReadOnlySpan<byte> OffsetY = y;
        public readonly ReadOnlySpan<byte> OffsetZ = z;
        public readonly ReadOnlySpan<byte> Seed    = seed;
    }

    public static Sticker Get(int slot)
    {
        return slot switch
        {
            0 => new("sticker slot 0 id"u8, "sticker slot 0 wear"u8, "sticker slot 0 scale"u8, "sticker slot 0 rotation"u8, "sticker slot 0 offset x"u8, "sticker slot 0 offset y"u8, "sticker slot 0 schema"u8),
            1 => new("sticker slot 1 id"u8, "sticker slot 1 wear"u8, "sticker slot 1 scale"u8, "sticker slot 1 rotation"u8, "sticker slot 1 offset x"u8, "sticker slot 1 offset y"u8, "sticker slot 1 schema"u8),
            2 => new("sticker slot 2 id"u8, "sticker slot 2 wear"u8, "sticker slot 2 scale"u8, "sticker slot 2 rotation"u8, "sticker slot 2 offset x"u8, "sticker slot 2 offset y"u8, "sticker slot 2 schema"u8),
            3 => new("sticker slot 3 id"u8, "sticker slot 3 wear"u8, "sticker slot 3 scale"u8, "sticker slot 3 rotation"u8, "sticker slot 3 offset x"u8, "sticker slot 3 offset y"u8, "sticker slot 3 schema"u8),
            4 => new("sticker slot 4 id"u8, "sticker slot 4 wear"u8, "sticker slot 4 scale"u8, "sticker slot 4 rotation"u8, "sticker slot 4 offset x"u8, "sticker slot 4 offset y"u8, "sticker slot 4 schema"u8),
            _ => default,
        };
    }

    public static Keychain GetKeychain(int slot)
    {
        return slot switch
        {
            0 => new("keychain slot 0 id"u8, "keychain slot 0 offset x"u8, "keychain slot 0 offset y"u8, "keychain slot 0 offset z"u8, "keychain slot 0 seed"u8),
            _ => default,
        };
    }
}
