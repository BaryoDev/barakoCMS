using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// How widely a sensitivity level exposes a value. The enum's numbers are stored, so they cannot
/// say this: Members was appended after Hidden and is less restricted than Sensitive and Hidden.
/// Compare levels through this, never with <c>&lt;</c> or <c>&gt;</c> on the enum.
/// </summary>
internal static class SensitivityRank
{
    public static int Exposure(SensitivityLevel level) => level switch
    {
        SensitivityLevel.Hidden => 0,
        SensitivityLevel.Sensitive => 1,
        SensitivityLevel.Members => 2,
        SensitivityLevel.Public => 3,
        _ => 0,
    };

    /// <summary>True when moving from one level to the other lets more callers read the value.</summary>
    public static bool IsLowering(SensitivityLevel from, SensitivityLevel to) => Exposure(to) > Exposure(from);

    /// <summary>A level everyone it applies to reads alike, so a role list or a mask on it would mean nothing.</summary>
    public static bool IsUniform(SensitivityLevel level) => level is SensitivityLevel.Public or SensitivityLevel.Members;
}
