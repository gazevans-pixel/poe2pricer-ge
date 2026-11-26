using Poe2Pricer.Core.Market;
using System.Text.RegularExpressions;

namespace Poe2Pricer.Core.Waystones;

// What a waystone's tooltip says, read from its OCR lines: the name and tier from the title,
// the summary block (only the lines the game shows — a value of 0 has no line), the mods
// matched against WaystoneMods, and Corrupted. Unread: lines between the summary and the
// flavour text that matched no mod (a garbled line, or a line from a mechanic the list does
// not know).
public sealed record WaystoneReading(
    string Name,
    int Tier,
    int? Revives,
    int? ItemRarity,
    int? PackSize,
    int? MonsterRarity,
    int? Effectiveness,
    int? DropChance,
    IReadOnlyList<WaystoneModReading> Mods,
    bool Corrupted,
    IReadOnlyList<string> Unread,
    bool Ended = true,
    bool Unidentified = false
)
{
    // Something of the tooltip was understood: a summary value, a mod, or at least its closing
    // line ("Can be used in a Map Device…"). Not so when the client writes in a language the
    // program does not read: with Alt held the title is English in every client, the rest
    // stays in the client's language, and a question with the tier alone would price any
    // waystone of that tier (21.10.2026: "1 ex" for a German tooltip read as nothing). A white
    // waystone without mods has its closing line and is asked for.
    // Unidentified: the tooltip says so ("Unidentified", a magic or rare one before it is
    // identified: the base line as title, no name, no mods — the controller shows it for loot
    // on the ground, 08.11.2026). It reads like a white one and is not asked for: its price is
    // not the base's.
    public bool Understood =>
        Ended || Mods.Count > 0 || new[] { Revives, ItemRarity, PackSize, MonsterRarity, Effectiveness, DropChance }.Any(v => v is not null);

    // The values a price is asked with: the summary block, corrupted, tier, and the mods with
    // their values (a later reading that finds a mod the first lost asks anew; review before 1.2.0).
    public bool SameValues(WaystoneReading other) =>
        (Tier, Revives, ItemRarity, PackSize, MonsterRarity, Effectiveness, DropChance, Corrupted)
            == (other.Tier, other.Revives, other.ItemRarity, other.PackSize, other.MonsterRarity, other.Effectiveness, other.DropChance, other.Corrupted)
        && Mods.Select(m => (m.Mod.Text, m.Value)).OrderBy(m => m.Text).SequenceEqual(other.Mods.Select(m => (m.Mod.Text, m.Value)).OrderBy(m => m.Text));

    // This reading with what a later one adds: a value the OCR lost here and found there,
    // a mod line that came out garbled here and clean there. Mods are told apart by text.
    public WaystoneReading Merge(WaystoneReading later)
    {
        var mods = ModRows.Merged(Mods, later.Mods, m => m.Mod.Text, m => m.Value is not null);
        return this with
        {
            Revives = Revives ?? later.Revives, ItemRarity = ItemRarity ?? later.ItemRarity, PackSize = PackSize ?? later.PackSize,
            MonsterRarity = MonsterRarity ?? later.MonsterRarity, Effectiveness = Effectiveness ?? later.Effectiveness, DropChance = DropChance ?? later.DropChance,
            Mods = mods, Corrupted = Corrupted || later.Corrupted, Unread = later.Unread, Ended = Ended || later.Ended, Unidentified = Unidentified || later.Unidentified,
        };
    }
}

public sealed record WaystoneModReading(WaystoneMod Mod, int? Value, string Line, int Distance);

public static partial class WaystoneTooltip
{
    public static bool IsTitle(string line) => Title().IsMatch(line);

    // "Unidentified" / "Nicht identifiziert" as the OCR may render it, a letter or two off
    // ("Unidentifed", review 08.11.2026): the key within distance 2 of the word.
    public static bool IsUnidentified(string key) =>
        key.Length >= 9 && (TitleReader.Distance(key, "unidentified") <= 2 || TitleReader.Distance(key, "nichtidentifiziert") <= 2);

    // lines: the tooltip's text lines from top to bottom, as the OCR gives them. null: no
    // waystone title among them.
    public static WaystoneReading? Parse(IReadOnlyList<string> lines)
    {
        int title = -1, tier = 0;
        string titleText = "";
        // The one title line of a magic waystone ("Penetrating Waystone (Tier 14) of Sleet")
        // comes from the OCR in two pieces now and then ("PENETRATING WAYSTONE", "IER 14
        // SLEET", 21.10.2026): when no line names it alone, a line and the next one read
        // together count as well (not before: a rare's name would join its base line).
        for (int i = 0; i < lines.Count && title < 0; i++)
            if (Title().Match(lines[i]) is { Success: true } m)
                (title, tier, titleText) = (i, int.Parse(m.Groups[1].Value), lines[i]);
        for (int i = 0; i + 1 < lines.Count && title < 0; i++)
            if (Title().Match(lines[i] + " " + lines[i + 1]) is { Success: true } joined)
                (title, tier, titleText) = (i + 1, int.Parse(joined.Groups[1].Value), lines[i] + " " + lines[i + 1]);
        if (title < 0)
            return null;
        // A rare waystone's name stands on the line above "Waystone (Tier N)"; a magic one's
        // title line is its name ("Penetrating Waystone (Tier 14) of Sleet").
        bool baseOnly = titleText.Trim().Length - Title().Match(titleText).Length <= BaseSlack;
        string name = !baseOnly ? titleText.Trim() : title > 0 && titleText == lines[title] ? lines[title - 1].Trim() : "";
        int? revives = null, itemRarity = null, packSize = null, monsterRarity = null, effectiveness = null, dropChance = null;
        var mods = new List<WaystoneModReading>();
        var unread = new List<string>();
        bool corrupted = false, ended = false, unidentified = false;
        foreach (var raw in lines.Skip(title + 1))
        {
            string line = Affix().Replace(raw, "").Trim();
            if (line.Length == 0)
                continue;
            string key = WaystoneMods.KeyOf(line);
            if (IsUnidentified(key))
            {
                unidentified = true;
                continue;
            }
            if (key.StartsWith("canbeusedinamapdevice") || key.StartsWith("waystonescanonlybeusedonce")
                || key.StartsWith("kannineinemkartenapparat") || key.StartsWith("wegsteinekonnennureinmal"))
            {
                ended = true;
                break;
            }
            if (key is "corrupted" or "verderbt")
            {
                corrupted = true;
                continue;
            }
            if (Summary(line, key, Revives, 0, RevivesLimit, ref revives) || Summary(line, key, ItemRarity, 1, SummaryLimit, ref itemRarity)
                || Summary(line, key, PackSize, 1, SummaryLimit, ref packSize) || Summary(line, key, MonsterRarity, 1, SummaryLimit, ref monsterRarity)
                || Summary(line, key, Effectiveness, 1, SummaryLimit, ref effectiveness) || Summary(line, key, DropChance, 1, SummaryLimit, ref dropChance))
                continue;
            if (key is "prefix" or "suffix" or "prafix" || key.Length < WaystoneMods.MinimumKey)
                continue;
            if (WaystoneMods.Match(line) is { } found)
            {
                // A line that shows its number with a minus ("-10% maximum Player
                // Resistances") is listed with the minus in its text and positive values.
                int? value = found.Mod.HasValue ? WaystoneMods.ValueOf(line) : null;
                if (value is { } v && found.Mod.Text.Contains("-#"))
                    value = Math.Abs(v);
                value = WaystoneMods.Plausible(value, found.Mod.Max, line);
                // A mod wrapped onto two lines (a long German one at UI Text Size Extra Large,
                // 22.10.2026): the second line fits the same mod as a part of it, even with a
                // stray line between them (stash tier marks read through the tooltip). One mod,
                // with the value from whichever line carries it; the game lists no mod twice.
                if (mods.Count > 0 && mods[^1].Mod == found.Mod)
                    mods[^1] = mods[^1] with { Value = mods[^1].Value ?? value, Line = mods[^1].Line + " " + line };
                else
                    mods.Add(new(found.Mod, value, line, found.Distance));
            }
            else
                unread.Add(line);
        }
        return new(name, tier, revives, itemRarity, packSize, monsterRarity, effectiveness, dropChance, mods, corrupted, unread, ended, unidentified);
    }

    // A summary line: "<label>: <value>". The label is matched on its letters alone, two
    // letters off at most ("AVAILABLé"); the value is the number before the per cent sign,
    // which the OCR writes as "0/0", "0/6" or "04" as often as "%", "O" standing in for a 0.
    // A value beyond what the game rolls (a "+" read as a digit: "+70%" → "4700/0") is not
    // taken: the line stays without value, and the next reading may do better. Nor is a value
    // below minimum: the game shows "Revives Available: 0", but a rarity, pack size,
    // effectiveness or drop chance line only with a value, so a 0 there is a misread ("+19%"
    // behind a dimmed tier mark of the stash read as "*00/0", the author's diagnosis 21.10.2026
    // 16:32).
    // labels: the label in English and in German (ClientStrings of the game files, as KeyOf
    // gives them).
    private static bool Summary(string line, string key, string[] labels, int minimum, int limit, ref int? value)
    {
        if (!labels.Any(label => Math.Abs(key.Length - label.Length) <= 2 && TitleReader.Distance(key.Length > label.Length + 2 ? key[..(label.Length + 2)] : key, label) <= 2))
            return false;
        int colon = line.IndexOf(':');
        string after = PerCent().Replace(colon >= 0 ? line[(colon + 1)..] : line, "$1%").Trim();
        if (SummaryValue().Match(after) is { Success: true } m && int.Parse(m.Groups[1].Value) is var v && v >= minimum && v <= limit)
            value = v;
        else if (minimum == 0 && after.TrimEnd('%') is "O" or "o" or "0")
            value = 0;
        return true;
    }

    public const int SummaryLimit = 300, RevivesLimit = 9;

    // Characters beside "Waystone (Tier N)" on a line that is still the base alone: the
    // brackets the pattern leaves out, a stray mark of the OCR.
    private const int BaseSlack = 4;

    // The summary labels, English and German ("Wiederbelebungen verfügbar", "Monstergruppengröße",
    // "Monster-Schlagkraft", "Chance auf fallen gelassene Wegsteine").
    private static readonly string[] Revives = ["revivesavailable", "wiederbelebungenverfugbar"],
        ItemRarity = ["itemrarity", "gegenstandsseltenheit"],
        PackSize = ["packsize", "monstergruppengrosse"],
        MonsterRarity = ["monsterrarity", "monsterseltenheit"],
        Effectiveness = ["monstereffectiveness", "monsterschlagkraft"],
        DropChance = ["waystonedropchance", "chanceauffallengelassenewegsteine"];

    // "Waystone (Tier 15)", in the German client "Wegstein (Level 15)" — with Alt held the game
    // writes the English name there too.
    [GeneratedRegex(@"(?:WAYSTONE\W*T?IER|WEGSTEIN\W*L?EVEL)\s*(\d{1,2})", RegexOptions.IgnoreCase)]
    private static partial Regex Title();

    // "PREFIX"/"SUFFIX" ("PRÄFIX") at the start of a line with Alt held, when the OCR joins the
    // label to the mod.
    [GeneratedRegex(@"^\s*(PREFIX|SUFFIX|PR\w?FIX)\b\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Affix();

    // The value at the line's end, with nothing but the label before it: a per cent on the
    // rarity lines, none on "Revives Available: 6"; a line that goes on after the number (the
    // tab list read through the tooltip, "+5504rice 5 exalted") has none.
    [GeneratedRegex(@"^[^\d]*?[+-]?\s*(\d{1,3})\s*%?\s*$")]
    private static partial Regex SummaryValue();

    [GeneratedRegex(@"(\d)\s*(0/0|o/o|O/O|0/6|04)(?!\d)")]
    private static partial Regex PerCent();
}
