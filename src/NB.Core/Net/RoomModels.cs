using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace NB.Core.Net;

/// <summary>Read-only view of a player registered on the <see cref="RoomServer"/> (one Xenia profile).</summary>
public sealed record RoomPlayer(string Xuid, string Gamertag, string HostAddress, string MachineId, string MacAddress,
    string SessionId, string TitleId, uint State, string RichPresence, DateTime LastSeen);

/// <summary>Read-only view of a session on the <see cref="RoomServer"/>. Slot counts are derived from the member list like the reference server.</summary>
public sealed record RoomSession(string TitleId, string Id, string? HostXuid, string HostAddress, string MacAddress, int Port, int Flags,
    int PublicSlots, int PrivateSlots, int OpenPublicSlots, int OpenPrivateSlots, IReadOnlyList<string> Players, bool Advertised,
    bool Deleted, string? MigratedTo, int PropertyCount, int ContextCount, bool HasXLast, DateTime Updated);

sealed class PlayerState
{
    public const uint DefaultState = 0x13;   // ONLINE | PLAYING | JOINABLE
    public const string NoSession = "0000000000000000";
    public string Xuid = "", Gamertag = "Xenia User", MachineId = "", HostAddress = "", MacAddress = "";
    public int Port = 36000;
    public string SessionId = NoSession, TitleId = "0", RichPresence = "";
    public uint State = DefaultState;
    /// <summary>Title id (8 hex, upper) -> base64 XUSER_SETTING blobs.</summary>
    public Dictionary<string, List<string>> Settings = new(StringComparer.OrdinalIgnoreCase);
    public DateTime LastSeen = DateTime.UtcNow;

    public RoomPlayer Snapshot() => new(Xuid, Gamertag, HostAddress, MachineId, MacAddress, SessionId, TitleId, State, RichPresence, LastSeen);
}

sealed class SessionState
{
    public uint TitleId;
    public string Id = "", Title = "", MediaId = "", Version = "", HostAddress = "", MacAddress = "";
    public string? Xuid, Migration, XLastSrc;
    public int Flags, PublicSlots, PrivateSlots, Port;
    /// <summary>xuid -> joined into a private slot.</summary>
    public OrderedDictionary<string, bool> Players = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Context id (lower-case hex without padding, as the reference stores it) -> value.</summary>
    public OrderedDictionary<string, uint> Context = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Base64 XUSER_PROPERTY blobs (contexts excluded).</summary>
    public List<string> Properties = new();
    public bool Deleted;
    public DateTime Updated = DateTime.UtcNow;
    XLast? _xlast; bool _xlastParsed;

    public string Key => KeyOf(TitleId, Id);
    public static string KeyOf(uint title, string id) => $"{title:X8}/{id}";
    public string TitleHex => TitleId.ToString("X");
    public bool Advertised => (Flags & (SessionFlag.Presence | SessionFlag.Matchmaking)) != 0;
    public bool Matchmaking => (Flags & SessionFlag.Matchmaking) != 0;
    public int OpenPublic => Math.Max(0, PublicSlots - Players.Values.Count(p => !p));
    public int OpenPrivate => Math.Max(0, PrivateSlots - Players.Values.Count(p => p));
    public int FilledPublic => PublicSlots - OpenPublic;
    public int FilledPrivate => PrivateSlots - OpenPrivate;
    public bool IsFull => OpenPublic == 0 && OpenPrivate == 0;
    /// <summary>Visible to searches / the index page: advertised, alive, with contexts and properties (reference repository filter).</summary>
    public bool Listed => Advertised && !Deleted && Migration == null && Context.Count > 0 && Properties.Count > 0;

    /// <summary>Properties followed by the contexts serialised back to property blobs (reference "propertiesStringArray").</summary>
    public List<string> AllProperties() => Properties.Concat(Context.Select(kv => XData.SerializeContext(Convert.ToUInt32(kv.Key, 16), kv.Value))).ToList();

    /// <summary>Host XUID: the GAMER_PUID property when present, otherwise the creator's xuid.</summary>
    public string? HostXuid()
    {
        foreach (var p in Properties)
            if (XData.TryParseProperty(p, out uint id, out _, out var data) && id == XData.GamerPuid)
                return data.Length == 8 ? BinaryPrimitives.ReadUInt64BigEndian(data).ToString("X16") : Xuid;
        return Xuid;
    }

    public XLast? GetXLast(Action<string> log)
    {
        if (!_xlastParsed) { _xlastParsed = true; if (!string.IsNullOrEmpty(XLastSrc)) _xlast = XLast.Parse(XLastSrc, log); }
        return _xlast;
    }
    public void SetXLast(string? src) { XLastSrc = string.IsNullOrEmpty(src) ? null : src; _xlast = null; _xlastParsed = false; }

    public SessionState CloneForMigration(string newId) => new()
    {
        TitleId = TitleId, Id = newId, Title = Title, MediaId = MediaId, Version = Version, Flags = Flags, PublicSlots = PublicSlots, PrivateSlots = PrivateSlots,
        Players = new(Players, StringComparer.OrdinalIgnoreCase), Context = new(Context, StringComparer.OrdinalIgnoreCase), Properties = new(Properties),
        XLastSrc = XLastSrc, Updated = DateTime.UtcNow,
    };

    public RoomSession Snapshot(bool hasQos) => new(TitleHex, Id, HostXuid(), HostAddress, MacAddress, Port, Flags, PublicSlots, PrivateSlots,
        OpenPublic, OpenPrivate, Players.Keys.ToList(), Advertised, Deleted, Migration, Properties.Count, Context.Count, XLastSrc != null, Updated);
}

/// <summary>XGI session flags (reference SessionFlags.ts).</summary>
static class SessionFlag
{
    public const int Host = 1, Presence = 2, Stats = 4, Matchmaking = 8, Arbitration = 16, PeerNetwork = 32, FriendsOnly = 1 << 11;
    const int SingleplayerWithStats = Presence | Stats | (1 << 8) | (1 << 9) | (1 << 10);
    public static bool IsHost(int f) => (f & Host) != 0 || f == Stats || f == SingleplayerWithStats;
    public const uint StateFriendsOnly = 1 << 8;
}

/// <summary>
/// Xenia's serialised XUSER_PROPERTY / XUSER_SETTING blobs (base64 in JSON). Property: id u32 little-endian @0, X_USER_DATA type @4,
/// big-endian value @12 (WSTRING/BINARY payload @20). Setting: id u32 big-endian @0, type @8, value @16 (payload @24).
/// </summary>
static class XData
{
    public const byte TypeContext = 0, TypeInt32 = 1, TypeInt64 = 2, TypeDouble = 3, TypeWString = 4, TypeFloat = 5, TypeBinary = 6, TypeDateTime = 7;
    public const uint GameType = 0x800A, GameMode = 0x800B, GamerPuid = 0x20008107, GamerHostname = 0x40008109, GamercardPictureKey = 0x4064000F;
    /// <summary>Reference server's default gamer picture setting (sent when a title asks for XPROFILE_GAMERCARD_PICTURE_KEY that was never registered).</summary>
    public const string DefaultGamerpic = "QGQADwAAAAAEAAAAAAAAAAAAADIwA5AAAEYARgBGAEUAMAA3AEQAMQAwADAAMAAyADAAMAAwADkAMAAwADAAMQAwADAAMAA5AAA=";

    public static bool TryParseProperty(string b64, out uint id, out byte type, out byte[] data)
    {
        id = 0; type = 0; data = [];
        byte[] b;
        try { b = Convert.FromBase64String(b64); } catch (FormatException) { return false; }
        if (b.Length < 12) return false;
        id = BinaryPrimitives.ReadUInt32LittleEndian(b);
        type = b[4];
        int off = type is TypeWString or TypeBinary ? 20 : 12;
        data = b.Length > off ? b[off..] : [];
        return true;
    }

    public static uint SettingId(string b64)
    {
        var b = Convert.FromBase64String(b64);
        if (b.Length < 16) throw new FormatException("setting blob too short");
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    public static string SerializeContext(uint id, uint value)
    {
        var b = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(b, id);
        b[4] = TypeContext;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), value);
        return Convert.ToBase64String(b);
    }

    /// <summary>Numeric value (long or double) of a property blob, like the reference getParsedValue; null for strings/binary.</summary>
    public static object? Value(byte type, byte[] d) => type switch
    {
        TypeContext when d.Length >= 4 => (long)BinaryPrimitives.ReadUInt32BigEndian(d),
        TypeInt32 when d.Length >= 4 => (long)BinaryPrimitives.ReadInt32BigEndian(d),
        TypeInt64 when d.Length >= 8 => BinaryPrimitives.ReadInt64BigEndian(d),
        TypeDateTime when d.Length >= 8 => (long)BinaryPrimitives.ReadUInt64BigEndian(d),
        TypeDouble when d.Length >= 8 => BinaryPrimitives.ReadDoubleBigEndian(d),
        TypeFloat when d.Length >= 4 => (double)BinaryPrimitives.ReadSingleBigEndian(d),
        _ => null,
    };

    public static object? Value(string b64) => TryParseProperty(b64, out _, out byte t, out var d) ? Value(t, d) : null;

    public static int? Compare(object? a, object? b)
    {
        if (a == null || b == null) return null;
        if (a is long la && b is long lb) return la.CompareTo(lb);
        return Convert.ToDouble(a).CompareTo(Convert.ToDouble(b));
    }

    public static string? WString(string b64) =>
        TryParseProperty(b64, out _, out byte t, out var d) && t == TypeWString ? Encoding.BigEndianUnicode.GetString(d).TrimEnd('\0') : null;
}

/// <summary>The matchmaking part of an XLast (XLAST .xlast project) that Xenia uploads with a session: gzip'd UTF-16LE XML in base64.</summary>
sealed class XLast
{
    public sealed record Filter(uint Left, string LeftType, string Op, uint Right, string RightType);
    public sealed class Query { public long Id; public string Name = ""; public List<Filter>? Filters; public List<uint>? Returns; }
    public readonly Dictionary<long, Query> Queries = new();
    public readonly Dictionary<uint, long> Constants = new();

    public static XLast? Parse(string b64, Action<string> log)
    {
        try
        {
            var raw = Convert.FromBase64String(b64);
            if (raw.Length > 2 && raw[0] == 0x1F && raw[1] == 0x8B)
            {
                using var gz = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
                using var ms = new MemoryStream(); gz.CopyTo(ms); raw = ms.ToArray();
            }
            string xml = raw.Length > 1 && (raw[1] == 0 || (raw[0] == 0xFF && raw[1] == 0xFE)) ? Encoding.Unicode.GetString(raw) : Encoding.UTF8.GetString(raw);
            var doc = XDocument.Parse(xml.TrimStart('﻿'));
            var x = new XLast();
            var mm = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Matchmaking");
            if (mm == null) return x;
            foreach (var c in Kids(Kid(mm, "Constants"), "Constant"))
                if (Num(c, "id") is long id && Num(c, "value") is long v) x.Constants[(uint)id] = v;
            foreach (var q in Kids(Kid(mm, "Queries"), "Query"))
            {
                if (Num(q, "id") is not long qid) continue;
                var query = new Query { Id = qid, Name = (string?)q.Attribute("friendlyName") ?? "" };
                var fl = Kids(Kid(q, "Filters"), "Filter").Select(f => new Filter((uint)(Num(f, "left") ?? 0), (string?)f.Attribute("leftType") ?? "",
                    (string?)f.Attribute("op") ?? "", (uint)(Num(f, "right") ?? 0), (string?)f.Attribute("rightType") ?? "")).ToList();
                if (fl.Count > 0) query.Filters = fl;
                var rl = Kids(Kid(q, "Returns"), "Return").Select(r => (uint)(Num(r, "id") ?? 0)).ToList();
                if (rl.Count > 0) query.Returns = rl;
                x.Queries[qid] = query;
            }
            return x;
        }
        catch (Exception e) { log($"  ! invalid XLast source: {e.Message}"); return null; }
    }

    static XElement? Kid(XElement? e, string name) => e?.Elements().FirstOrDefault(k => k.Name.LocalName == name);
    static IEnumerable<XElement> Kids(XElement? e, string name) => e?.Elements().Where(k => k.Name.LocalName == name) ?? [];
    static long? Num(XElement e, string attr)
    {
        var s = ((string?)e.Attribute(attr))?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return long.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out long h) ? h : null;
        return long.TryParse(s, out long d) ? d : null;
    }
}
