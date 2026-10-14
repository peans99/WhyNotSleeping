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

    readonly bool _isAdmin;
    readonly SleepMonitor _monitor;
    TaskCompletionSource _wake = new();
    long _eventsShown;

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

    public MainForm(bool isAdmin, string? logPath)
    {
        _isAdmin = isAdmin;
        _monitor = new SleepMonitor(5, logPath);

        Text = "WhyNotSleeping";
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Background;
        ForeColor = TextColor;
        ClientSize = new Size(1000, 720);
        MinimumSize = new Size(780, 580);
        StartPosition = FormStartPosition.CenterScreen;
        using (var icon = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico"))
            if (icon != null) Icon = new Icon(icon);

        // Limited-mode banner
        var bannerText = new Label
        {
            Text = "Limited mode: without administrator rights, Windows won't say which apps and drivers are keeping the PC awake.",
            AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.BannerText, Margin = new Padding(0, 6, 12, 6),
        };
        var elevate = new Button { Text = "Restart as administrator", AutoSize = true, Anchor = AnchorStyles.Right, FlatStyle = FlatStyle.System };
        elevate.Click += (_, _) => RestartElevated();
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
        var settings = new Button { Text = "Power && sleep settings", AutoSize = true, FlatStyle = FlatStyle.System };
        settings.Click += (_, _) => Open("ms-settings:powersleep");
        var refresh = new Button { Text = "Refresh now", AutoSize = true, FlatStyle = FlatStyle.System };
        refresh.Click += (_, _) => _wake.TrySetResult();
        var headerButtons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, WrapContents = false, Margin = Padding.Empty };
        headerButtons.Controls.AddRange([settings, refresh]);
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
        var inputMenu = new ContextMenuStrip();
        inputMenu.Items.AddRange([turnOff, turnOn]);
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
            "Every device that sent input since this window opened. Only input that resets the idle timer delays sleep and screen-off.", inputSplit);
        tabs.TabPages.Add(inputsPage);
        Fill(_controls, [], "Select a controller above to see its axes and buttons.");
        // Polled rather than driven by SelectedIndexChanged, which also fires while a refresh rebuilds the list.
        _live.Tick += (_, _) => { if (tabs.SelectedTab == inputsPage) ShowControls(); };
        _live.Start();
        tabs.TabPages.Add(Page("Event log",
            logPath is null ? "What changed, newest first." : $"What changed, newest first. Also saved to {logPath}", _events, openLog, openFolder));
        tabs.TabPages.Add(Page("Ignored apps",
            "Apps whose requests Windows disregards (powercfg /requestsoverride).", _overrides, _unignore));

        _status = new Label { AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 6, 0, 0), Text = "Starting..." };

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14, 12, 14, 8) };
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

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _monitor.Start(_isAdmin);
        while (!IsDisposed)
        {
            try
            {
                var report = await Task.Run(_monitor.Tick);
                if (IsDisposed) return;
                Display(report);
            }
            catch (Exception ex)
            {
                _status.Text = $"Refresh failed: {ex.Message}";
            }
            _wake = new TaskCompletionSource();
            await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(_monitor.IntervalSeconds)), _wake.Task);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _live.Stop();
        _monitor.Stop();
        base.OnFormClosed(e);
    }

    void Display(Report r)
    {
        var s = r.Snapshot;
        var p = s.Power;

        _sleepCard.SetFindings(r.Sleep, worst => worst switch
        {
            Severity.Problem => "Your PC is being kept awake",
            Severity.Warning or Severity.Unknown => "Something may be keeping your PC awake",
            _ => "Nothing is blocking sleep",
        });
        _screenCard.SetFindings(r.Screen, worst => r.Display == DisplayState.Off ? "The screen is off" : worst switch
        {
            Severity.Problem => "Something is keeping the screen on",
            Severity.Warning or Severity.Unknown => "Something may be keeping the screen on",
            _ => "Nothing is keeping the screen on",
        });

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

        _banner.Visible = s.Requests == null;
        _status.Text = $"Updated {s.Time:HH:mm:ss}, refreshes every {_monitor.IntervalSeconds} s. " +
                       $"Idle time: {r.LastInputDescription}. Closing this window stops the monitoring.";
    }

    void ShowRequests(Report r)
    {
        var empty = r.Snapshot.Requests == null
            ? "Restart as administrator to see which apps and drivers are keeping the PC awake."
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
            InputMonitor.Label(i.Kind), i.ShortName, Format.Duration(s.Time - i.LastInput) + " ago",
            i.IdleResets.ToString("N0"), i.ResetsLastMinute.ToString("N0"), i.WhatItSends ?? ""])
        {
            Tag = i,
            ToolTipText = (i.Suspicious ? "This device keeps resetting the idle timer. If nobody is using it, right-click it to turn it off.\n" : "") +
                          i.Name + (i.WhatItSends is { } what ? "\n" + what : ""),
            ForeColor = i.Suspicious ? Red : i.IdleResets == 0 ? Muted : ForeColor,
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
        AfterDeviceSwitch(error);
    }

    async void TurnOnSelected()
    {
        if (Selected(_inputs) is not TurnedOffDevice device || !_isAdmin) return;
        AfterDeviceSwitch(await Task.Run(() => DeviceSwitch.TurnOn(device.InstanceId)));
    }

    void AfterDeviceSwitch(string? error)
    {
        if (error != null)
            MessageBox.Show(this, error, "WhyNotSleeping", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _wake.TrySetResult();
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

    /// <summary>Appends only events not shown yet (newest first), so the list keeps its scroll position.</summary>
    void ShowEvents(Report r)
    {
        var fresh = (int)Math.Min(r.TotalEvents - _eventsShown, r.Events.Count);
        _eventsShown = r.TotalEvents;
        if (fresh <= 0) return;
        _events.BeginUpdate();
        foreach (var line in r.Events.TakeLast(fresh))
        {
            var (time, text) = line.Length > 20 ? (line[..19], line[20..]) : ("", line);
            _events.Items.Insert(0, new ListViewItem([time, text.Trim()])
            {
                ToolTipText = text,
                ForeColor = text.StartsWith("+ new") ? Amber : text.StartsWith("- released") ? Green
                    : text.StartsWith("* input") ? Purple : Blue,
            });
        }
        while (_events.Items.Count > 1000) _events.Items.RemoveAt(_events.Items.Count - 1);
        _events.EndUpdate();
    }

    void ShowOverrides(Snapshot s)
    {
        Fill(_overrides, s.Overrides.Select(o => new ListViewItem([FriendlyKind(o.CallerType), o.Name, string.Join(", ", o.Requests.Select(FriendlyCategory))]) { Tag = o }),
            "No apps are being ignored.");
        _unignore.Enabled = _isAdmin && Selected(_overrides) is RequestOverride;
    }

    void IgnoreSelected()
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
        var existing = PowerCfg.GetOverrides().FirstOrDefault(o => o.CallerType == "PROCESS" && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var requests = (existing?.Requests ?? Array.Empty<string>()).Append(request.Category).Distinct().ToList();
        AfterOverride(PowerCfg.SetOverride("PROCESS", name, requests));
    }

    void UnignoreSelected()
    {
        if (Selected(_overrides) is not RequestOverride o) return;
        AfterOverride(PowerCfg.SetOverride(o.CallerType, o.Name, []));
    }

    void AfterOverride(string? error)
    {
        if (error != null)
            MessageBox.Show(this, error, "powercfg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _wake.TrySetResult();
    }

    void LocateSelected()
    {
        if (Selected(_requests) is PowerRequest request && File.Exists(request.Name))
            Process.Start("explorer.exe", $"/select,\"{request.Name}\"");
    }

    void RestartElevated()
    {
        if (Elevation.TryRelaunch("--no-elevate"))
            Close();
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
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = description, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 0, 0, 6) }, 0, 0);
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

        public void SetFindings(List<Finding> findings, Func<Severity, string> headline)
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
            _headline.Text = headline(worst);
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
