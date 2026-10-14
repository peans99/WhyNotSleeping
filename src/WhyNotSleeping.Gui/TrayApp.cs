using Microsoft.Win32;

namespace WhyNotSleeping.Gui;

/// <summary>
/// The app while it runs: the monitor refreshing every few seconds, the tray icon, the away group, and the window,
/// which only hides when closed so the monitoring goes on. Exit is in the tray menu.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    readonly bool _isAdmin;
    readonly SleepMonitor _monitor;
    readonly Settings _settings = Settings.Load();
    readonly AwayGuard _away;
    readonly MainForm _form;
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _comeBackItem, _startupItem;
    readonly System.Windows.Forms.Timer _awayTimer = new() { Interval = 1000 };
    TaskCompletionSource _wake = new();
    bool _exiting, _formClosed;

    public TrayApp(bool isAdmin, string? logPath, bool startHidden)
    {
        _isAdmin = isAdmin;
        _monitor = new SleepMonitor(5, logPath);
        _away = new AwayGuard(_settings, isAdmin, _monitor.Log);
        _form = new MainForm(this, isAdmin, logPath, _settings, _away);
        _ = _form.Handle; // so it can be updated, and found by a second copy, while hidden
        _form.FormClosed += (_, e) => // Windows shutting down or signing out, or Exit
        {
            _formClosed = true;
            // Windows may end the process as soon as this returns, before an awaited restore gets to run, and a
            // device turned off "until restart" stays off through a sign-out or Fast Startup. So restore now.
            if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
                _away.StopNow("Windows is signing out or shutting down");
            Exit();
        };

        StartsWithWindows = Startup.IsEnabled(out var stale);
        if (stale && isAdmin) Startup.Enable(); // the app was moved: start the new copy

        var open = new ToolStripMenuItem("Open WhyNotSleeping", null, (_, _) => ShowWindow()) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
        _comeBackItem = new ToolStripMenuItem("Turn the away group back on", null, (_, _) => _ = _away.ComeBackAsync("turned back on from the tray"));
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => SetStartWithWindows(!StartsWithWindows));
        var pause = new ToolStripMenuItem("Pause the away group");
        pause.DropDownItems.AddRange([
            new ToolStripMenuItem("For 30 minutes", null, async (_, _) => await PauseAwayAsync(TimeSpan.FromMinutes(30))),
            new ToolStripMenuItem("For one hour", null, async (_, _) => await PauseAwayAsync(TimeSpan.FromHours(1))),
            new ToolStripMenuItem("Until WhyNotSleeping restarts", null, async (_, _) => await PauseAwayAsync(null)),
        ]);
        var resume = new ToolStripMenuItem("Resume the away group", null, (_, _) => { _away.Resume(); UpdateAwayView(); });
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([open, _comeBackItem, pause, resume, new ToolStripSeparator(), _startupItem, new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => Exit())]);
        menu.Opening += (_, _) =>
        {
            _comeBackItem.Visible = _away.IsAway;
            pause.Enabled = _settings.AwayEnabled && _settings.AwayDevices.Count > 0;
            resume.Visible = _away.PauseReason != null;
            _startupItem.Checked = StartsWithWindows;
            _startupItem.Enabled = isAdmin;
            _startupItem.Text = "Start with Windows" + (isAdmin ? "" : " (needs administrator)");
        };
        _tray = new NotifyIcon { Icon = _form.Icon, Text = "WhyNotSleeping", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

        _away.WentAway += OnWentAway;
        _away.Failed += message => _tray.ShowBalloonTip(10000, "Away group", message, ToolTipIcon.Warning);
        _awayTimer.Tick += async (_, _) =>
        {
            try
            {
                await _away.TickAsync();
                if (!_exiting)
                {
                    _monitor.Away = _away.VerdictGroup;
                    if (_form.Visible) _form.ShowAway();
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"* away: {ex.Message}");
            }
        };
        // Unlocking means someone is back, even if the lock screen kept their typing from us.
        SystemEvents.SessionSwitch += OnSessionSwitch;

        if (!startHidden) _form.Show();
        _ = RunAsync();
    }

    public int IntervalSeconds => _monitor.IntervalSeconds;
    public bool StartsWithWindows { get; private set; }

    async Task PauseAwayAsync(TimeSpan? duration)
    {
        try { await _away.PauseAsync(duration); }
        catch (Exception ex) { _monitor.Log($"* away: couldn't pause: {ex.Message}"); }
        if (!_exiting) UpdateAwayView();
    }

    void UpdateAwayView()
    {
        _monitor.Away = _away.VerdictGroup;
        if (_form.Visible) _form.ShowAway();
        RefreshNow();
    }

    /// <summary>Refreshes now instead of at the next tick.</summary>
    public void RefreshNow() => _wake.TrySetResult();

    public void Log(string message) => _monitor.Log(message);

    async Task RunAsync()
    {
        _monitor.Start(_isAdmin);
        await _away.RestoreLeftoversAsync();
        if (_exiting) return;
        _awayTimer.Start();
        while (!_exiting)
        {
            _wake = new TaskCompletionSource();
            try
            {
                var report = await Task.Run(_monitor.Tick);
                if (_exiting) return;
                if (_form.Visible) _form.Display(report);
                _tray.Text = TrayText(report);
            }
            catch (Exception ex)
            {
                if (!_exiting) _form.ShowError(ex.Message);
            }
            await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(_monitor.IntervalSeconds)), _wake.Task);
        }
    }

    /// <summary>The tray tooltip: both verdicts, or that the away group is off. Windows cuts it at 127 characters.</summary>
    string TrayText(Report r)
    {
        var text = _away.IsAway || _away.PauseReason != null || _away.VerdictGroup?.Failures.Count > 0
            ? $"WhyNotSleeping\n{_away.Status}"
            : $"WhyNotSleeping\n{Gui.MainForm.SleepHeadline(r)}\n{Gui.MainForm.ScreenHeadline(r)}"; // not ApplicationContext.MainForm
        return text.Length <= 127 ? text : text[..127];
    }

    void OnWentAway(AwayAction action)
    {
        switch (action)
        {
            case AwayAction.ScreenOff:
                // Like the monitor's own timeout; it stays off now that the noisy devices can't wake it.
                Native.PostMessage(_form.Handle, Native.WmSysCommand, Native.ScMonitorPower, 2);
                break;
            case AwayAction.Sleep:
                // SetSuspendState returns only after the PC wakes up, so not on the UI thread.
                Task.Run(() =>
                {
                    if (!Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, false, false))
                        _monitor.Log("* away: Windows didn't go to sleep when asked");
                });
                break;
        }
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock && !_exiting)
            _form.BeginInvoke(() => { if (_away.IsAway) _ = _away.ComeBackAsync("the PC was unlocked"); });
    }

    public void ShowWindow()
    {
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    /// <summary>The window was closed: it hides, and says so once.</summary>
    public void Hidden()
    {
        if (_settings.TrayTipShown) return;
        _settings.TrayTipShown = true;
        _settings.Save();
        _tray.ShowBalloonTip(8000, "WhyNotSleeping is still running",
            "It keeps watching from the notification area. Right-click its icon to exit.", ToolTipIcon.Info);
    }

    public void SetStartWithWindows(bool on)
    {
        if (!_isAdmin) return;
        if ((on ? Startup.Enable() : Startup.Disable()) is { } error)
            MessageBox.Show(_form.Visible ? _form : null, error, "WhyNotSleeping", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        StartsWithWindows = Startup.IsEnabled(out _);
        _form.ShowStartup();
    }

    /// <summary>Restarts elevated; the new copy takes over, this one exits.</summary>
    public void RestartElevated()
    {
        SingleInstance.Release();
        if (Elevation.TryRelaunch("--no-elevate"))
            Exit();
        else
            SingleInstance.Claim();
    }

    public bool Exiting => _exiting;

    public async void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _awayTimer.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        await _away.StopAsync();
        _monitor.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        if (!_formClosed) _form.Close();
        ExitThread();
    }
}
