using System.Text.RegularExpressions;
using OpenCvSharp;
using Tune = Poe2Pricer.Core.Tuning.Explosives;

namespace Poe2Pricer.Core.Runes;

// What the game's explosives counter says of the expedition the player stands at.
// AllPlaced: every explosive of this expedition is placed — the detonator can only be used
// then (there is no partial detonation), so the rune chain of this expedition is over.
public abstract record ExplosivesReading
{
    public abstract bool AllPlaced { get; }
}

// The controller UI's block under "Map Content": "Explosives Placed: 2/5".
public sealed record ExplosivesPlaced(int Placed, int Of) : ExplosivesReading
{
    public override bool AllPlaced => Of > 0 && Placed == Of;
    public override string ToString() => $"{Placed}/{Of} placed";
}

// The keyboard UI's counter box over the explosive button: the explosives left to place.
public sealed record ExplosivesLeft(int Left) : ExplosivesReading
{
    public override bool AllPlaced => Left == 0;
    public override string ToString() => $"{Left} left";
}

// The block or the counter is there, but nothing can be read of it (no digit pictures for
// this window height): nothing is concluded from it, the reason is logged.
public sealed record ExplosivesUnread(string Text) : ExplosivesReading
{
    public override bool AllPlaced => false;
    public override string ToString() => $"not understood: \"{Text}\"";
}

// Reads the game's explosives counter from a capture of the game, for the rune chain's
// expedition boundaries (RuneChain.Complete). Two UI modes, two places (pictures of
// 07.11.2026 at 1080, 1440 and 2160, maps and a logbook, English and German):
// - Controller: a block at the right edge under "Map Content", headed "Expedition Encounter"
//   with the expedition's swirl icon at the heading's right; below it "Explosives Placed: 2/5"
//   ("Platzierter Sprengstoff: 14/15") and the button hints, then "Return To Detonator" once
//   all are placed, "Explosives Detonated" after. The block stands only near the expedition's
//   site; its height depends on the lines above it (the map's objectives). The icon is
//   artwork and found by template (Anchor) on the reference picture; the number is read with
//   the game's own digits (CounterDigits) in the picture at the game's size (Frame.Native) —
//   the OCR misreads the slash. The block past the placing has no number and says nothing
//   new: the row closed at "5/5", which the block shows until the detonation (no OCR at all,
//   the author, 07.11.2026).
// - Keyboard: the explosive button right of the skill bar at the bottom, a counter box with
//   the explosives left above it and an undo arrow beside the box. Shown anywhere in the map
//   from the first contact, red near the site, grey away from it; gone once detonated; while
//   placing, a tooltip stands in the box's place. The button's plate is translucent (the world
//   shows through: .49–.56 against the template from another picture, 1.00 on its own), the
//   undo arrow is opaque (.74–1.00) — both must be there. The number in the box is 14 px high
//   at reference size, in the game's HUD font at a size of its own: the OCR reads nothing of a
//   lone digit, the tooltip digits (NumberGlyphs) hit .78–.90 and missed the 4 and the 6, the
//   line's digits .80–.89 — so a second set per height, cut from the box (CounterDigits).
public sealed partial class ExplosivesReader(string uiDirectory) : IDisposable
{
    private readonly Mat icon = Anchor.Load(uiDirectory, "expedition-icon.png");
    private readonly Mat button = Anchor.Load(uiDirectory, "explosive-button.png");
    private readonly Mat undo = Anchor.Load(uiDirectory, "explosive-undo.png");
    private readonly CounterDigits digits = new(Path.Combine(uiDirectory, Tune.CounterFolder, Tune.LineFolder));
    private readonly CounterDigits boxDigits = new(Path.Combine(uiDirectory, Tune.CounterFolder, Tune.BoxFolder));

    // The window heights the counter's number can be read at (the controller block's line,
    // the keyboard button's box).
    public IReadOnlyCollection<int> DigitHeights => digits.Heights.Intersect(boxDigits.Heights).ToArray();

    // Where the two UI elements are on this picture, with their template scores (for tests
    // and the diagnosis; the scores are matched over the bands of Tuning.Explosives).
    public sealed record Located(double Icon, Point IconAt, double Button, Point ButtonAt, double Undo)
    {
        public bool ControllerBlock => Icon >= Tune.IconFrom;
        public bool KeyboardCounter => Button >= Tune.ButtonFrom && Undo >= Tune.UndoFrom;
    }

    public Located Locate(Mat bgr)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        var (iconScore, iconAt) = Best(gray, icon, new Rect(bgr.Width - Tune.RightBand, Tune.IconTop, Tune.RightBand, Tune.IconBottom - Tune.IconTop));
        var (buttonScore, buttonAt) = Best(gray, button, new Rect(bgr.Width / 2 + Tune.ButtonLeft, bgr.Height - Tune.BottomBand, Tune.ButtonRight - Tune.ButtonLeft, Tune.BottomBand));
        // The undo arrow stands at a fixed offset from the button: looked for there alone.
        var (undoScore, _) = buttonScore >= Tune.ButtonFrom
            ? Best(gray, undo, new Rect(buttonAt.X + Tune.UndoDx - Tune.UndoSlack, buttonAt.Y + Tune.UndoDy - Tune.UndoSlack, undo.Width + 2 * Tune.UndoSlack, undo.Height + 2 * Tune.UndoSlack))
            : (0, default);
        return new Located(iconScore, iconAt, buttonScore, buttonAt, undoScore);
    }

    // The counter line's number on the picture at the game's size: "5/5", "14/15"; null when
    // no digit hits (no pictures for this height, the block past the placing).
    public string? Number(GameCapture.Frame frame, Located at)
    {
        if (frame.Native is not { } native)
            return null;
        double s = frame.Scale;
        var line = new Rect(
            (int)Math.Round((frame.Image.Width - Tune.LineReach) * s),
            (int)Math.Round((at.IconAt.Y + Tune.LineTop) * s),
            (int)Math.Round((Tune.LineReach - (frame.Image.Width - at.IconAt.X)) * s),
            (int)Math.Round((Tune.LineBottom - Tune.LineTop) * s));
        return digits.Read(native, line, s);
    }

    // The counter as the capture shows it; null when neither element is there (the player is
    // away from the site, no expedition, the HUD is hidden) or the block shows no number
    // (past the detonation).
    public ExplosivesReading? Read(GameCapture.Frame frame)
    {
        var bgr = frame.Image;
        var at = Locate(bgr);
        if (at.ControllerBlock)
        {
            if (frame.Native is not { } native)
                return new ExplosivesUnread("no picture at the game's size");
            if (!digits.Has(native.Height))
                return new ExplosivesUnread($"no digit pictures for a screen {native.Height} px high");
            string? number = Number(frame, at);
            if (number is not null && Placed().Match(number) is { Success: true } m && int.TryParse(m.Groups[1].Value, out int placed) && int.TryParse(m.Groups[2].Value, out int of) && placed <= of)
                return new ExplosivesPlaced(placed, of);
            return null;
        }
        if (at.KeyboardCounter)
        {
            if (frame.Native is not { } native)
                return new ExplosivesUnread("no picture at the game's size");
            if (!boxDigits.Has(native.Height))
                return new ExplosivesUnread($"no digit pictures for a screen {native.Height} px high");
            double s = frame.Scale;
            var box = new Rect(
                (int)Math.Round((at.ButtonAt.X + Tune.BoxDx) * s),
                (int)Math.Round((at.ButtonAt.Y + Tune.BoxDy) * s),
                (int)Math.Round(Tune.BoxWidth * s),
                (int)Math.Round(Tune.BoxHeight * s));
            string? text = boxDigits.Read(native, box, s);
            if (text is not null && Digits().Match(text) is { Success: true } m && m.Value == text && int.TryParse(text, out int left))
                return new ExplosivesLeft(left);
            // No number in the box this moment (a tooltip over it while an explosive is held,
            // the button fading): null, like the controller's line — not Unread, which would
            // put the counter aside for UnreadEvery and could miss the "0" that stands only
            // until the detonation (review 08.11.2026).
            return null;
        }
        return null;
    }

    private static (double Score, Point At) Best(Mat gray, Mat template, Rect band)
    {
        band = band.Intersect(new Rect(0, 0, gray.Width, gray.Height));
        if (band.Width < template.Width || band.Height < template.Height)
            return (0, default);
        using var crop = new Mat(gray, band);
        using var scores = new Mat();
        Cv2.MatchTemplate(crop, template, scores, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(scores, out _, out double score, out _, out Point at);
        return (score, new Point(band.X + at.X, band.Y + at.Y));
    }

    [GeneratedRegex(@"^(\d{1,2})/(\d{1,2})$")]
    private static partial Regex Placed();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    public void Dispose()
    {
        icon.Dispose();
        button.Dispose();
        undo.Dispose();
        digits.Dispose();
        boxDigits.Dispose();
    }
}
