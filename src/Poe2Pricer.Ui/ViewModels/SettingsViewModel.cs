using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Poe2Pricer.Core;
using Poe2Pricer.Core.Market;

namespace Poe2Pricer.Ui.ViewModels;

// A league of the list, shown by poe.ninja's own name ("Forbidden Rites", "HC Forbidden Rites",
// "Standard", "Hardcore").
// null = follow the current softcore league.
public sealed record LeagueChoice(string? Value, string Text)
{
    public override string ToString() => Text;
}

// One checkbox per switch of the automatic scan, from the window profiles (AutoSwitch.All).
// The automatic scan runs as soon as one of them is ticked; there is no main switch.
public sealed class AutoSwitchViewModel(AutoSwitch kind, bool on) : ObservableObject
{
    private bool on = on;

    public AutoSwitch Switch => kind;
    public string Label => Texts.Get(kind.Label);

    // The language of the window changed.
    public void Reworded() => OnPropertyChanged(nameof(Label));

    public bool On
    {
        get => on;
        set => SetProperty(ref on, value);
    }
}

// The Settings tab: what the fields hold and what "Save" makes of them. Numbers are texts as
// typed: comma and point both count; they are shown with the decimal sign of the window's
// language and never with a sign between thousands, which the next save would read as the
// decimal sign.
// store: writes the settings (and whatever is saved along with them); report: the status line.
public sealed class SettingsViewModel : ObservableObject
{
    private readonly Func<Settings, Told?> store;
    private readonly Action<StatusMessage> report;
    private Settings settings;
    private string[] known;
    private IReadOnlyList<LeagueChoice> leagues;
    private LeagueChoice league;
    private string floor;
    private string? unit;
    private string priceUnit;
    private bool startWithWindows;
    private bool extendedDiagnostics;
    private bool tradeInPerson;
    private bool captureGameWindow;
    private bool liveTradePrice;
    private bool checkForUpdates;
    private bool floorInvalid;
    private bool intervalInvalid;
    private bool glanceInvalid;
    private string interval;
    private string glance;
    private string toggleKey;
    private string diagnoseKey;
    private string exactKey;
    private string gameConfigFile;
    private bool gameConfigFileInvalid;

    public SettingsViewModel(Settings settings, IEnumerable<string> leagues, Func<Settings, Told?> store, Action<StatusMessage> report)
    {
        this.settings = settings;
        this.store = store;
        this.report = report;
        known = leagues.ToArray();
        this.leagues = LeagueChoices();
        league = this.leagues.First(c => c.Value == settings.League);
        // The language of the window changed: what is put together here follows at once
        // (the fixed texts of the window follow by themselves, views:Text).
        Texts.Changed += Reworded;
        floor = Shown(settings.FloorValue);
        // A saved unit the list does not know is shown as none and saved as "ex".
        unit = Units.Contains(settings.FloorUnit) ? settings.FloorUnit : null;
        priceUnit = PriceUnits.Contains(settings.PriceUnit) ? settings.PriceUnit : "ex";
        Switches = AutoSwitch.All.Select(a => new AutoSwitchViewModel(a, a.Get(settings))).ToArray();
        startWithWindows = settings.StartWithWindows;
        extendedDiagnostics = settings.ExtendedDiagnostics;
        tradeInPerson = settings.TradeInPerson;
        captureGameWindow = settings.CaptureGameWindow;
        liveTradePrice = settings.LiveTradePrice;
        checkForUpdates = settings.CheckForUpdates;
        interval = Shown(settings.AutoIntervalSeconds);
        glance = Shown(settings.GlanceIntervalSeconds);
        toggleKey = HotKey.Shown(settings.ToggleKey);
        diagnoseKey = HotKey.Shown(settings.DiagnoseKey);
        exactKey = settings.ExactKey.Length == 0 ? "" : HotKey.Shown(settings.ExactKey);
        gameConfigFile = string.IsNullOrWhiteSpace(settings.GameConfigFile) ? GameConfig.StandardFile : settings.GameConfigFile;
        BrowseGameConfigCommand = new RelayCommand(() =>
        {
            if (PickGameConfig?.Invoke(GameConfigFile) is { Length: > 0 } picked)
                GameConfigFile = picked;
        });
        SaveCommand = new RelayCommand(() => Save());
        ReloadPricesCommand = new RelayCommand(() => ReloadPrices?.Invoke());
    }

    public IReadOnlyList<LeagueChoice> Leagues
    {
        get => leagues;
        private set => SetProperty(ref leagues, value);
    }

    private IReadOnlyList<LeagueChoice> LeagueChoices()
    {
        var current = known.Length > 0 ? NinjaClient.DefaultLeague(known) : null;
        var choices = new[] { new LeagueChoice(null, current is null ? Texts.Get("LeagueAutoCurrent") : Texts.Get("LeagueAuto", current)) }
            .Concat(known.Select(l => new LeagueChoice(l, l)))
            .ToList();
        if (settings.League is not null && !known.Contains(settings.League))
            choices.Add(new LeagueChoice(settings.League, settings.League));
        return choices;
    }

    // The leagues of poe.ninja, once they are there: the program no longer waits for them
    // before its window shows, and a list that failed at the start comes with the next load
    // that succeeds. What is chosen stays chosen.
    public void SetLeagues(IEnumerable<string> names)
    {
        known = names.ToArray();
        string? chosen = League?.Value;
        Leagues = LeagueChoices();
        League = Leagues.FirstOrDefault(c => c.Value == chosen) ?? Leagues[0];
    }

    // The list of leagues ("Auto (…)"), the hint with the limits, the names of the switches,
    // the keys and the numbers in the new language. A number or key that cannot be read stays
    // as typed.
    private void Reworded()
    {
        string? chosen = League?.Value;
        Leagues = LeagueChoices();
        League = Leagues.FirstOrDefault(c => c.Value == chosen) ?? Leagues[0];
        OnPropertyChanged(nameof(TimesHint));
        foreach (var s in Switches)
            s.Reworded();
        ToggleKey = HotKey.Shown(ToggleKey);
        DiagnoseKey = HotKey.Shown(DiagnoseKey);
        ExactKey = ExactKey.Trim().Length == 0 ? "" : HotKey.Shown(ExactKey);
        if (Seconds(Interval, out double every))
            interval = Shown(every);
        if (Seconds(Glance, out double check))
            glance = Shown(check);
        if (Number(Floor, out decimal floorValue))
            floor = Shown(floorValue);
        OnPropertyChanged(nameof(Interval));
        OnPropertyChanged(nameof(Glance));
        OnPropertyChanged(nameof(Floor));
    }

    // The language was chosen and saved by the window's list (MainViewModel): the next save
    // of this page must not write the old one back.
    public void SetLanguage(string language) => settings = settings with { Language = language };
    // Likewise a hint closed with its "✕" (MainViewModel.CloseHint).
    public void SetHiddenHint(string hint) => settings = settings with { HiddenHint = hint };
    public IReadOnlyList<string> Units { get; } = ["ex", "chaos", "div"];
    // The unit prices are written in below one divine (Settings.PriceUnit).
    public IReadOnlyList<string> PriceUnits { get; } = ["ex", "chaos"];
    public IReadOnlyList<AutoSwitchViewModel> Switches { get; }
    public IRelayCommand SaveCommand { get; }
    public IRelayCommand ReloadPricesCommand { get; }

    // "Browse…" beside the game's settings file; PickGameConfig: the window's file dialog
    // (current path in, chosen path or null out).
    public IRelayCommand BrowseGameConfigCommand { get; }
    public Func<string, string?>? PickGameConfig { get; set; }

    // Where the game keeps its settings file (GameConfig). The field always shows a path:
    // with none set, the standard place — an empty field would ask for an input. Saved is ""
    // as long as it is the standard place, so that it follows when
    // Windows moves the documents folder.
    public string GameConfigFile
    {
        get => gameConfigFile;
        set
        {
            SetProperty(ref gameConfigFile, value);
            GameConfigFileInvalid = false;
        }
    }

    public bool GameConfigFileInvalid
    {
        get => gameConfigFileInvalid;
        private set => SetProperty(ref gameConfigFileInvalid, value);
    }

    // The hint below the two times, with their limits.
    public string TimesHint =>
        Texts.Get("TipIntervals", Tuning.Control.AutoMinimumSeconds, Tuning.Control.GlanceEvery.TotalSeconds, Tuning.Control.FollowEvery.TotalSeconds);

    public event Action<Settings>? Saved;

    // The Waystones tab, saved along with this page: whether its fields are in order, and
    // what they hold (MainViewModel sets it).
    public Func<(bool Valid, Core.Waystones.WaystoneSettings Settings)>? WaystonesSource { get; set; }
    public Func<(bool Valid, Core.Tablets.TabletSettings Settings)>? TabletsSource { get; set; }
    public event Action? ReloadPrices;

    public LeagueChoice League
    {
        get => league;
        set => SetProperty(ref league, value);
    }

    public string Floor
    {
        get => floor;
        set
        {
            SetProperty(ref floor, value);
            FloorInvalid = false;
        }
    }

    public string? Unit
    {
        get => unit;
        set => SetProperty(ref unit, value);
    }

    public string PriceUnit
    {
        get => priceUnit;
        set => SetProperty(ref priceUnit, value);
    }

    public bool StartWithWindows
    {
        get => startWithWindows;
        set => SetProperty(ref startWithWindows, value);
    }

    // The live price at waystone and tablet tooltips (Settings.LiveTradePrice).
    public bool LiveTradePrice
    {
        get => liveTradePrice;
        set => SetProperty(ref liveTradePrice, value);
    }

    // Trade prices with sellers online in person besides Instant Buyout (Settings.TradeInPerson).
    public bool TradeInPerson
    {
        get => tradeInPerson;
        set => SetProperty(ref tradeInPerson, value);
    }

    // The game read from its own window, not from the screen (Settings.CaptureGameWindow).
    public bool CaptureGameWindow
    {
        get => captureGameWindow;
        set => SetProperty(ref captureGameWindow, value);
    }

    public bool ExtendedDiagnostics
    {
        get => extendedDiagnostics;
        set => SetProperty(ref extendedDiagnostics, value);
    }

    public bool CheckForUpdates
    {
        get => checkForUpdates;
        set => SetProperty(ref checkForUpdates, value);
    }

    public string Interval
    {
        get => interval;
        set
        {
            SetProperty(ref interval, value);
            IntervalInvalid = false;
        }
    }

    public string Glance
    {
        get => glance;
        set
        {
            SetProperty(ref glance, value);
            GlanceInvalid = false;
        }
    }

    // A number that could not be read when saving: its field is marked until it is changed.
    public bool FloorInvalid
    {
        get => floorInvalid;
        private set => SetProperty(ref floorInvalid, value);
    }

    public bool IntervalInvalid
    {
        get => intervalInvalid;
        private set => SetProperty(ref intervalInvalid, value);
    }

    public bool GlanceInvalid
    {
        get => glanceInvalid;
        private set => SetProperty(ref glanceInvalid, value);
    }

    // A field given the key the other one holds: the two swap, the other takes what this one
    // held; refusing it would make exchanging the two take a third key.
    public string ToggleKey
    {
        get => toggleKey;
        set
        {
            string before = toggleKey;
            if (SetProperty(ref toggleKey, value) && Same(value, diagnoseKey) && !Same(before, value))
            {
                diagnoseKey = before;
                OnPropertyChanged(nameof(DiagnoseKey));
                report(new(Texts.Get("HotkeysSwapped")));
            }
            else if (Same(value, exactKey) && !Same(before, value))
            {
                exactKey = before;
                OnPropertyChanged(nameof(ExactKey));
                report(new(Texts.Get("HotkeysSwapped")));
            }
        }
    }

    public string DiagnoseKey
    {
        get => diagnoseKey;
        set
        {
            string before = diagnoseKey;
            if (SetProperty(ref diagnoseKey, value) && Same(value, toggleKey) && !Same(before, value))
            {
                toggleKey = before;
                OnPropertyChanged(nameof(ToggleKey));
                report(new(Texts.Get("HotkeysSwapped")));
            }
            else if (Same(value, exactKey) && !Same(before, value))
            {
                exactKey = before;
                OnPropertyChanged(nameof(ExactKey));
                report(new(Texts.Get("HotkeysSwapped")));
            }
        }
    }

    // The exact-price key; given the key of one of the other two, they swap as those two do.
    public string ExactKey
    {
        get => exactKey;
        set
        {
            string before = exactKey;
            if (!SetProperty(ref exactKey, value) || Same(before, value))
                return;
            if (Same(value, toggleKey))
            {
                toggleKey = before;
                OnPropertyChanged(nameof(ToggleKey));
                report(new(Texts.Get("HotkeysSwapped")));
            }
            else if (Same(value, diagnoseKey))
            {
                diagnoseKey = before;
                OnPropertyChanged(nameof(DiagnoseKey));
                report(new(Texts.Get("HotkeysSwapped")));
            }
        }
    }

    // The same key, however it is written; a text that names no key is the same as none.
    private static bool Same(string a, string b) => Readable(a) && Readable(b) && HotKey.Parse(a) == HotKey.Parse(b);

    // Numbers as the fields show them: with the decimal sign of the window's language (point
    // in English, comma in German), never with a sign between thousands.
    private static string Shown(double number) => number.ToString(CultureInfo.InvariantCulture).Replace(".", Texts.DecimalSeparator);

    private static string Shown(decimal number) => number.ToString(CultureInfo.InvariantCulture).Replace(".", Texts.DecimalSeparator);

    private static bool Number(string text, out decimal number) => Numbers.TryParse(text, out number);

    private static bool Seconds(string text, out double seconds) => Numbers.TryParse(text, out seconds);

    private static bool Readable(string key)
    {
        try
        {
            HotKey.Parse(key);
            return true;
        }
        catch (Exception e) when (HotKey.Unreadable(e))
        {
            return false;
        }
    }

    // False: an input cannot be read, nothing is saved and the status line says why.
    public bool Save()
    {
        // A key text that cannot be read is named as it stands, without the message of .NET.
        // The key fields take pressed keys only, so this is a settings file changed by hand.
        // The exact-price key may stay empty: it is then off.
        if (new[] { ToggleKey, DiagnoseKey, ExactKey }.Where(key => key != ExactKey || ExactKey.Trim().Length > 0).FirstOrDefault(key => !Readable(key)) is { } unknown)
        {
            report(StatusMessage.Error(Texts.Get("InvalidUnknownKey", unknown.Trim())));
            return false;
        }
        bool exactOff = ExactKey.Trim().Length == 0;
        if (HotKey.Parse(ToggleKey) == HotKey.Parse(DiagnoseKey) || !exactOff && (HotKey.Parse(ExactKey) == HotKey.Parse(ToggleKey) || HotKey.Parse(ExactKey) == HotKey.Parse(DiagnoseKey)))
        {
            report(StatusMessage.Error(Texts.Get("InvalidHotkeysIdentical")));
            return false;
        }
        IntervalInvalid = !Seconds(Interval, out double every);
        GlanceInvalid = !Seconds(Glance, out double check);
        FloorInvalid = !Number(Floor, out decimal floorValue);
        if (IntervalInvalid || GlanceInvalid || FloorInvalid)
        {
            report(StatusMessage.Error(Texts.Get("InvalidNotANumber")));
            return false;
        }
        // Another place than the standard one must hold the file: a mistyped path would
        // silently count as "no settings file" (popup size and game language at their standard).
        string config = GameConfigFile.Trim().Trim('"');
        bool standardPlace = config.Length == 0 || string.Equals(config, GameConfig.StandardFile, StringComparison.OrdinalIgnoreCase);
        if (!standardPlace && !System.IO.File.Exists(config))
        {
            GameConfigFileInvalid = true;
            report(StatusMessage.Error(Texts.Get("InvalidFileNotFound")));
            return false;
        }
        GameConfigFile = standardPlace ? GameConfig.StandardFile : config;
        // Both times have a lowest value the control keeps to anyway; the field shows
        // what counts instead of a 0 that would not.
        double everyKept = Math.Clamp(every, Tuning.Control.AutoMinimumSeconds, Tuning.Control.AutoMaximumSeconds),
            checkKept = Math.Clamp(check, Tuning.Control.GlanceEvery.TotalSeconds, Tuning.Control.FollowEvery.TotalSeconds);
        Interval = Shown(everyKept);
        Glance = Shown(checkKept);
        Floor = Shown(floorValue);
        var waystones = WaystonesSource?.Invoke();
        var tablets = TabletsSource?.Invoke();
        if (waystones is { Valid: false } || tablets is { Valid: false })
        {
            // The field marked red stands on another tab: it is named (review before 1.2.0).
            report(StatusMessage.Error(Texts.Get("InvalidInTab", Texts.Get(waystones is { Valid: false } ? "TabWaystones" : "TabTablets"))));
            return false;
        }
        var next = settings with
        {
            Waystones = waystones?.Settings ?? settings.Waystones,
            Tablets = tablets?.Settings ?? settings.Tablets,
            League = League?.Value,
            FloorValue = floorValue,
            FloorUnit = Unit ?? "ex",
            PriceUnit = PriceUnit,
            StartWithWindows = StartWithWindows,
            ExtendedDiagnostics = ExtendedDiagnostics,
            TradeInPerson = TradeInPerson,
            CaptureGameWindow = CaptureGameWindow,
            LiveTradePrice = LiveTradePrice,
            CheckForUpdates = CheckForUpdates,
            AutoIntervalSeconds = everyKept,
            GlanceIntervalSeconds = checkKept,
            ToggleKey = HotKey.Parse(ToggleKey).Stored,
            DiagnoseKey = HotKey.Parse(DiagnoseKey).Stored,
            ExactKey = exactOff ? "" : HotKey.Parse(ExactKey).Stored,
            GameConfigFile = standardPlace ? "" : config,
        };
        next = Switches.Aggregate(next, (n, s) => s.Switch.Set(n, s.On));
        // The settings file cannot be written: nothing counts, and the reason is named. What is
        // saved along with the settings and fails (the rumour ratings) does not undo them: they
        // are on the disk and are applied, and the status line names what was not saved.
        Told? notSaved;
        try
        {
            notSaved = store(next);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            report(StatusMessage.Error(Texts.Get("SettingsNotSaved", e.Message)));
            return false;
        }
        settings = next;
        Saved?.Invoke(next);
        report(notSaved is null ? StatusMessage.Ok(Texts.Get(everyKept != every || checkKept != check ? "SavedAdjusted" : "Saved")) : StatusMessage.Error(notSaved.Shown));
        return true;
    }
}
