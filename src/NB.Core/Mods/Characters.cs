namespace NB.Core.Mods;

/// <summary>A playable character of the Character Select mod (coop/research/charsel/chars.json).</summary>
/// <param name="Index">Sent in co-op packets (0 = Banjo).</param>
/// <param name="TownOnly">Its data is in Showdown Town's bundle only: elsewhere the player is Banjo.</param>
public sealed record Character(int Index, string Key, string Name, uint Actor, uint Model, uint AnimTable, bool TownOnly);

/// <summary>
/// Character Select: the characters, and the exe mod mailbox ("charsel", <see cref="ExePatches.CharacterSelect"/>):
/// [<see cref="MailboxTown"/>] = actor id used in Showdown Town only, [<see cref="MailboxAny"/>] = actor id used in every
/// level, both 0 = Banjo. A change applies at the next player spawn (new game / continue, garage to town, entering a world).
/// </summary>
public static class Characters
{
    public const uint MailboxTown = 0x82FBFD00, MailboxAny = 0x82FBFD04;
    public const uint Hook = 0x8251CA98, HookWord = 0x48848D68;
    /// <summary>The Character Select mod's id (an edition that has it can play every character).</summary>
    public const string ModId = "character-select";

    public static readonly IReadOnlyList<Character> All = new Character[]
    {
        new(0, "banjo", "Banjo", 0x1F041D5E, 0x04041D5E, 0x20041D5E, false),
        new(1, "tux", "Tuxedo Banjo", 0x1F4D1FA1, 0x040DDF84, 0x20041D5E, false),
        new(2, "robot", "Robot Banjo", 0x1F915116, 0x044CE7A0, 0x20041D5E, false),
        new(3, "kazooie", "Kazooie (floating backpack)", 0x1F0526D0, 0x040526D0, 0x20041D5E, false),
        new(4, "mumbo", "Mumbo Jumbo", 0x1FA07D33, 0x04B07E16, 0x20A09A7E, false),
        new(5, "grunty", "Gruntilda", 0x1FDEBA0B, 0x048F7E4F, 0x20AE87C1, false),
        new(6, "log", "L.O.G.", 0x1F6EE147, 0x04491F1A, 0x201BCA39, false),
        new(7, "thomas", "Trophy Thomas", 0x1F4E43B5, 0x041F87F1, 0x203E7E7F, true),
        new(8, "klungo", "Klungo", 0x1FF7A750, 0x04A66314, 0x20879A9A, true),
        new(9, "mrfit", "Mr. Fit", 0x1FB3BA4C, 0x04A3B969, 0x20B35D01, true),
        new(10, "humba", "Humba Wumba", 0x1FF8DF44, 0x04E8DC61, 0x20F83809, true),
        new(11, "bottles", "Bottles", 0x1F982888, 0x0462FCC5, 0x2021731B, true),
        new(12, "boggy", "Boggy", 0x1F6099DB, 0x04709AFE, 0x20607E96, true),
        new(13, "jinjoking", "King Jingaling", 0x1F4C6D42, 0x04253B3D, 0x2099475E, true),
        new(14, "jolly", "Jolly Dodger", 0x1FB1E430, 0x04A1E715, 0x20B1037D, true),
        new(15, "blubber", "Captain Blubber", 0x1F876649, 0x047DB204, 0x203E3DDA, true),
        new(16, "piddles", "Piddles", 0x1F79866F, 0x04835222, 0x20C0DDFC, true),
        new(17, "jinjo", "Jinjo", 0x1FD07D55, 0x04C07E70, 0x20D09A18, true),
    };

    public static Character Get(string? key) => All.FirstOrDefault(c => c.Key == key) ?? All[0];
    public static Character ByIndex(int i) => i >= 0 && i < All.Count ? All[i] : All[0];

    /// <summary>The two mailbox words for a character: (town-only slot, every-level slot); Banjo = (0, 0).</summary>
    public static (uint Town, uint Any) MailboxWords(Character c) =>
        c.Index == 0 ? (0u, 0u) : c.TownOnly ? (c.Actor, 0u) : (0u, c.Actor);
}
