using System.Runtime.InteropServices;
using System.Windows.Threading;
using WallhackTerminal.Protocol;

namespace WallhackTerminal.Services;

public sealed class DeviceService : IDisposable
{
    readonly Dispatcher _ui;
    readonly Timer _scanTimer;
    volatile MouseClient? _client;
    volatile bool _paused;
    volatile bool _attaching;
    int _scanning;
    bool _hydrating;
    DateTime _lastProbe = DateTime.MinValue;
    readonly Dictionary<SettingId, int> _confirmed = new();
    readonly Dictionary<SettingId, CancellationTokenSource> _pendingWrites = new();

    public MouseState State { get; } = new();
    public bool Paused => _paused;
    public bool WindowVisible { get; set; }

    public bool TrafficEnabled { get; set; }

    public event Action? StateChanged;
    public event Action<HardwareChange>? HardwareChanged;
    public event Action<bool>? ConnectionChanged;
    public event Action<string, bool>? Traffic;
    public event Action<double>? MotionSpeed;
    public event Action<string, bool>? Status;

    public DeviceService(Dispatcher ui)
    {
        _ui = ui;
        _scanTimer = new Timer(_ => Scan(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _scanTimer.Change(0, 2000);

    void Post(Action action) => _ui.BeginInvoke(action);

    void Changed() => StateChanged?.Invoke();

    void Report(string message, bool error = false)
    {
        if (error) Log.Info("device: " + message);
        Status?.Invoke(message, error);
    }

    void Scan()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            var client = _client;
            if (client is null)
            {
                if (_paused || _attaching) return;
                var opened = MouseClient.TryOpen(out var status);
                if (opened is null)
                {
                    Post(() =>
                    {
                        if (_client is not null || State.ReceiverStatus == status) return;
                        State.ReceiverStatus = status;
                        Changed();
                    });
                    return;
                }
                _attaching = true;
                Post(() => Attach(opened));
            }
            else
            {
                Post(MaybeProbe);
            }
        }
        catch (Exception ex)
        {
            Log.Error("scan failed", ex);
        }
        finally
        {
            Volatile.Write(ref _scanning, 0);
        }
    }

    void Attach(MouseClient client)
    {
        _attaching = false;
        if (_client is not null || _paused)
        {
            client.Dispose();
            return;
        }
        _client = client;
        client.RanksReported += (dpi, poll) => Post(() => OnRanks(client, dpi, poll));
        client.MotionSpeedReported += speed => Post(() => MotionSpeed?.Invoke(speed));
        client.Disconnected += ex => Post(() => Detach(client, ex));
        client.ReportReceived += (id, data) =>
        {
            if (TrafficEnabled) Post(() => Traffic?.Invoke($"R{id} {Convert.ToHexString(Trim(data))}", false));
        };
        client.ReportSent += payload =>
        {
            if (TrafficEnabled) Post(() => Traffic?.Invoke($"W4 {Convert.ToHexString(Trim(payload))}", true));
        };

        State.Reset();
        State.ReceiverConnected = true;
        State.ReceiverStatus = "CONNECTED";
        Log.Info("attached: " + string.Join(" | ", client.OpenInterfaces.Select(i =>
            $"{i.UsagePage:X4}/{i.Usage:X2} in{i.InputReportLength} out{i.OutputReportLength}")));
        Changed();
        ConnectionChanged?.Invoke(true);
        _ = HydrateAsync(client);
    }

    static byte[] Trim(byte[] data)
    {
        int n = data.Length;
        while (n > 8 && data[n - 1] == 0) n--;
        return data.AsSpan(0, n).ToArray();
    }

    void Detach(MouseClient client, Exception? ex)
    {
        if (_client != client) return;
        _client = null;
        client.Dispose();
        foreach (var pending in _pendingWrites.Values) pending.Cancel();
        _pendingWrites.Clear();
        _confirmed.Clear();
        State.Reset();
        State.ReceiverStatus = _paused ? "DISCONNECTED" : "RECEIVER REMOVED";
        if (ex is not null) Log.Info("detached: " + ex.Message);
        Changed();
        ConnectionChanged?.Invoke(false);
    }

    public void Disconnect()
    {
        _paused = true;
        if (_client is { } client) Detach(client, null);
        State.ReceiverStatus = "DISCONNECTED";
        Changed();
    }

    public void Reconnect()
    {
        _paused = false;
        State.ReceiverStatus = "SCANNING";
        Changed();
        ThreadPool.QueueUserWorkItem(_ => Scan());
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO
    {
        public int cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime)) : TimeSpan.Zero;
    }

    void MaybeProbe()
    {
        if (_client is not { } client || State.Hydrated || _hydrating) return;
        var sinceProbe = DateTime.UtcNow - _lastProbe;
        bool userActive = IdleTime() < TimeSpan.FromSeconds(3);
        if ((userActive && sinceProbe > TimeSpan.FromSeconds(6)) || (WindowVisible && sinceProbe > TimeSpan.FromSeconds(20)))
            _ = HydrateAsync(client);
    }

    public Task RefreshAsync() => _client is { } client ? HydrateAsync(client, force: true) : Task.CompletedTask;

    async Task HydrateAsync(MouseClient client, bool force = false)
    {
        if (_hydrating) return;
        _hydrating = true;
        _lastProbe = DateTime.UtcNow;
        bool firstTry = State.MouseResponding is null;
        try
        {
            if (firstTry) await Task.Delay(300);
            try
            {
                State.Versions = await client.ReadVersionsAsync(firstTry || force ? 3 : 1);
                State.MouseResponding = true;
            }
            catch (MouseTimeoutException)
            {
                if (_client != client) return;
                State.MouseResponding = false;
                Changed();
                return;
            }
            State.Busy = true;
            Changed();

            await Step(client, async () =>
            {
                var d = await client.ReadAreaAsync(M001.Addr.ReportEsb, 1);
                if (d.Length > 0 && d[0] <= M001.MaxRank) State.PollRank = d[0];
            });
            await Step(client, async () =>
            {
                var d = await client.ReadAreaAsync(M001.Addr.DpiRank, 1);
                if (d.Length > 0 && d[0] <= M001.MaxRank) State.DpiRank = d[0];
            });
            await Step(client, async () =>
            {
                var d = await client.ReadAreaAsync(M001.Addr.Dpi8Block, 9);
                if (M001.DecodeSetting(SettingId.Dpi, d) is int dpi) Confirm(SettingId.Dpi, dpi);
            });
            await Step(client, async () =>
            {
                var values = M001.ParseCombined(await client.ReadAreaAsync(M001.Addr.ReportUser, M001.Addr.CombinedLength));
                foreach (var (id, value) in values) Confirm(id, value);
                if (values.TryGetValue(SettingId.Ripple, out int ripple) && ripple != 0) SetSetting(SettingId.Ripple, 0);
            });
            await Step(client, async () => State.MouseChipId = await client.ReadChipIdAsync(receiver: false));
            await Step(client, async () => State.ReceiverChipId = await client.ReadChipIdAsync(receiver: true));
            State.Hydrated = true;
            Changed();

            await Step(client, async () => State.Buttons = await client.ReadKeysAsync(defaults: false));
            await Step(client, async () => State.DefaultButtons = await client.ReadKeysAsync(defaults: true));
            await Step(client, ReadCurvesAsync);
            await Step(client, async () => State.Macros = await client.ReadMacrosAsync());
        }
        finally
        {
            _hydrating = false;
            if (_client == client)
            {
                State.Busy = false;
                Changed();
            }
        }

        async Task ReadCurvesAsync()
        {
            for (int mode = 0; mode < 4; mode++)
            {
                try
                {
                    State.Curves[mode] = await client.ReadCurveAsync(mode);
                }
                catch (MouseRejectedException)
                {
                    State.CurvesUnsupported = true;
                    return;
                }
                await Task.Delay(50);
            }
        }
    }

    async Task Step(MouseClient client, Func<Task> read)
    {
        if (_client != client) return;
        try
        {
            await read();
            Changed();
        }
        catch (Exception ex) when (_client == client)
        {
            Log.Error("read failed", ex);
        }
        catch
        {
        }
        await Task.Delay(50);
    }

    void Confirm(SettingId id, int value)
    {
        _confirmed[id] = value;
        if (!_pendingWrites.ContainsKey(id)) State.Set(id, value);
    }

    void OnRanks(MouseClient client, int? dpiRank, int? pollRank)
    {
        if (_client != client) return;
        bool dpiChanged = dpiRank is int d && State.DpiRank != d;
        bool pollChanged = pollRank is int p && State.PollRank != p;
        if (dpiRank is int dr) State.DpiRank = dr;
        if (pollRank is int pr) State.PollRank = pr;
        if (!dpiChanged && !pollChanged) return;
        Changed();
        HardwareChanged?.Invoke(new HardwareChange(dpiChanged ? State.DpiRank : null, pollChanged ? State.PollRank : null));
    }

    static string Describe(SettingId id) => id switch
    {
        SettingId.Dpi => "DPI",
        SettingId.PollRate => "POLLING RATE",
        SettingId.MotionSync => "MOTION SYNC",
        SettingId.GameMode => "SCANNING MODE",
        SettingId.SleepEnabled => "AUTO SLEEP",
        SettingId.SleepMinutes => "SLEEP TIMER",
        SettingId.Lod => "LIFT-OFF DISTANCE",
        SettingId.SensorAngle => "SENSOR ROTATION",
        SettingId.Ripple => "RIPPLE CONTROL",
        SettingId.DynEnabled => "DPI ACCELERATION",
        SettingId.DynMode => "ACCELERATION CURVE",
        SettingId.DynSpeedReporting => "SPEED READOUT",
        _ => id.ToString().ToUpperInvariant(),
    };

    public void SetSetting(SettingId id, int value)
    {
        if (_client is null) return;
        State.Set(id, value);
        Changed();
        if (_pendingWrites.Remove(id, out var previous)) previous.Cancel();
        var cancel = new CancellationTokenSource();
        _pendingWrites[id] = cancel;
        _ = WriteSettingAsync(id, value, cancel);
    }

    async Task WriteSettingAsync(SettingId id, int value, CancellationTokenSource cancel)
    {
        try
        {
            bool numeric = id is SettingId.Dpi or SettingId.PollRate or SettingId.SensorAngle or SettingId.SleepMinutes;
            await Task.Delay(numeric ? 350 : 60, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_client is not { } client) return;

        Report($"WRITING {Describe(id)}…");
        try
        {
            bool confirmed = await client.WriteSettingAsync(id, value);
            if (!confirmed)
            {
                var (address, length) = M001.SettingLocation(id);
                var actual = M001.DecodeSetting(id, await client.ReadAreaAsync(address, length));
                if (actual != value) throw new InvalidOperationException("the mouse did not keep the new value");
            }
            _confirmed[id] = value;
            if (!cancel.IsCancellationRequested) Report($"{Describe(id)} SAVED");
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            if (cancel.IsCancellationRequested || _client != client) return;
            if (_confirmed.TryGetValue(id, out int old)) State.Set(id, old);
            Report($"{Describe(id)} FAILED — {ex.Message}", error: true);
            MarkAsleepOn(ex);
            Changed();
        }
        finally
        {
            if (_pendingWrites.TryGetValue(id, out var current) && current == cancel) _pendingWrites.Remove(id);
        }
    }

    async Task<bool> RunWriteAsync(string what, Func<MouseClient, Task> write)
    {
        if (_client is not { } client) return false;
        Report($"WRITING {what}…");
        try
        {
            await write(client);
            Report($"{what} SAVED");
            Changed();
            return true;
        }
        catch (Exception ex)
        {
            if (_client != client) return false;
            Report($"{what} FAILED — {ex.Message}", error: true);
            MarkAsleepOn(ex);
            Changed();
            return false;
        }
    }

    void MarkAsleepOn(Exception ex)
    {
        if (ex is not MouseTimeoutException) return;
        State.MouseResponding = false;
        State.Hydrated = false;
    }

    public Task<bool> WriteButtonAsync(int slot, KeyTriplet binding) => RunWriteAsync(Bindings.SlotNames[slot].ToUpperInvariant(), async c =>
    {
        await c.WriteButtonAsync(slot, binding);
        var buttons = State.Buttons?.ToArray() ?? new KeyTriplet[M001.KeySlots];
        if (slot < buttons.Length) buttons[slot] = binding;
        State.Buttons = buttons;
    });

    public Task<bool> WriteMacroAsync(int slot, IReadOnlyList<MacroStep> steps) => RunWriteAsync($"MACRO {slot + 1}", async c =>
    {
        await c.WriteMacroAsync(slot, steps);
        var macros = State.Macros?.ToArray() ?? new List<MacroStep>?[Macros.Slots];
        macros[slot] = steps.Count > 0 ? steps.ToList() : null;
        State.Macros = macros;
    });

    public Task<bool> WriteCustomCurveAsync(Curve curve) => RunWriteAsync("CUSTOM CURVE", async c =>
    {
        await c.WriteCustomCurveAsync(curve);
        State.Curves[Curves.CustomMode] = curve;
    });

    public void Dispose()
    {
        _scanTimer.Dispose();
        var client = _client;
        _client = null;
        client?.Dispose();
    }
}
