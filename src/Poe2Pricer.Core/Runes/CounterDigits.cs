using OpenCvSharp;
using Tune = Poe2Pricer.Core.Tuning.Explosives;

namespace Poe2Pricer.Core.Runes;

// The digits of the game's explosives counter line ("Explosives Placed: 2/5", the controller
// UI), read with pictures of the game's own drawing of them. The OCR misreads the slash of
// this line as a "1" in every third reading of a still picture and more in play ("1/15" came
// out as "1/1" and closed an expedition after its first explosive, 07.11.2026), and the
// tooltip digits (NumberGlyphs) do not fit the line's size (.78–.96 against letters up to
// .82). The game's drawing fits: digits cut from the line hit on other pictures of the same
// window height at .95–1.00, letters at most .88 (40 pictures at 1080, 1440 and 2160) — but
// only at that height and in the game's own pixels: the game draws the letters anew for
// every window height, a set of 1440 hits on 2160 at .82–.94, and the 2160 picture brought
// to 1440 shifts each digit by a third of a pixel (.77–.93). So a set per height, in
// data/ui/counter/<height>/ (0.png … 9.png, slash.png; cut by tools/counter_digits.py from
// the fixtures, the world behind the line blacked out: every pixel whose brightest channel
// is below Tune.CounterBright, as here before matching), read in Frame.Native.
// The line is read from its right end: the glyphs that hit, in order, as far as they stand
// close together — the "1" fits the stems of letters ("l", "P": .85–.95), which stand left
// of the colon and are not reached. A digit without a picture breaks the run, so the number
// is never read too high: a missing digit can only shorten the left number ("6/15" → "/15").
// And every bright blob a glyph of the run touches must lie within the run's glyphs (Tune.
// DigitCoverage): with the "6" missing from a set, the "1" fit the stem of a "6" in play
// ("1 left" of a box showing 6, 07.11.2026) — a "1" covers .47 of the 6, the true glyphs
// cover .89–1.00 of their blobs on the 73 fixtures (.89: a "2" cut from a touching slash).
public sealed class CounterDigits : IDisposable
{
    private readonly Dictionary<int, (string Char, Mat Picture)[]> sets = [];

    public CounterDigits(string folder)
    {
        if (!Directory.Exists(folder))
            return;
        foreach (string dir in Directory.GetDirectories(folder))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int height))
                continue;
            var set = new List<(string, Mat)>();
            foreach (string file in Directory.GetFiles(dir, "*.png").OrderBy(f => f, StringComparer.Ordinal))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                var picture = Cv2.ImRead(file, ImreadModes.Grayscale);
                if (picture.Empty())
                    throw new FileNotFoundException($"counter digit picture unreadable: {file}", file);
                set.Add((name == "slash" ? "/" : name, picture));
            }
            if (set.Count > 0)
                sets[height] = set.ToArray();
        }
    }

    // The window heights a set exists for, and what a height's set lacks of "0123456789/".
    public IReadOnlyCollection<int> Heights => sets.Keys.Order().ToArray();

    public bool Has(int height) => sets.ContainsKey(height);

    public string Missing(int height) => sets.TryGetValue(height, out var set) ? string.Concat("0123456789/".Where(c => set.All(s => s.Char != c.ToString()))) : "0123456789/";

    // The glyphs at the right end of the line ("5/5", "14/15"), null when none hits. line: in
    // native's pixels; scale: native pixels per reference pixel (the gap allowed between
    // glyphs, Tune.DigitGap, is in reference pixels).
    public string? Read(Mat native, Rect line, double scale)
    {
        if (!sets.TryGetValue(native.Height, out var set))
            return null;
        line = line.Intersect(new Rect(0, 0, native.Width, native.Height));
        if (line.Width <= 0 || line.Height <= 0)
            return null;
        using var crop = new Mat(native, line);
        using var kept = Bright(crop);
        var hits = new List<(int X, int Y, int Width, int Height, double Score, string Char)>();
        foreach (var (ch, picture) in set)
        {
            if (picture.Width > kept.Width || picture.Height > kept.Height)
                continue;
            using var result = new Mat();
            Cv2.MatchTemplate(kept, picture, result, TemplateMatchModes.CCoeffNormed);
            for (int y = 0; y < result.Rows; y++)
                for (int x = 0; x < result.Cols; x++)
                {
                    float score = result.At<float>(y, x);
                    if (score >= Tune.DigitMatch && Peak(result, x, y, score))
                        hits.Add((x, y, picture.Width, picture.Height, score, ch));
                }
        }
        // Where two overlap, the better wins (a "1" in the stem of a "4").
        var glyphs = new List<(int X, int Y, int Width, int Height, double Score, string Char)>();
        foreach (var hit in hits.OrderByDescending(h => h.Score))
            if (!glyphs.Any(g => Math.Min(g.X + g.Width, hit.X + hit.Width) - Math.Max(g.X, hit.X) > Tune.DigitOverlap * Math.Min(g.Width, hit.Width)))
                glyphs.Add(hit);
        if (glyphs.Count == 0)
            return null;
        glyphs.Sort((a, b) => a.X.CompareTo(b.X));
        // From the right, as far as the glyphs stand close together.
        int first = glyphs.Count - 1;
        while (first > 0 && glyphs[first].X - (glyphs[first - 1].X + glyphs[first - 1].Width) <= Tune.DigitGap * scale)
            first--;
        var run = glyphs.Skip(first).ToArray();
        return Covered(kept, run) ? string.Concat(run.Select(g => g.Char)) : null;
    }

    // Whether the bright blobs the run's glyphs touch lie within the glyphs (widened by a
    // pixel) to Tune.DigitCoverage: a glyph that fits part of a larger digit does not.
    private static bool Covered(Mat kept, (int X, int Y, int Width, int Height, double Score, string Char)[] run)
    {
        using var mask = new Mat();
        Cv2.Threshold(kept, mask, 0, 255, ThresholdTypes.Binary);
        using var labels = new Mat();
        int count = Cv2.ConnectedComponents(mask, labels, PixelConnectivity.Connectivity8);
        using var within = new Mat(kept.Size(), MatType.CV_8UC1, Scalar.All(0));
        foreach (var g in run)
        {
            using var part = within[new Rect(g.X - 1, g.Y - 1, g.Width + 2, g.Height + 2).Intersect(new Rect(0, 0, kept.Width, kept.Height))];
            part.SetTo(255);
        }
        var touched = new bool[count];
        for (int y = 0; y < kept.Height; y++)
            for (int x = 0; x < kept.Width; x++)
                if (within.At<byte>(y, x) != 0 && labels.At<int>(y, x) != 0)
                    touched[labels.At<int>(y, x)] = true;
        int total = 0, inside = 0;
        for (int y = 0; y < kept.Height; y++)
            for (int x = 0; x < kept.Width; x++)
            {
                int label = labels.At<int>(y, x);
                if (label == 0 || !touched[label])
                    continue;
                total++;
                if (within.At<byte>(y, x) != 0)
                    inside++;
            }
        return total > 0 && inside >= Tune.DigitCoverage * total;
    }

    // The line with the world behind it blacked out: the text is white, what shows through
    // the block is darker than Tune.CounterBright in every channel (sand, lava and crystals
    // on the pictures of 07.11.2026).
    private static Mat Bright(Mat crop)
    {
        var channels = crop.Split();
        using var most = new Mat();
        Cv2.Max(channels[0], channels[1], most);
        Cv2.Max(most, channels[2], most);
        foreach (var channel in channels)
            channel.Dispose();
        using var mask = new Mat();
        Cv2.Threshold(most, mask, Tune.CounterBright - 1, 255, ThresholdTypes.Binary);
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        var kept = new Mat(crop.Size(), MatType.CV_8UC1, Scalar.All(0));
        gray.CopyTo(kept, mask);
        return kept;
    }

    private static bool Peak(Mat result, int x, int y, float score)
    {
        for (int dy = -Tune.DigitPeak; dy <= Tune.DigitPeak; dy++)
            for (int dx = -Tune.DigitPeak; dx <= Tune.DigitPeak; dx++)
            {
                int yy = y + dy, xx = x + dx;
                if (yy >= 0 && xx >= 0 && yy < result.Rows && xx < result.Cols && result.At<float>(yy, xx) > score)
                    return false;
            }
        return true;
    }

    public void Dispose()
    {
        foreach (var set in sets.Values)
            foreach (var (_, picture) in set)
                picture.Dispose();
        sets.Clear();
    }
}
