using System.Net;

namespace NB.Core.Net;

/// <summary>
/// Room codes for NB multiplayer: the host's reachable IPv4 address and room-server port, plus 16 bits of the host's
/// compatibility fingerprint (so an obviously different game install is recognised before connecting), encoded in
/// Crockford base32 as "NB-XXXX-XXXX-XXXXX". Relay = port + 1.
/// </summary>
public static class RoomCode
{
    const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(IPAddress ip, int port, ushort compatTag)
    {
        var b = ip.MapToIPv4().GetAddressBytes();
        ulong v = (ulong)b[0] << 56 | (ulong)b[1] << 48 | (ulong)b[2] << 40 | (ulong)b[3] << 32 | (ulong)(ushort)port << 16 | compatTag;
        // 64 bits + 1 check digit = 13 + 1 symbols
        var chars = new char[13];
        ulong t = v;
        for (int i = 12; i >= 0; i--) { chars[i] = Alphabet[(int)(t & 31)]; t >>= 5; }
        char check = Alphabet[(int)(v % 31)];
        var s = new string(chars) + check;
        return $"NB-{s[..4]}-{s[4..8]}-{s[8..]}";
    }

    /// <summary>Steam room code "NBS-XXXX-XXXX-XXXX-XXXXX": the host's SteamID64 (connection through Steam's relay
    /// network, no port forwarding) + 16-bit compatibility tag, 80 bits in 16 symbols + check digit.</summary>
    public static string EncodeSteam(ulong steamId, ushort compatTag)
    {
        var chars = new char[16];
        ulong hi = steamId >> 48, lo = steamId << 16 | compatTag;   // 80-bit value = hi:16 | lo:64
        System.Numerics.BigInteger v = ((System.Numerics.BigInteger)hi << 64) | lo, t = v;
        for (int i = 15; i >= 0; i--) { chars[i] = Alphabet[(int)(t & 31)]; t >>= 5; }
        char check = Alphabet[(int)(v % 31)];
        var s = new string(chars) + check;
        return $"NBS-{s[..4]}-{s[4..8]}-{s[8..12]}-{s[12..]}";
    }

    public static bool TryDecodeSteam(string code, out ulong steamId, out ushort compatTag)
    {
        steamId = 0; compatTag = 0;
        var s = new string(code.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (!s.StartsWith("NBS")) return false;
        s = s[3..].Replace('O', '0').Replace('I', '1').Replace('L', '1');
        if (s.Length != 17) return false;
        System.Numerics.BigInteger v = 0;
        for (int i = 0; i < 16; i++)
        {
            int d = Alphabet.IndexOf(s[i]);
            if (d < 0) return false;
            v = v << 5 | d;
        }
        if (Alphabet[(int)(v % 31)] != s[16]) return false;
        compatTag = (ushort)(v & 0xFFFF);
        steamId = (ulong)((v >> 16) & ulong.MaxValue);
        return steamId != 0;
    }

    public static bool TryDecode(string code, out IPAddress ip, out int port, out ushort compatTag)
    {
        ip = IPAddress.None; port = 0; compatTag = 0;
        var s = new string(code.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (s.StartsWith("NB")) s = s[2..];
        s = s.Replace('O', '0').Replace('I', '1').Replace('L', '1');
        if (s.Length != 14) return false;
        ulong v = 0;
        for (int i = 0; i < 13; i++)
        {
            int d = Alphabet.IndexOf(s[i]);
            if (d < 0) return false;
            v = v << 5 | (uint)d;
        }
        if (Alphabet[(int)(v % 31)] != s[13]) return false;
        ip = new IPAddress(new[] { (byte)(v >> 56), (byte)(v >> 48), (byte)(v >> 40), (byte)(v >> 32) });
        port = (int)(v >> 16 & 0xFFFF); compatTag = (ushort)(v & 0xFFFF);
        return port > 0;
    }
}
