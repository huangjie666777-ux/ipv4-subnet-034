namespace SubnetPlanner.Api;

public static class IpMath
{
    public static bool TryParseIPv4(string? text, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('.');
        if (parts.Length != 4) return false;
        uint result = 0;
        foreach (var part in parts)
        {
            if (part.Length == 0 || part.Length > 3) return false;
            if (part.Length > 1 && part[0] == '0') return false;
            if (!part.All(char.IsAsciiDigit)) return false;
            if (!int.TryParse(part, out var octet) || octet > 255) return false;
            result = (result << 8) | (uint)octet;
        }
        value = result;
        return true;
    }

    public static string Format(uint value) =>
        string.Join('.', (value >> 24) & 0xFF, (value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);

    public static uint PrefixMask(int prefix) =>
        prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);

    public static ulong BlockSize(int prefix) => 1UL << (32 - prefix);

    public static bool IsAligned(uint address, int prefix) => (address & ~PrefixMask(prefix)) == 0;
}
