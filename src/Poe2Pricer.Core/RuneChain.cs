using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using OpenCvSharp;
using Tune = Poe2Pricer.Core.Tuning.Runes;

namespace Poe2Pricer.Core;

// A small picture of a rune for the overlay: Size × Size pixels, BGR, row by row.
public sealed record RuneThumb(int Size, byte[] Bgr);

// One slot of a rune bar: its centre, the crown above it, no rune in it.
public sealed record RuneSlot(int X, int Y, bool Crowned, bool Empty);

// The runes carried along the explosive chain of a map. A rune in a crowned slot of a Runic
// Remnant goes on to every later pack of the chain, and the game counts each rune once: a
// second remnant with the same crowned rune adds nothing. So the chain is the set of crowned
// runes of the remnants whose runes are chosen (a bar without an empty slot), each once.
// Learned from the bars above the remnants while no game window is open (RemnantGameWindow);
// forgotten when the game's log shows a new map, or by key.
// A rune is known by its shape only (no names on these screens): the same rune on two bars,
// and a bar's rune against the same rune drawn in the remnant list (Tuning.Runes).
public sealed partial class RuneChain : IDisposable
{
    // Drawn over a list row's crowned tile whose rune the chain carries already.
    public const string Repeat = "✗";

    private sealed record Carried(float[] Shape, RuneThumb Thumb);

    private readonly Mat cap;
    private readonly Lock gate = new();
    private readonly List<Carried> carried = [];
    private readonly GameLog log = new();

    public RuneChain(string uiDirectory) => cap = Anchor.Load(uiDirectory, Tune.CapFile);

    public int Count
    {
        get
        {
            lock (gate)
                return carried.Count;
        }
    }

    public RuneThumb[] Thumbs
    {
        get
        {
            lock (gate)
                return carried.Select(c => c.Thumb).ToArray();
        }
    }

    public void Reset(string why, Action<string>? say = null)
    {
        int had;
        lock (gate)
        {
            had = carried.Count;
            carried.Clear();
        }
        say?.Invoke($"rune chain cleared ({why}), {had} rune(s) forgotten");
    }

    // A new map in the game's log: a new chain.
    public void FollowLog(Action<string> say)
    {
        string? map;
        lock (gate)
            map = log.NewMap(say);
        if (map is not null)
            Reset($"new map {map}", say);
    }

    // The crowned runes of the chosen bars on this frame join the chain. How many joined.
    public int Learn(Mat bgr, Action<string> say)
    {
        int joined = 0;
        foreach (var bar in Bars(bgr))
        {
            // A slot still empty: the runes are not chosen yet.
            if (bar.Any(s => s.Empty))
                continue;
            foreach (var slot in bar.Where(s => s.Crowned))
            {
                if (Shape(BarInk(bgr, slot.X, slot.Y)) is not { } shape)
                    continue;
                lock (gate)
                {
                    if (carried.Any(c => Similar(c.Shape, shape) >= Tune.SameRune))
                        continue;
                    carried.Add(new(shape, Thumb(bgr, slot.X, slot.Y)));
                    joined++;
                }
            }
        }
        if (joined > 0)
            say($"rune chain: {joined} crowned rune(s) joined, {Count} carried");
        return joined;
    }

    // The rune bars on the frame, each slot from left to right.
    public RuneSlot[][] Bars(Mat bgr)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        if (gray.Width < cap.Width || gray.Height < cap.Height)
            return [];
        using var result = new Mat();
        Cv2.MatchTemplate(gray, cap, result, TemplateMatchModes.CCoeffNormed);
        var bars = new List<RuneSlot[]>();
        var whole = new Rect(0, 0, result.Width, result.Height);
        for (int n = 0; n < Tune.MostBars; n++)
        {
            Cv2.MinMaxLoc(result, out _, out double best, out _, out Point at);
            if (best < Tune.MinimumCapScore)
                break;
            var taken = new Rect(at.X - 60, at.Y - 40, 120, 80).Intersect(whole);
            using (var spot = new Mat(result, taken))
                spot.SetTo(Scalar.All(-1));
            if (Slots(bgr, gray, at) is { Length: > 0 } slots)
                bars.Add(slots);
        }
        return bars.ToArray();
    }

    // The slots left of a cap: circles in a band at the slots' height, from the last slot
    // leftwards, each about a pitch from the one before.
    private static RuneSlot[] Slots(Mat bgr, Mat gray, Point cap)
    {
        int y = cap.Y + Tune.CapToSlotY;
        int left = Math.Max(0, cap.X - Tune.MostSlots * Tune.SlotPitch - Tune.SlotBand);
        var band = new Rect(left, y - Tune.SlotBand, cap.X - left, 2 * Tune.SlotBand).Intersect(new Rect(0, 0, gray.Width, gray.Height));
        if (band.Width < Tune.SlotPitch || band.Height < 2 * Tune.SlotRadiusMax)
            return [];
        using var part = new Mat(gray, band);
        using var smooth = new Mat();
        Cv2.MedianBlur(part, smooth, 3);
        var circles = Cv2.HoughCircles(smooth, HoughModes.Gradient, 1, 55, 80, 25, Tune.SlotRadiusMin, Tune.SlotRadiusMax);
        var centres = circles
            .Select(c => (X: (int)Math.Round(c.Center.X) + band.X, Y: (int)Math.Round(c.Center.Y) + band.Y))
            .Where(c => Math.Abs(c.Y - y) <= Tune.SlotRowTolerance)
            .OrderByDescending(c => c.X)
            .ToList();
        var chain = new List<(int X, int Y)>();
        int expect = cap.X + Tune.CapToSlotX;
        foreach (var c in centres)
            if (Math.Abs(c.X - expect) <= Tune.SlotTolerance)
            {
                chain.Add(c);
                expect = c.X - Tune.SlotPitch;
            }
        chain.Reverse();
        return chain
            .Select(c => new RuneSlot(
                c.X,
                c.Y,
                GoldIn(bgr, new Rect(c.X - Tune.CrownWidth / 2, c.Y - Tune.CrownAbove, Tune.CrownWidth, Tune.CrownHeight)) >= Tune.CrownFrom,
                Glow(bgr, c.X, c.Y) < Tune.EmptyBelow
            ))
            .ToArray();
    }

    // The crowned tile of each row of the remnant list, keyboard UI (the controller UI draws
    // the list otherwise; not measured). A tile is given to one row only.
    public static (RemnantRow Row, int X, int Y)[] CrownedInList(Mat bgr, RemnantWindow window, IEnumerable<RemnantRow> rows)
    {
        if (window.Mode != UiMode.KeyboardMouse)
            return [];
        int half = Tune.TileSize / 2;
        var area = new Rect(
                window.List.X - half,
                window.List.Y - Tune.ListRises.Max() - half - Tune.TileSearchY,
                Tune.ListFirstX + Tune.ListMostRunes * Tune.ListPitch + Tune.TileSize,
                window.List.Height + Tune.ListRises.Max() + Tune.TileSize + 2 * Tune.TileSearchY
            )
            .Intersect(new Rect(0, 0, bgr.Width, bgr.Height));
        if (area.Width < Tune.TileSize || area.Height < Tune.TileSize)
            return [];
        using var gold = GoldMask(bgr, area);
        using var sums = new Mat();
        Cv2.Integral(gold, sums);
        sums.GetArray(out int[] s);
        int stride = sums.Cols;
        int Sum(int x0, int y0, int x1, int y1) => s[y1 * stride + x1] - s[y0 * stride + x1] - s[y1 * stride + x0] + s[y0 * stride + x0];
        int inner = Tune.TileSize - 2 * Tune.TileFrame;
        double ringArea = Tune.TileSize * Tune.TileSize - inner * inner;
        double Ring(int cx, int cy)
        {
            int x0 = cx - half - area.X,
                y0 = cy - half - area.Y;
            if (x0 < 0 || y0 < 0 || x0 + Tune.TileSize > area.Width || y0 + Tune.TileSize > area.Height)
                return 0;
            int outer = Sum(x0, y0, x0 + Tune.TileSize, y0 + Tune.TileSize);
            int middle = Sum(x0 + Tune.TileFrame, y0 + Tune.TileFrame, x0 + Tune.TileFrame + inner, y0 + Tune.TileFrame + inner);
            return (outer - middle) / ringArea;
        }
        var found = new List<(RemnantRow, int, int)>();
        foreach (var row in rows)
        {
            (double Score, int X, int Y) best = (0, 0, 0);
            foreach (int rise in Tune.ListRises)
                for (int k = 0; k < Tune.ListMostRunes; k++)
                {
                    int cx = window.List.X + Tune.ListFirstX + k * Tune.ListPitch,
                        cy = (int)Math.Round(row.CenterY) - rise;
                    for (int dy = -Tune.TileSearchY; dy <= Tune.TileSearchY; dy++)
                    for (int dx = -Tune.TileSearchX; dx <= Tune.TileSearchX; dx++)
                    {
                        double score = Ring(cx + dx, cy + dy);
                        if (score > best.Score)
                            best = (score, cx + dx, cy + dy);
                    }
                }
            if (best.Score < Tune.CrownedTileFrom)
                continue;
            if (found.Any(f => Math.Abs(f.Item2 - best.X) < half && Math.Abs(f.Item3 - best.Y) < half))
                continue;
            found.Add((row, best.X, best.Y));
        }
        return found.ToArray();
    }

    // The rune of a list tile is in the chain already: true; not: false; nothing to tell: null.
    public bool? Carries(Mat bgr, int x, int y)
    {
        if (Shape(ListInk(bgr, x, y)) is not { } shape)
            return null;
        lock (gate)
            return carried.Any(c => Similar(c.Shape, shape) >= Tune.SameRuneAcross);
    }

    // Share of gold pixels (the crown, the crowned tile's frame) in a part of the picture.
    private static double GoldIn(Mat bgr, Rect r)
    {
        r = r.Intersect(new Rect(0, 0, bgr.Width, bgr.Height));
        if (r.Width <= 0 || r.Height <= 0)
            return 0;
        using var mask = GoldMask(bgr, r);
        return Cv2.CountNonZero(mask) / (double)(r.Width * r.Height);
    }

    // 1 where the picture is gold, else 0.
    private static Mat GoldMask(Mat bgr, Rect r)
    {
        using var part = new Mat(bgr, r);
        using var hsv = new Mat();
        Cv2.CvtColor(part, hsv, ColorConversionCodes.BGR2HSV);
        var mask = new Mat();
        Cv2.InRange(hsv, new Scalar(8, 111, 151), new Scalar(28, 255, 255), mask);
        Cv2.Threshold(mask, mask, 0, 1, ThresholdTypes.Binary);
        return mask;
    }

    // The brightest channel of a square, as float; null when the square leaves the picture.
    private static Mat? Brightest(Mat bgr, int x, int y, int r)
    {
        var square = new Rect(x - r, y - r, 2 * r, 2 * r);
        if (square.X < 0 || square.Y < 0 || square.Right > bgr.Width || square.Bottom > bgr.Height)
            return null;
        using var part = new Mat(bgr, square);
        var channels = part.Split();
        try
        {
            using var most = new Mat();
            Cv2.Max(channels[0], channels[1], most);
            Cv2.Max(most, channels[2], most);
            var f = new Mat();
            most.ConvertTo(f, MatType.CV_32FC1);
            return f;
        }
        finally
        {
            foreach (var c in channels)
                c.Dispose();
        }
    }

    // Mean glow in a slot: the strokes of a rune light up, an empty slot stays dark.
    private static double Glow(Mat bgr, int x, int y)
    {
        using var f = Brightest(bgr, x, y, Tune.InkRadius);
        if (f is null)
            return 0;
        using var ink = AboveBlur(f);
        return Cv2.Mean(ink).Val0;
    }

    // What stands out of its surroundings: the image minus its blur, negatives cut off.
    private static Mat AboveBlur(Mat f)
    {
        using var blur = new Mat();
        Cv2.GaussianBlur(f, blur, new Size(0, 0), Tune.InkBlur);
        var ink = new Mat();
        Cv2.Subtract(f, blur, ink);
        Cv2.Threshold(ink, ink, 0, 0, ThresholdTypes.Tozero);
        return ink;
    }

    // A bar's rune: glowing strokes on a dark disc; only inside the slot's ring.
    private static Mat? BarInk(Mat bgr, int x, int y)
    {
        int r = Tune.BarInkRadius;
        using var f = Brightest(bgr, x, y, r);
        if (f is null)
            return null;
        var ink = AboveBlur(f);
        using var disc = new Mat(ink.Size(), MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(disc, new Point(r, r), r - 1, Scalar.All(255), -1);
        using var outside = new Mat();
        Cv2.BitwiseNot(disc, outside);
        ink.SetTo(Scalar.All(0), outside);
        return ink;
    }

    // A list tile's rune: dark strokes on parchment.
    private static Mat? ListInk(Mat bgr, int x, int y)
    {
        int r = Tune.ListInkRadius;
        var square = new Rect(x - r, y - r, 2 * r, 2 * r);
        if (square.X < 0 || square.Y < 0 || square.Right > bgr.Width || square.Bottom > bgr.Height)
            return null;
        using var part = new Mat(bgr, square);
        using var gray = new Mat();
        Cv2.CvtColor(part, gray, ColorConversionCodes.BGR2GRAY);
        using var f = new Mat();
        gray.ConvertTo(f, MatType.CV_32FC1);
        using var blur = new Mat();
        Cv2.GaussianBlur(f, blur, new Size(0, 0), Tune.InkBlur);
        var ink = new Mat();
        Cv2.Subtract(blur, f, ink);
        Cv2.Threshold(ink, ink, 0, 0, ThresholdTypes.Tozero);
        return ink;
    }

    // The strokes centred on their centre of mass and scaled to a fixed spread: the same rune
    // drawn larger, smaller or a little off centre gives the same shape. Mean 0, length 1.
    private static float[]? Shape(Mat? ink)
    {
        if (ink is null)
            return null;
        using (ink)
        {
            var m = Cv2.Moments(ink);
            if (m.M00 <= 1e-6)
                return null;
            double cx = m.M10 / m.M00,
                cy = m.M01 / m.M00,
                spread = Math.Sqrt((m.Mu20 + m.Mu02) / m.M00);
            if (spread < 1e-3)
                return null;
            int n = Tune.SignatureSize;
            double scale = n * Tune.SignatureSpread / spread;
            using var affine = Mat.FromArray(
                new double[,]
                {
                    { scale, 0, n / 2.0 - scale * cx },
                    { 0, scale, n / 2.0 - scale * cy },
                }
            );
            using var shaped = new Mat();
            Cv2.WarpAffine(ink, shaped, affine, new Size(n, n), InterpolationFlags.Area);
            shaped.GetArray(out float[] v);
            double mean = v.Average(p => (double)p);
            double norm = Math.Sqrt(v.Sum(p => (p - mean) * (p - mean)));
            if (norm <= 0)
                return null;
            return v.Select(p => (float)((p - mean) / norm)).ToArray();
        }
    }

    // Best correlation of two shapes, one shifted by up to SignatureShift px (wrapping round).
    private static double Similar(float[] a, float[] b)
    {
        int n = Tune.SignatureSize,
            k = Tune.SignatureShift;
        double best = -1;
        for (int dy = -k; dy <= k; dy++)
        for (int dx = -k; dx <= k; dx++)
        {
            double sum = 0;
            for (int y = 0; y < n; y++)
            {
                int by = ((y - dy) % n + n) % n;
                for (int x = 0; x < n; x++)
                    sum += a[y * n + x] * b[by * n + ((x - dx) % n + n) % n];
            }
            best = Math.Max(best, sum);
        }
        return best;
    }

    private static RuneThumb Thumb(Mat bgr, int x, int y)
    {
        int size = Tune.ThumbSize;
        var square = new Rect(x - size / 2, y - size / 2, size, size).Intersect(new Rect(0, 0, bgr.Width, bgr.Height));
        using var part = new Mat(bgr, square);
        using var copy = new Mat();
        Cv2.Resize(part, copy, new Size(size, size));
        var bytes = new byte[size * size * 3];
        Marshal.Copy(copy.Data, bytes, 0, bytes.Length);
        return new(size, bytes);
    }

    public void Dispose() => cap.Dispose();

    // The game's log (logs/Client.txt beside the game's program): the game writes a line for
    // every area it generates, with the area's name and seed. A map with another name or seed
    // is a new map; a hideout or town in between is not, nor the same map entered again. Read
    // from where it ended when first found: what came before is no news.
    private sealed partial class GameLog
    {
        private string? path;
        private long position = -1;
        private long searched = long.MinValue;
        private (string Area, string Seed)? map;

        [GeneratedRegex("""Generating level \d+ area "(?<area>[^"]+)" with seed (?<seed>\d+)""")]
        private static partial Regex Generated();

        // The map entered since the last call, or null.
        public string? NewMap(Action<string> say)
        {
            if (path is null || !File.Exists(path))
            {
                if (Environment.TickCount64 - searched < (long)Tune.LogSearchEvery.TotalMilliseconds)
                    return null;
                searched = Environment.TickCount64;
                path = Find();
                position = -1;
                if (path is null)
                    return null;
                say($"rune chain: game log {path}");
            }
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long length = stream.Length;
                if (position < 0 || length < position || length - position > Tune.LogReadMost)
                {
                    position = length;
                    return null;
                }
                if (length == position)
                    return null;
                var bytes = new byte[length - position];
                stream.Seek(position, SeekOrigin.Begin);
                int read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                if (read <= 0)
                    return null;
                int end = Array.LastIndexOf(bytes, (byte)'\n', read - 1);
                if (end < 0)
                    return null;
                position += end + 1;
                string? entered = null;
                foreach (Match m in Generated().Matches(Encoding.UTF8.GetString(bytes, 0, end + 1)))
                {
                    string area = m.Groups["area"].Value,
                        seed = m.Groups["seed"].Value;
                    if (area.Contains("hideout", StringComparison.OrdinalIgnoreCase) || area.Contains("town", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (map is { } now && now.Area == area && now.Seed == seed)
                        continue;
                    map = (area, seed);
                    entered = area;
                }
                return entered;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        // The log beside the running game's program; null while the game is not running or its
        // program cannot be asked for its place.
        private static string? Find()
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!p.ProcessName.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (p.MainModule?.FileName is not { } program || Path.GetDirectoryName(program) is not { } folder)
                        continue;
                    var file = Path.Combine(folder, "logs", "Client.txt");
                    if (File.Exists(file))
                        return file;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                }
                finally
                {
                    p.Dispose();
                }
            }
            return null;
        }
    }
}
