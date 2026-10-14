using System.Diagnostics;

namespace WhyNotSleeping.Gui;

/// <summary>
/// The window: two verdict cards (sleep, screen), the power plan at a glance, and tabs for the power requests, input
/// devices, event log and ignored apps. Refreshes on a worker thread every few seconds.
/// </summary>
internal sealed class MainForm : Form
{
    /// <summary>Follows the Windows light/dark setting; set before the form is created.</summary>
    internal static Palette Theme { get; set; } = Palette.Light;

    static Color Background => Theme.Background;
    static Color Surface => Theme.Surface;
    static Color TextColor => Theme.Text;
    static Color Muted => Theme.Muted;
    static Color Red => Theme.Red;
    static Color Amber => Theme.Amber;
    static Color Green => Theme.Green;
    static Color Purple => Theme.Purple;
    static Color Blue => Theme.Blue;

    readonly TrayApp _app;
    readonly bool _isAdmin;
    readonly Settings _settings;
    readonly AwayGuard _away;
    long _eventsShown;
    readonly List<string> _eventHistory = [];
    readonly ComboBox _eventCategory = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    readonly TextBox _eventSearch = new() { Width = 200, PlaceholderText = "Search events" };
    readonly System.Windows.Forms.Timer _searchDelay = new() { Interval = 250 }; // filter once typing pauses
    readonly Button _exportLog;
    readonly string? _logPath;
    readonly SemaphoreSlim _overrideGate = new(1, 1);

    readonly Panel _banner;
    readonly StatusCard _sleepCard = new("SLEEP");
    readonly StatusCard _screenCard = new("SCREEN");
    readonly Fact _plan = new("Power plan"), _source = new("Power source"), _sleepAfter = new("Sleep after"),
        _screenAfter = new("Screen off after"), _idle = new("Idle for"), _screen = new("Screen is");
    readonly ItemList _requests, _inputs, _events, _overrides;
    readonly ItemList _controls = NewList(("Control", 170), ("Now", 90), ("Changes (last minute)", 150), ("Range (last minute)", 170), ("", 200));
    readonly System.Windows.Forms.Timer _live = new() { Interval = 150 };
    readonly Dictionary<string, (long? Value, DateTime Changed)> _seen = new(); // for lighting up what just moved
    long? _liveDevice;
    readonly Button _unignore;
    readonly ToolStripMenuItem _ignoreItem, _locateItem;
    readonly Label _status;
    readonly CheckBox _startWithWindows;
    readonly ItemList _awayList = NewList(("Device", 300), ("Now", 400));
    readonly CheckBox _awayEnabled, _awaySkipFullScreen;
    readonly NumericUpDown _awayMinutes;
    readonly RadioButton[] _awayActions;
    readonly Label _awayStatus;
    readonly Button _awayRemove;
    bool _syncing; // setting the controls from the settings, not the user changing them

    public MainForm(TrayApp app, bool isAdmin, string? logPath, Settings settings, AwayGuard away)
    {
        _app = app;
        _isAdmin = isAdmin;
        _settings = settings;
        _away = away;
        _logPath = logPath;

        Text = "WhyNotSleeping";
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Background;
        ForeColor = TextColor;
        ClientSize = new Size(1000, 720);
        MinimumSize = new Size(780, 660);
        StartPosition = FormStartPosition.CenterScreen;
        using (var icon = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico"))
            if (icon != null) Icon = new Icon(icon);

        // Limited-mode banner
        var bannerText = new Label
        {
            Text = "Limited mode: restart as administrator to see apps and drivers keeping the PC awake.",
            AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.BannerText, Margin = new Padding(0, 6, 12, 6),
        };
        var elevate = new Button { Text = "Restart as administrator", AutoSize = true, Anchor = AnchorStyles.Right, FlatStyle = FlatStyle.System };
        elevate.Click += (_, _) => _app.RestartElevated();
        var bannerLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        bannerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bannerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bannerLayout.Controls.Add(bannerText, 0, 0);
        bannerLayout.Controls.Add(elevate, 1, 0);
        _banner = new Panel
        {
            Dock = DockStyle.Fill, AutoSize = true, BackColor = Theme.BannerBack, Padding = new Padding(10, 4, 6, 4),
            Margin = new Padding(0, 0, 0, 10), Visible = !isAdmin,
        };
        _banner.Controls.Add(bannerLayout);

        // Header
        var title = new Label
        {
            Text = "Why isn't my PC sleeping?", AutoSize = true, Anchor = AnchorStyles.Left,
            Font = new Font("Segoe UI Semibold", 15f), Margin = new Padding(0, 0, 0, 4),
        };
        var powerSettings = new Button { Text = "Power && sleep settings", AutoSize = true, FlatStyle = FlatStyle.System };
        powerSettings.Click += (_, _) => Open("ms-settings:powersleep");
        var refresh = new Button { Text = "Refresh now", AutoSize = true, FlatStyle = FlatStyle.System };
        refresh.Click += (_, _) => _app.RefreshNow();
        _startWithWindows = new CheckBox
        {
            Text = "Start with Windows", AutoSize = true, Enabled = isAdmin, ForeColor = TextColor, Margin = new Padding(3, 7, 12, 3),
        };
        _startWithWindows.CheckedChanged += (_, _) =>
        {
            if (!_syncing && _startWithWindows.Checked != _app.StartsWithWindows) _app.SetStartWithWindows(_startWithWindows.Checked);
        };
        new ToolTip().SetToolTip(_startWithWindows, isAdmin
            ? "Starts WhyNotSleeping in the notification area when you sign in, without the administrator prompt."
            : "Needs administrator: restart WhyNotSleeping as administrator to change this.");
        var headerButtons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, WrapContents = false, Margin = Padding.Empty };
        headerButtons.Controls.AddRange([_startWithWindows, powerSettings, refresh]);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 8) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(headerButtons, 1, 0);

        // Verdict cards
        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 0, 0, 8) };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _sleepCard.Margin = new Padding(0, 0, 5, 0);
        _screenCard.Margin = new Padding(5, 0, 0, 0);
        cards.Controls.Add(_sleepCard, 0, 0);
        cards.Controls.Add(_screenCard, 1, 0);

        // Power plan at a glance
        var facts = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 8), BackColor = Surface, Padding = new Padding(6, 6, 6, 4) };
        facts.Controls.AddRange([_plan, _source, _sleepAfter, _screenAfter, _idle, _screen]);

        // Tabs
        _requests = NewList(("Keeps", 150), ("Who", 190), ("Why", 330), ("For", 80), ("Type", 80));
        _ignoreItem = new ToolStripMenuItem("Ignore this app...", null, (_, _) => IgnoreSelected());
        _locateItem = new ToolStripMenuItem("Open file location", null, (_, _) => LocateSelected());
        var copyItem = new ToolStripMenuItem("Copy details", null, (_, _) =>
        {
            if (Selected(_requests) is PowerRequest r) Clipboard.SetText($"{r.Describe()}");
        });
        var requestMenu = new ContextMenuStrip();
        requestMenu.Items.AddRange([_ignoreItem, _locateItem, new ToolStripSeparator(), copyItem]);
        requestMenu.Opening += (_, e) =>
        {
            var request = Selected(_requests) as PowerRequest;
            if (request is null) { e.Cancel = true; return; }
            _ignoreItem.Enabled = _isAdmin && request.Kind == "PROCESS";
            _locateItem.Enabled = File.Exists(request.Name);
        };
        _requests.ContextMenuStrip = requestMenu;

        _inputs = NewList(("Device", 100), ("Name", 250), ("Last input", 90), ("Idle resets", 80), ("Last minute", 85), ("What it's sending", 300));
        var turnOff = new ToolStripMenuItem("Turn off this device...", null, (_, _) => TurnOffSelected());
        var turnOn = new ToolStripMenuItem("Turn back on", null, (_, _) => TurnOnSelected());
        var awayItem = new ToolStripMenuItem("Add to the away group", null, (_, _) => ToggleAwayGroup());
        var inputMenu = new ContextMenuStrip();
        inputMenu.Items.AddRange([turnOff, turnOn, new ToolStripSeparator(), awayItem]);
        inputMenu.Opening += (_, e) =>
        {
            var selected = Selected(_inputs);
            if (selected is null) { e.Cancel = true; return; }
            var needsAdmin = _isAdmin ? "" : " (needs administrator)";
            turnOff.Visible = selected is InputSource;
            turnOff.Enabled = CanTurnOff(selected);
            turnOff.Text = selected is InputSource { Kind: InputKind.Mouse or InputKind.Keyboard }
                ? "Turn off this device (not for mice and keyboards)"
                : "Turn off this device..." + needsAdmin;
            turnOn.Visible = selected is TurnedOffDevice;
            turnOn.Enabled = _isAdmin;
            turnOn.Text = "Turn back on" + needsAdmin;
            awayItem.Visible = CanJoinAwayGroup(selected);
            awayItem.Text = _settings.InAwayGroup((selected as InputSource)?.InstanceId)
                ? "Remove from the away group"
                : "Add to the away group (turn it off when I'm away)...";
        };
        _inputs.ContextMenuStrip = inputMenu;
        _events = NewList(("Time", 150), ("Event", 760));
        _overrides = NewList(("Type", 100), ("Name", 300), ("Ignored requests", 260));

        _unignore = new Button { Text = "Stop ignoring", AutoSize = true, Enabled = false, FlatStyle = FlatStyle.System };
        _unignore.Click += (_, _) => UnignoreSelected();
        _overrides.SelectedIndexChanged += (_, _) => _unignore.Enabled = _isAdmin && Selected(_overrides) is RequestOverride;

        var openLog = new Button { Text = "Open log file", AutoSize = true, Enabled = logPath != null, FlatStyle = FlatStyle.System };
        openLog.Click += (_, _) => { if (File.Exists(logPath)) Open(logPath); };
        var openFolder = new Button { Text = "Open log folder", AutoSize = true, Enabled = logPath != null, FlatStyle = FlatStyle.System };
        openFolder.Click += (_, _) => { if (Path.GetDirectoryName(logPath) is { } dir) Open(dir); };
        _exportLog = new Button { Text = "Export filtered log...", AutoSize = true, FlatStyle = FlatStyle.System };
        _exportLog.Click += async (_, _) => await ExportLogAsync();
        _eventCategory.Items.AddRange(["All events", "Screen changes", "Input", "Power requests", "Device actions", "Power & sleep", "Other"]);
        _eventCategory.SelectedIndex = 0;
        _eventCategory.SelectedIndexChanged += (_, _) => RefreshEvents();
        _searchDelay.Tick += (_, _) => RefreshEvents();
        _eventSearch.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        var eventFilters = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 6) };
        eventFilters.Controls.AddRange([
            new Label { Text = "Show", AutoSize = true, Margin = new Padding(0, 5, 6, 0) }, _eventCategory, _eventSearch,
        ]);
        var eventLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = Padding.Empty };
        eventLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        eventLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        eventLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        eventLayout.Controls.Add(eventFilters, 0, 0);
        eventLayout.Controls.Add(Cell(_events), 0, 1);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
        tabs.TabPages.Add(Page("Keeping it awake",
            "Apps, services and drivers asking Windows to keep the PC or the screen awake. Right-click one for options.", _requests));
        // Input devices on top, the selected device's axes and buttons live below.
        var inputSplit = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = Padding.Empty };
        inputSplit.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        inputSplit.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inputSplit.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        inputSplit.Controls.Add(Cell(_inputs), 0, 0);
        inputSplit.Controls.Add(new Label
        {
            Text = "Controls of the selected device, live: move a stick or press a button and its row lights up.",
            AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 10, 0, 6),
        }, 0, 1);
        inputSplit.Controls.Add(Cell(_controls), 0, 2);
        var inputsPage = Page("Input devices",
            "Every device that sent input since WhyNotSleeping started. Only input that resets the idle timer delays sleep and screen-off.", inputSplit);
        tabs.TabPages.Add(inputsPage);
        Fill(_controls, [], "Select a controller above to see its axes and buttons.");
        // Polled rather than driven by SelectedIndexChanged, which also fires while a refresh rebuilds the list.
        _live.Tick += (_, _) => { if (tabs.SelectedTab == inputsPage) ShowControls(); };
        _live.Start();

        // When I'm away: the away group and when to turn it off
        _awayEnabled = new CheckBox
        {
            Text = "Turn the devices below off when nobody has used the keyboard, mouse or a controller for", AutoSize = true, ForeColor = TextColor,
            Margin = new Padding(0, 3, 4, 3),
        };
        _awayMinutes = new NumericUpDown { Minimum = 1, Maximum = 240, Width = 56, BackColor = Surface, ForeColor = TextColor, Margin = new Padding(0, 1, 4, 0) };
        var awayWhen = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
        awayWhen.Controls.AddRange([_awayEnabled, _awayMinutes,
            new Label { Text = "minutes and all they send is noise.", AutoSize = true, Margin = new Padding(0, 4, 0, 3) }]);
        _awayActions =
        [
            new RadioButton { Text = "Then let Windows turn the screen off and sleep on its usual schedule", Tag = AwayAction.DevicesOnly },
            new RadioButton { Text = "Then turn the screen off right away", Tag = AwayAction.ScreenOff },
            new RadioButton { Text = "Then put the PC to sleep right away", Tag = AwayAction.Sleep },
        ];
        var awayThen = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(18, 2, 0, 0) };
        foreach (var radio in _awayActions)
        {
            radio.AutoSize = true;
            radio.ForeColor = TextColor;
            radio.Margin = new Padding(0, 1, 0, 1);
            radio.CheckedChanged += (_, _) => { if (radio.Checked) AwaySettingsChanged(); };
        }
        awayThen.Controls.AddRange(_awayActions);
        _awaySkipFullScreen = new CheckBox
        {
            Text = "Not while a full-screen game, video or presentation is running", AutoSize = true, ForeColor = TextColor,
            Margin = new Padding(0, 6, 0, 2),
        };
        _awayStatus = new Label { AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Font = new Font("Segoe UI Semibold", 9.75f), Margin = new Padding(0, 8, 0, 8) };
        _awayEnabled.CheckedChanged += (_, _) => AwaySettingsChanged();
        _awayMinutes.ValueChanged += (_, _) => AwaySettingsChanged();
        _awaySkipFullScreen.CheckedChanged += (_, _) => AwaySettingsChanged();
        _awayRemove = new Button { Text = "Remove from the group", AutoSize = true, Enabled = false, FlatStyle = FlatStyle.System };
        _awayRemove.Click += (_, _) => { if (Selected(_awayList) is TurnedOffDevice d) RemoveFromAwayGroup(d.InstanceId); };
        _awayList.SelectedIndexChanged += (_, _) => _awayRemove.Enabled = Selected(_awayList) is TurnedOffDevice;
        var awayLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = Padding.Empty };
        awayLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) awayLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        awayLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        awayLayout.Controls.Add(awayWhen, 0, 0);
        awayLayout.Controls.Add(awayThen, 0, 1);
        awayLayout.Controls.Add(_awaySkipFullScreen, 0, 2);
        awayLayout.Controls.Add(_awayStatus, 0, 3);
        awayLayout.Controls.Add(Cell(_awayList), 0, 4);
        tabs.TabPages.Add(Page("When I'm away",
            "Devices that keep the PC awake when nobody is using them, like a joystick with a noisy sensor. Add one from Input devices (right-click it). " +
            "They come back on as soon as you move the mouse, press a key, use a controller or unlock the PC, and after a restart.", awayLayout, _awayRemove));
        SyncAwaySettings();

        tabs.TabPages.Add(Page("Event log",
            logPath is null ? "Recent events, newest first. Export saves the events shown by your filters."
                : $"Recent events, newest first. Export applies your filters to the full saved log: {logPath}", eventLayout, _exportLog, openLog, openFolder));
        tabs.TabPages.Add(Page("Ignored apps",
            "Apps whose requests Windows disregards (powercfg /requestsoverride).", _overrides, _unignore));

        _status = new Label { AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 6, 0, 0), Text = "Starting..." };

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14, 12, 14, 8) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // the window's width, so long lines wrap instead of widening it
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 168));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_banner, 0, 0);
        root.Controls.Add(header, 0, 1);
        root.Controls.Add(cards, 0, 2);
        root.Controls.Add(facts, 0, 3);
        root.Controls.Add(tabs, 0, 4);
        root.Controls.Add(_status, 0, 5);
        Controls.Add(root);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // A second copy asks this one to show itself; let that through even when this one is elevated and it isn't.
        Native.ChangeWindowMessageFilterEx(Handle, SingleInstance.ShowMessage, Native.MsgfltAllow, IntPtr.Zero);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == SingleInstance.ShowMessage && SingleInstance.ShowMessage != 0)
            _app.ShowWindow();
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window hides it; monitoring and the away group go on. Exit is in the tray menu. (None: a plain
        // WM_CLOSE from another program, which is still someone closing the window.)
        if (e.CloseReason is CloseReason.UserClosing or CloseReason.None && !_app.Exiting)
        {
            e.Cancel = true;
            Hide();
            _app.Hidden();
        }
        base.OnFormClosing(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        // Nothing is drawn while hidden; catch up on showing.
        if (Visible)
        {
            _live.Start();
            ShowStartup();
            ShowAway();
            _app.RefreshNow();
        }
        else
        {
            _live.Stop();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _live.Stop();
        base.OnFormClosed(e);
    }

    internal static string SleepHeadline(Report r) => r.Sleep.Max(f => f.Severity) switch
    {
        Severity.Problem => "Your PC is being kept awake",
        Severity.Warning or Severity.Unknown => "Something may be keeping your PC awake",
        _ => "Nothing is blocking sleep",
    };

    internal static string ScreenHeadline(Report r) => r.Display == DisplayState.Off ? "The screen is off" : r.Screen.Max(f => f.Severity) switch
    {
        Severity.Problem => "Something is keeping the screen on",
        Severity.Warning or Severity.Unknown => "Something may be keeping the screen on",
        _ => "Nothing is keeping the screen on",
    };

    internal void ShowError(string message) => _status.Text = $"Refresh failed: {message}";

    internal void Display(Report r)
    {
        var s = r.Snapshot;
        var p = s.Power;

        _sleepCard.SetFindings(r.Sleep, SleepHeadline(r));
        _screenCard.SetFindings(r.Screen, ScreenHeadline(r));

        _plan.Set(p.SchemeName);
        _source.Set((p.OnAC ? "Plugged in" : "Battery") + (p.BatteryPercent is { } b ? $" ({b}%)" : ""));
        _sleepAfter.Set(Format.Timeout(p.SleepTimeout), p.SleepTimeout == 0 ? Red : null);
        _screenAfter.Set(Format.Timeout(p.DisplayTimeout), p.DisplayTimeout == 0 ? Red : null);
        _idle.Set(Format.Duration(p.UserIdle));
        _screen.Set(r.Display == DisplayState.Unknown ? "unknown" : r.Display.ToString().ToLowerInvariant());

        ShowRequests(r);
        ShowInputs(s);
        ShowEvents(r);
        ShowOverrides(s);

        _banner.Visible = !_isAdmin;
        _status.Text = $"Updated {s.Time:HH:mm:ss}, refreshes every {_app.IntervalSeconds} s. " +
                       $"Idle time: {r.LastInputDescription}. Closing this window keeps it running in the notification area.";
    }

    internal void ShowStartup()
    {
        _syncing = true;
        _startWithWindows.Checked = _app.StartsWithWindows;
        _syncing = false;
    }

    /// <summary>The away group's status line and its devices, refreshed every second while the window is open.</summary>
    internal void ShowAway()
    {
        _awayStatus.Text = _away.Status;
        _awayStatus.ForeColor = _away.VerdictGroup?.Failures.Count > 0 ? Red
            : _away.PauseReason != null ? Amber : _away.IsAway ? Blue : _settings.AwayEnabled ? TextColor : Muted;
        var now = DateTime.Now;
        var rows = _settings.AwayDevices.Select(d => new ListViewItem([d.Name, _away.Describe(d, now)])
        {
            Tag = d,
            ToolTipText = d.InstanceId,
            ForeColor = _away.IsOff(d.InstanceId) ? Blue : TextColor,
        }).ToList();
        // Update in place when it's the same devices, so the selection and tooltips hold still.
        if (_awayList.Items.Count == rows.Count && rows.Count > 0 &&
            rows.Select((r, i) => (r, i)).All(x => _awayList.Items[x.i].Tag == x.r.Tag))
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var item = _awayList.Items[i];
                if (item.SubItems[1].Text != rows[i].SubItems[1].Text) item.SubItems[1].Text = rows[i].SubItems[1].Text;
                item.ForeColor = rows[i].ForeColor;
            }
            return;
        }
        Fill(_awayList, rows, "The away group is empty.");
        _awayRemove.Enabled = Selected(_awayList) is TurnedOffDevice;
    }

    void SyncAwaySettings()
    {
        _syncing = true;
        _awayEnabled.Checked = _settings.AwayEnabled;
        _awayMinutes.Value = Math.Clamp(_settings.AwayMinutes, (int)_awayMinutes.Minimum, (int)_awayMinutes.Maximum);
        foreach (var radio in _awayActions)
            radio.Checked = (AwayAction)radio.Tag! == _settings.AwayAction;
        _awaySkipFullScreen.Checked = _settings.AwaySkipFullScreen;
        _syncing = false;
    }

    void AwaySettingsChanged()
    {
        if (_syncing) return;
        _settings.AwayEnabled = _awayEnabled.Checked;
        _settings.AwayMinutes = (int)_awayMinutes.Value;
        _settings.AwayAction = _awayActions.FirstOrDefault(r => r.Checked)?.Tag as AwayAction? ?? AwayAction.ScreenOff;
        _settings.AwaySkipFullScreen = _awaySkipFullScreen.Checked;
        _settings.Save();
    }

    /// <summary>Same devices as turning off: a mouse or keyboard in the group would leave nothing to come back with.</summary>
    static bool CanJoinAwayGroup(object? selected) =>
        selected is InputSource { InstanceId: not null, Kind: not (InputKind.Mouse or InputKind.Keyboard or InputKind.Software) };

    void ToggleAwayGroup()
    {
        if (Selected(_inputs) is not InputSource { InstanceId: { } id } device || !CanJoinAwayGroup(device)) return;
        if (_settings.InAwayGroup(id))
        {
            RemoveFromAwayGroup(id);
            return;
        }
        var first = !_settings.AwayEnabled;
        if (first)
        {
            var answer = MessageBox.Show(this,
                $"When nobody has used the keyboard, mouse or a controller for {_settings.AwayMinutes} minutes and \"{device.ShortName}\" is only sending noise, " +
                "WhyNotSleeping will turn it off, like Disable in Device Manager, so the screen can turn off and the PC can sleep.\n\n" +
                "It comes back on as soon as you move the mouse, press a key, use a controller or unlock the PC, and after a restart. " +
                "Playing with it, or with any other controller, keeps it on.\n\n" +
                "Add it to the away group and turn this on? You can change the time and what happens next in the \"When I'm away\" tab.",
                "Away group", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
            _settings.AwayEnabled = true;
        }
        _settings.AwayDevices.Add(new TurnedOffDevice(id, device.ShortName));
        _settings.Save();
        SyncAwaySettings();
        ShowAway();
        _app.RefreshNow();
    }

    void RemoveFromAwayGroup(string instanceId)
    {
        _settings.AwayDevices.RemoveAll(d => d.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        ShowAway();
        _app.RefreshNow();
    }

    void ShowRequests(Report r)
    {
        var empty = r.Snapshot.PowerCfgError is { } error ? $"Couldn't ask Windows this time: {error}"
            : r.Snapshot.Requests == null ? "Restart as administrator to see which apps and drivers are keeping the PC awake."
            : "Nothing is asking Windows to stay awake right now.";
        Fill(_requests, r.Active.Select(a => new ListViewItem([
            FriendlyCategory(a.Request.Category), a.Request.ShortName, a.Request.Reason,
            Format.Duration(r.Snapshot.Time - a.Since), FriendlyKind(a.Request.Kind)])
        {
            Tag = a.Request,
            ToolTipText = a.Request.Name,
            ForeColor = a.Request.BlocksSleep ? Red : a.Request.KeepsDisplayOn ? Amber : ForeColor,
        }), empty);
    }

    void ShowInputs(Snapshot s)
    {
        var empty = InputMonitor.Available ? "No input seen yet." : "Raw input is unavailable.";
        // A device turned off here is listed as off, even after a restart, so it can always be turned back on.
        var off = s.TurnedOff.Select(d => d.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var active = s.Inputs.Where(i => i.InstanceId is null || !off.Contains(i.InstanceId)).Select(i => new ListViewItem([
            _away.IsOff(i.InstanceId) ? "off (away)" : InputMonitor.Label(i.Kind),
            i.ShortName + (_settings.InAwayGroup(i.InstanceId) ? " (away group)" : ""),
            Format.Duration(s.Time - i.LastInput) + " ago",
            i.IdleResets.ToString("N0"), i.ResetsLastMinute.ToString("N0"), i.WhatItSends ?? ""])
        {
            Tag = i,
            ToolTipText = (i.Suspicious ? "This device keeps resetting the idle timer. If nobody is using it, right-click it to turn it off, " +
                                          "or to add it to the away group.\n" : "") +
                          i.Name + (i.WhatItSends is { } what ? "\n" + what : ""),
            ForeColor = _away.IsOff(i.InstanceId) ? Muted : i.Suspicious ? Red : i.IdleResets == 0 ? Muted : ForeColor,
        });
        var turnedOff = s.TurnedOff.Select(d => new ListViewItem(["turned off", d.Name, "", "", "", "Turned off by WhyNotSleeping. Right-click to turn it back on."])
        {
            Tag = d,
            ToolTipText = d.InstanceId,
            ForeColor = Muted,
        });
        Fill(_inputs, active.Concat(turnedOff), empty);
    }

    bool CanTurnOff(object? selected) => _isAdmin &&
        selected is InputSource { InstanceId: not null, Kind: not (InputKind.Mouse or InputKind.Keyboard or InputKind.Software) };

    async void TurnOffSelected()
    {
        if (Selected(_inputs) is not InputSource device || !CanTurnOff(device)) return;
        var answer = MessageBox.Show(this,
            $"Windows will turn off \"{device.ShortName}\", like Disable in Device Manager.\n\n" +
            "Games and apps won't see it, and it stays off, even after a restart or replugging it, " +
            "until you turn it back on: right-click it here and choose \"Turn back on\", or enable it in Device Manager.\n\n" +
            "Turn it off?",
            "Turn off this device", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;
        var error = await Task.Run(() => DeviceSwitch.TurnOff(device.InstanceId!, device.ShortName));
        AfterDeviceSwitch(error, device.ShortName, off: true);
    }

    async void TurnOnSelected()
    {
        if (Selected(_inputs) is not TurnedOffDevice device || !_isAdmin) return;
        AfterDeviceSwitch(await Task.Run(() => DeviceSwitch.TurnOn(device.InstanceId)), device.Name, off: false);
    }

    void AfterDeviceSwitch(string? error, string name, bool off)
    {
        _app.Log(error is null ? $"* device: turned {(off ? "off" : "on")} {name}"
            : $"* device: couldn't turn {(off ? "off" : "on")} {name}: {error}");
        if (IsDisposed) return;
        if (error != null)
            MessageBox.Show(this, error, "WhyNotSleeping", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _app.RefreshNow();
    }

    /// <summary>
    /// The selected device's axes and buttons, refreshed several times a second while the tab is open. A row lights
    /// up for a moment when its value changes, which is how to find which physical control "vendor channel 2" is.
    /// </summary>
    void ShowControls()
    {
        if (Selected(_inputs) is not InputSource device)
        {
            _liveDevice = null;
            Fill(_controls, [], "Select a controller above to see its axes and buttons.");
            return;
        }
        if (_liveDevice != device.Id)
        {
            _liveDevice = device.Id;
            _seen.Clear();
        }

        var now = DateTime.Now;
        var controls = InputMonitor.Controls(device.Id);
        var rows = controls.Select(c =>
        {
            var changed = _seen.TryGetValue(c.Name, out var seen) && seen.Value != c.Value ? now : seen.Changed;
            _seen[c.Name] = (c.Value, changed);
            var lit = now - changed < TimeSpan.FromSeconds(1);
            var note = c.Jitter ? "jittering: a noisy sensor sends input while nobody touches it"
                : c.Unattended ? "changing nonstop with the keyboard and mouse idle"
                : lit ? (c.IsAxis ? "moving" : "pressed")
                : "";
            return (Key: c.Name, Cells: new[]
            {
                c.Name,
                c.DisplayValue,
                c.Changes == 0 ? "" : c.Changes.ToString("N0"),
                c.IsAxis && c.Min != null ? $"{c.Min}–{c.Max}" : "",
                note,
            }, Color: c.OnItsOwn ? Red : lit ? Blue : c.Changes == 0 ? Muted : TextColor, Status: c);
        }).ToList();

        // Update in place when the rows are the same controls, so the list doesn't flicker or lose its scroll position.
        if (_controls.Items.Count == rows.Count && rows.Count > 0 &&
            rows.Select((r, i) => (r, i)).All(x => (_controls.Items[x.i].Tag as ControlStatus)?.Name == x.r.Key))
        {
            _controls.BeginUpdate();
            for (var i = 0; i < rows.Count; i++)
            {
                var item = _controls.Items[i];
                item.Tag = rows[i].Status;
                item.ForeColor = rows[i].Color;
                for (var c = 0; c < rows[i].Cells.Length; c++)
                    if (item.SubItems[c].Text != rows[i].Cells[c]) item.SubItems[c].Text = rows[i].Cells[c];
            }
            _controls.EndUpdate();
            return;
        }
        Fill(_controls, rows.Select(r => new ListViewItem(r.Cells) { Tag = r.Status, ForeColor = r.Color }),
            device.Kind is InputKind.Mouse or InputKind.Keyboard
                ? "Mice and keyboards have no axes to show here; the list above says what they sent."
                : "Windows doesn't describe this device's controls.");
    }

    const int MaxEvents = 1000;

    /// <summary>
    /// Keeps recent history apart from the filters, so changing a filter brings older rows back. New events are only
    /// added (newest first), so the list keeps its scroll position and selection.
    /// </summary>
    void ShowEvents(Report r)
    {
        var fresh = (int)Math.Min(r.TotalEvents - _eventsShown, r.Events.Count);
        _eventsShown = r.TotalEvents;
        if (fresh <= 0) return;
        var lines = r.Events.TakeLast(fresh).ToList();
        _eventHistory.AddRange(lines);
        if (_eventHistory.Count > MaxEvents) _eventHistory.RemoveRange(0, _eventHistory.Count - MaxEvents);
        if (_events.Items.Count == 0)
        {
            RefreshEvents(); // also hides the "no events" text
            return;
        }
        var matching = lines.Where(line => EventLogView.Matches(line, SelectedEventCategory, _eventSearch.Text)).ToList();
        if (matching.Count == 0) return;
        _events.BeginUpdate();
        foreach (var line in matching) _events.Items.Insert(0, EventItem(line));
        while (_events.Items.Count > MaxEvents) _events.Items.RemoveAt(_events.Items.Count - 1);
        _events.EndUpdate();
    }

    EventCategory SelectedEventCategory => (EventCategory)Math.Max(_eventCategory.SelectedIndex, 0);

    void RefreshEvents()
    {
        _searchDelay.Stop();
        var lines = _eventHistory.AsEnumerable().Reverse().Where(line => EventLogView.Matches(line, SelectedEventCategory, _eventSearch.Text));
        Fill(_events, lines.Select(EventItem), "No events match these filters.");
    }

    static ListViewItem EventItem(string line)
    {
        var (time, text) = EventLogView.Split(line);
        return new ListViewItem([time, text.Trim()])
        {
            Tag = line,
            ToolTipText = text,
            ForeColor = (EventLogView.Category(line) == EventCategory.DeviceActions && text.Contains("couldn't", StringComparison.OrdinalIgnoreCase)) ? Red
                : text.StartsWith("+ new") ? Amber : text.StartsWith("- released") ? Green
                : text.StartsWith("* input") ? Purple : Blue,
        };
    }

    async Task ExportLogAsync()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Export filtered event log",
            Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"whynotsleeping-{DateTime.Now:yyyy-MM-dd-HHmmss}.log",
            DefaultExt = "log",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (_logPath != null && Path.GetFullPath(dialog.FileName).Equals(Path.GetFullPath(_logPath), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Choose a different file so monitoring can keep writing to its log.", "Export log", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var category = SelectedEventCategory;
        var search = _eventSearch.Text;
        var recent = _eventHistory.ToArray();
        _exportLog.Enabled = false;
        try
        {
            var count = await Task.Run(() => EventLogView.Export(dialog.FileName,
                _logPath != null && File.Exists(_logPath) ? EventLogView.ReadFile(_logPath) : recent, category, search));
            if (!IsDisposed) _status.Text = $"Exported {count} events to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!IsDisposed) MessageBox.Show(this, ex.Message, "Couldn't export the log", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { if (!IsDisposed) _exportLog.Enabled = true; }
    }

    void ShowOverrides(Snapshot s)
    {
        Fill(_overrides, s.Overrides.Select(o => new ListViewItem([FriendlyKind(o.CallerType), o.Name, string.Join(", ", o.Requests.Select(FriendlyCategory))]) { Tag = o }),
            "No apps are being ignored.");
        _unignore.Enabled = _isAdmin && Selected(_overrides) is RequestOverride;
    }

    async void IgnoreSelected()
    {
        if (Selected(_requests) is not PowerRequest { Kind: "PROCESS" } request) return;
        var name = Path.GetFileName(request.Name);
        var answer = MessageBox.Show(this,
            $"Windows will ignore requests from {name} to keep the {(request.KeepsDisplayOn ? "screen on" : "PC awake")}.\n\n" +
            $"Your PC may then sleep while {name} is playing audio, downloading or presenting. " +
            "You can undo this from the Ignored apps tab.\n\nIgnore it?",
            "Ignore this app", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        // powercfg replaces an app's override, so keep the request types it already ignores.
        await ChangeOverrideAsync(() =>
        {
            var existing = PowerCfg.GetOverrides().FirstOrDefault(o => o.CallerType == "PROCESS" && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var requests = (existing?.Requests ?? Array.Empty<string>()).Append(request.Category).Distinct().ToList();
            return PowerCfg.SetOverride("PROCESS", name, requests);
        });
    }

    async void UnignoreSelected()
    {
        if (Selected(_overrides) is not RequestOverride o) return;
        await ChangeOverrideAsync(() => PowerCfg.SetOverride(o.CallerType, o.Name, []));
    }

    async Task ChangeOverrideAsync(Func<string?> change)
    {
        // Each command replaces an app's override: keep consecutive clicks from losing each other's request types.
        await _overrideGate.WaitAsync();
        try
        {
            if (IsDisposed) return;
            var error = await Task.Run(change);
            if (!IsDisposed) AfterOverride(error);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (!IsDisposed) AfterOverride(ex.Message);
        }
        finally { _overrideGate.Release(); }
    }

    void AfterOverride(string? error)
    {
        if (error != null)
            MessageBox.Show(this, error, "powercfg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _app.RefreshNow();
    }

    void LocateSelected()
    {
        if (Selected(_requests) is PowerRequest request && File.Exists(request.Name))
            Process.Start("explorer.exe", $"/select,\"{request.Name}\"");
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    static string FriendlyCategory(string category) => category switch
    {
        "SYSTEM" => "Keeps PC awake",
        "EXECUTION" => "Keeps PC awake (app)",
        "AWAYMODE" => "Away mode",
        "DISPLAY" => "Keeps screen on",
        _ => category,
    };

    static string FriendlyKind(string kind) => kind switch
    {
        "PROCESS" => "App",
        "SERVICE" => "Service",
        "DRIVER" => "Driver",
        _ => kind,
    };

    static object? Selected(ListView list) => list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag : null;

    static ItemList NewList(params (string Name, int Width)[] columns)
    {
        var list = new ItemList
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
            ShowItemToolTips = true, BorderStyle = BorderStyle.None, HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BackColor = Surface, ForeColor = TextColor,
        };
        foreach (var (name, width) in columns)
            list.Columns.Add(name, width);
        return list;
    }

    /// <summary>Replaces a list's items, keeping the selection and scroll position; shows the empty text instead when there are none.</summary>
    static void Fill(ItemList list, IEnumerable<ListViewItem> items, string empty)
    {
        var selected = list.SelectedItems.Count > 0 ? Key(list.SelectedItems[0]) : null;
        var top = list.TopItem?.Index ?? 0;
        list.BeginUpdate();
        list.Items.Clear();
        list.Items.AddRange(items.ToArray());
        if (selected != null && list.Items.Cast<ListViewItem>().FirstOrDefault(i => Key(i) == selected) is { } again)
            again.Selected = true;
        if (top > 0 && top < list.Items.Count)
            list.TopItem = list.Items[top];
        list.EndUpdate();
        list.Empty.Text = empty;
        list.Empty.Visible = list.Items.Count == 0;
        list.Visible = list.Items.Count > 0;

        static string? Key(ListViewItem item) => item.Tag switch
        {
            PowerRequest r => r.Key,
            RequestOverride o => o.ToString(),
            TurnedOffDevice d => "off:" + d.InstanceId,
            string s => s,
            InputSource i => i.Id.ToString(),
            ControlStatus c => c.Name,
            _ => null,
        };
    }

    static TabPage Page(string title, string description, Control content, params Button[] buttons)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // so long descriptions wrap
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = description, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, ForeColor = Muted, Margin = new Padding(0, 0, 0, 6) }, 0, 0);
        layout.Controls.Add(content is ItemList list ? Cell(list) : content, 0, 1);
        if (buttons.Length > 0)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
            row.Controls.AddRange(buttons);
            layout.Controls.Add(row, 0, 2);
        }
        var page = new TabPage(title) { BackColor = Surface, UseVisualStyleBackColor = false };
        page.Controls.Add(layout);
        return page;
    }

    /// <summary>A list with its empty-state text in the same spot, and its last column stretched to the width.</summary>
    static Panel Cell(ItemList list)
    {
        var cell = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        cell.Controls.AddRange([list, list.Empty]);
        list.Resize += (_, _) =>
        {
            if (list.Columns.Count == 0) return;
            var others = list.Columns.Cast<ColumnHeader>().SkipLast(1).Sum(c => c.Width);
            list.Columns[^1].Width = Math.Max(list.ClientSize.Width - others, 120);
        };
        return cell;
    }

    /// <summary>A flicker-free list with a message to show in its place when it is empty.</summary>
    sealed class ItemList : ListView
    {
        public readonly Label Empty = new() { Dock = DockStyle.Fill, ForeColor = Muted, Visible = false, Padding = new Padding(2, 4, 0, 0) };

        public ItemList() => DoubleBuffered = true;
    }

    /// <summary>A verdict: colored stripe, headline and the findings behind it.</summary>
    sealed class StatusCard : Panel
    {
        readonly Panel _stripe = new() { Dock = DockStyle.Left, Width = 6 };
        readonly Label _headline = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 13f), Margin = new Padding(0, 2, 0, 6) };
        // Scrolls when there are more findings than fit, instead of cutting them off.
        readonly RichTextBox _details = new()
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.Vertical,
            TabStop = false, Cursor = Cursors.Default, ForeColor = TextColor,
        };

        public StatusCard(string title)
        {
            Dock = DockStyle.Fill;
            BackColor = Surface;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14, 10, 12, 8) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI Semibold", 8.5f) }, 0, 0);
            layout.Controls.Add(_headline, 0, 1);
            layout.Controls.Add(_details, 0, 2);
            Controls.Add(layout);
            Controls.Add(_stripe);
        }

        public void SetFindings(List<Finding> findings, string headline)
        {
            var worst = findings.Max(f => f.Severity);
            var accent = worst switch
            {
                Severity.Problem => Red,
                Severity.Warning or Severity.Unknown => Amber,
                _ => Green,
            };
            _stripe.BackColor = accent;
            BackColor = Palette.Blend(Surface, accent, 0.06); // a hint of the verdict, not a wash of it
            _details.BackColor = BackColor;
            _headline.ForeColor = accent;
            _headline.Text = headline;
            var details = string.Join("\n", findings.Select(f => "• " + f.Text));
            if (_details.Text != details) _details.Text = details; // setting it scrolls back to the top
        }
    }

    /// <summary>A caption over a value, e.g. "Sleep after" / "30m 0s".</summary>
    sealed class Fact : TableLayoutPanel
    {
        readonly Label _value = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), Margin = Padding.Empty };

        public Fact(string caption)
        {
            AutoSize = true;
            ColumnCount = 1;
            Margin = new Padding(8, 2, 22, 4);
            Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = Muted, Margin = Padding.Empty }, 0, 0);
            Controls.Add(_value, 0, 1);
        }

        public void Set(string value, Color? color = null)
        {
            _value.Text = value;
            _value.ForeColor = color ?? ForeColor;
        }
    }
}
