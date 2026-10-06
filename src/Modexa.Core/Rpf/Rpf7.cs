namespace Modexa.Core.Rpf;

/// <summary>RPF7 archive constants (GTA V). Clean-room, from public format documentation.</summary>
public static class Rpf7
{
    public const uint Version = 0x52504637;      // "RPF7"
    public const uint EncryptionNone = 0x00000000;
    public const uint EncryptionOpen = 0x4E45504F; // "OPEN" — unencrypted TOC, what OpenIV/OpenRPF accept
    public const uint EncryptionAes = 0x0FFFFFF9;
    public const uint EncryptionNg = 0x0FEFFFFF;

    public const uint DirectoryIdentifier = 0x7FFFFF00;
    public const int BlockSize = 512;
    public const int EntrySize = 16;

    public static long AlignUp(long value, long align) => (value + align - 1) / align * align;
}
