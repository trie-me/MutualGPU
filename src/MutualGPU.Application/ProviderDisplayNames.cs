using System.Security.Cryptography;
using System.Text;

namespace MutualGPU.Application;

/// <summary>Creates a stable support call sign without retaining the provider passcode.</summary>
public static class ProviderDisplayNames
{
    private static readonly string[] Adjectives =
    [
        "Amber", "Arctic", "Azure", "Bold", "Bright", "Bronze", "Calm", "Clever",
        "Copper", "Coral", "Cosmic", "Daring", "Emerald", "Fleet", "Gentle", "Golden",
        "Indigo", "Ivory", "Jade", "Keen", "Lunar", "Merry", "Nimble", "Noble",
        "Rapid", "Scarlet", "Silver", "Solar", "Steady", "Swift", "Verdant", "Vivid",
    ];

    private static readonly string[] Animals =
    [
        "Badger", "Bear", "Bison", "Crane", "Falcon", "Fox", "Gecko", "Heron",
        "Ibex", "Koala", "Lynx", "Mantis", "Marten", "Moose", "Otter", "Owl",
        "Panda", "Panther", "Raven", "Seal", "Shark", "Sparrow", "Stag", "Tiger",
        "Tortoise", "Viper", "Weasel", "Wolf", "Wombat", "Yak", "Zebra", "Kestrel",
    ];

    public static string Create(string presharedKey)
    {
        if (String.IsNullOrWhiteSpace(presharedKey)) throw new ArgumentException("A provider passcode is required.", nameof(presharedKey));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(presharedKey));
        var prefix = presharedKey[..Math.Min(4, presharedKey.Length)];
        return $"{Adjectives[digest[0] % Adjectives.Length]} {Animals[digest[1] % Animals.Length]} · {prefix}";
    }
}
