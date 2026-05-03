using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SomeEngine.Assets;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct AssetGuid(Guid Value)
{
    public static readonly AssetGuid Empty = new(Guid.Empty);
    public static AssetGuid New() => new(Guid.NewGuid());
    public static AssetGuid FromSource(SourceGuid sourceGuid, string subAssetKey)
    {
        if (sourceGuid.IsEmpty || string.IsNullOrWhiteSpace(subAssetKey))
        {
            return Empty;
        }

        byte[] namespaceBytes = sourceGuid.Value.ToByteArray();
        SwapGuidByteOrder(namespaceBytes);
        byte[] nameBytes = Encoding.UTF8.GetBytes(subAssetKey);

        byte[] hash = SHA1.HashData(namespaceBytes.Concat(nameBytes).ToArray());
        byte[] guidBytes = hash[..16];
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        SwapGuidByteOrder(guidBytes);
        return new AssetGuid(new Guid(guidBytes));
    }

    public bool IsEmpty => Value == Guid.Empty;
    public string ToFlatString() => Value.ToString("D");
    public override string ToString() => ToFlatString();
    public static AssetGuid Parse(string value) => new(Guid.Parse(value));

    public static bool TryParse(string? value, out AssetGuid guid)
    {
        bool success = Guid.TryParse(value, out Guid parsed);
        guid = success ? new AssetGuid(parsed) : Empty;
        return success;
    }

    private static void SwapGuidByteOrder(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }
}

[StructLayout(LayoutKind.Sequential)]
public readonly record struct SourceGuid(Guid Value)
{
    public static readonly SourceGuid Empty = new(Guid.Empty);
    public static SourceGuid New() => new(Guid.NewGuid());
    public bool IsEmpty => Value == Guid.Empty;
    public string ToFlatString() => Value.ToString("D");
    public override string ToString() => ToFlatString();
    public static SourceGuid Parse(string value) => new(Guid.Parse(value));

    public static bool TryParse(string? value, out SourceGuid guid)
    {
        bool success = Guid.TryParse(value, out Guid parsed);
        guid = success ? new SourceGuid(parsed) : Empty;
        return success;
    }
}

public readonly record struct AssetRef<TAsset>(AssetGuid Id)
    where TAsset : class, IAsset
{
    public static AssetRef<TAsset> Empty => new(AssetGuid.Empty);
    public bool IsEmpty => Id.IsEmpty;
}
