using WallhackTerminal.Hid;

namespace WallhackTerminal.Protocol;

public sealed class MouseTimeoutException(string message = "Mouse did not answer — it appears to be off, asleep, or out of range")
    : Exception(message);

public sealed class MouseRejectedException(byte status) : Exception(M001.DescribeStatus(status))
{
    public byte Status { get; } = status;
}

public sealed class MouseClient : IDisposable
{
    static readonly int[] RetryTimeoutsMs = [1000, 1500, 2000];
    const int Attempts = 3;
    const int PostCommitCooldownMs = 500;

    sealed class Waiter(Func<byte[], bool> match)
    {
        public readonly Func<byte[], bool> Match = match;
        public readonly TaskCompletionSource<byte[]> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    readonly HidConnection _command;
    readonly List<HidConnection> _connections;
    readonly List<Thread> _readers = [];
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly object _lock = new();
    readonly List<Waiter> _waiters = [];
    DateTime _quietUntil = DateTime.MinValue;
    bool _fastBeginEchoes = true;
    bool _preCommitEcho = true;
    volatile bool _responsive = true;
    int _disconnected;

    public HidInterfaceInfo CommandInterface => _command.Info;
    public IReadOnlyList<HidInterfaceInfo> OpenInterfaces => _connections.Select(c => c.Info).ToList();

    public event Action<byte, byte[]>? ReportReceived;
    public event Action<byte[]>? ReportSent;
    public event Action<int?, int?>? RanksReported;
    public event Action<double>? MotionSpeedReported;
    public event Action<Exception?>? Disconnected;

    MouseClient(HidConnection command, List<HidConnection> connections)
    {
        _command = command;
        _connections = connections;
        foreach (var connection in connections)
        {
            var thread = new Thread(() => ReadLoop(connection)) { IsBackground = true, Name = "M001 HID reader" };
            _readers.Add(thread);
            thread.Start();
        }
    }

    public static MouseClient? TryOpen(out string status)
    {
        var interfaces = HidEnumerator.Enumerate(M001.VendorId, M001.ProductId);
        if (interfaces.Count == 0)
        {
            status = "NO RECEIVER DETECTED";
            return null;
        }

        var commandInfo = interfaces.FirstOrDefault(i =>
            i.UsagePage == M001.CommandUsagePage && i.Usage == M001.CommandUsage && i.OutputReportLength > 0);
        if (commandInfo is null)
        {
            status = "RECEIVER FOUND, BUT ITS CONFIG INTERFACE IS MISSING";
            return null;
        }

        var command = HidConnection.TryOpen(commandInfo, write: true);
        if (command is null)
        {
            status = "COULD NOT OPEN THE RECEIVER";
            return null;
        }

        var connections = new List<HidConnection> { command };
        foreach (var info in interfaces)
        {
            if (info.Path == commandInfo.Path || info.InputReportLength == 0) continue;
            if (info.UsagePage == 0x01 && info.Usage is 0x01 or 0x02 or 0x06 or 0x07) continue;
            if (HidConnection.TryOpen(info, write: false) is { } extra) connections.Add(extra);
        }

        status = "CONNECTED";
        return new MouseClient(command, connections);
    }

    void ReadLoop(HidConnection connection)
    {
        var buffer = new byte[Math.Max(connection.Info.InputReportLength, 2)];
        bool vendor = connection.Info.UsagePage >= 0xFF00 || connection.Info.Usage == M001.CommandUsage;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int read = connection.Read(buffer, _cts.Token);
                if (read < 1) continue;
                Dispatch(buffer[0], buffer.AsSpan(1, read - 1).ToArray(), vendor);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested && connection == _command) RaiseDisconnected(ex);
        }
    }

    void Dispatch(byte reportId, byte[] data, bool vendorCollection)
    {
        try { ReportReceived?.Invoke(reportId, data); } catch { }

        if (reportId == M001.ReportStatus && data.Length >= 2 && (vendorCollection || data.Length == 2))
        {
            if (M001.ParseMotionSpeed(data[0], data[1]) is double speed)
            {
                MotionSpeedReported?.Invoke(speed);
                return;
            }
            int? dpiRank = data[0] <= M001.MaxRank ? data[0] : null;
            int? pollRank = data[1] <= M001.MaxRank ? data[1] : null;
            if (dpiRank is not null || pollRank is not null) RanksReported?.Invoke(dpiRank, pollRank);
            return;
        }

        if (reportId != M001.ReportCommand) return;
        _responsive = true;

        Waiter? hit = null;
        lock (_lock)
        {
            int index = _waiters.FindIndex(w => w.Match(data));
            if (index >= 0)
            {
                hit = _waiters[index];
                _waiters.RemoveAt(index);
            }
        }
        hit?.Result.TrySetResult(data);
    }

    void RaiseDisconnected(Exception? ex)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) == 0) Disconnected?.Invoke(ex);
    }

    Waiter AddWaiter(Func<byte[], bool> match)
    {
        var waiter = new Waiter(match);
        lock (_lock) _waiters.Add(waiter);
        return waiter;
    }

    void RemoveWaiter(Waiter waiter)
    {
        lock (_lock) _waiters.Remove(waiter);
    }

    async Task SendAsync(byte[] payload)
    {
        _cts.Token.ThrowIfCancellationRequested();
        var frame = new byte[_command.Info.OutputReportLength];
        frame[0] = M001.ReportCommand;
        Array.Copy(payload, 0, frame, 1, Math.Min(payload.Length, frame.Length - 1));
        try
        {
            await Task.Run(() => _command.Write(frame)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            RaiseDisconnected(ex);
            throw;
        }
        try { ReportSent?.Invoke(payload); } catch { }
    }

    async Task<byte[]> RequestAsync(byte[] payload, Func<byte[], bool> match, int attempts = Attempts)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            var waiter = AddWaiter(match);
            try
            {
                await SendAsync(payload).ConfigureAwait(false);
            }
            catch
            {
                RemoveWaiter(waiter);
                throw;
            }
            var timeout = Task.Delay(RetryTimeoutsMs[Math.Min(attempt, RetryTimeoutsMs.Length - 1)], _cts.Token);
            if (await Task.WhenAny(waiter.Result.Task, timeout).ConfigureAwait(false) == waiter.Result.Task)
                return await waiter.Result.Task.ConfigureAwait(false);
            RemoveWaiter(waiter);
            _cts.Token.ThrowIfCancellationRequested();
        }
        _responsive = false;
        throw new MouseTimeoutException();
    }

    async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        await _gate.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            var wait = _quietUntil - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, _cts.Token).ConfigureAwait(false);
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    static Func<byte[], bool> ReplyTo(byte cmd) => r => r.Length > 2 && r[2] == cmd;

    static Func<byte[], bool> ReplyAt(byte cmd, int address) => r => r.Length > 6 && r[2] == cmd && M001.Offset(r) == address;

    public Task<byte[]> ReadAreaAsync(int address, int length) => RunAsync(async () =>
    {
        var reply = await RequestAsync(M001.ReadArea(address, length), ReplyAt(M001.Cmd.ReadArea, address)).ConfigureAwait(false);
        if (M001.Status(reply) != 0) throw new MouseRejectedException(M001.Status(reply));
        return M001.Data(reply);
    });

    public Task<FirmwareVersions?> ReadVersionsAsync(int attempts = Attempts) => RunAsync(async () =>
        M001.ParseVersions(await RequestAsync(M001.Simple(M001.Cmd.ReadVersion), ReplyTo(M001.Cmd.ReadVersion), attempts)
            .ConfigureAwait(false)));

    public Task<string?> ReadChipIdAsync(bool receiver) => RunAsync(async () =>
    {
        byte cmd = receiver ? M001.Cmd.ReadDongleChipId : M001.Cmd.ReadMouseChipId;
        return M001.ParseChipId(await RequestAsync(M001.Simple(cmd), ReplyTo(cmd)).ConfigureAwait(false));
    });

    public Task<KeyTriplet[]> ReadKeysAsync(bool defaults) => RunAsync(async () =>
    {
        byte cmd = defaults ? M001.Cmd.GetDefaultKeys : M001.Cmd.GetKeys;
        return M001.ParseKeys(await RequestAsync(M001.ReadKeys(defaults), ReplyTo(cmd)).ConfigureAwait(false));
    });

    async Task<byte[]> ReadMacroAreaCoreAsync(int address, int length) =>
        M001.Data(await RequestAsync(M001.ReadMacroArea(address, length), ReplyTo(M001.Cmd.GetMacro)).ConfigureAwait(false));

    async Task<List<MacroStep>?> ReadMacroCoreAsync(int address)
    {
        if (!Macros.IsValidAddress(address)) return null;
        var header = await ReadMacroAreaCoreAsync(address, 4).ConfigureAwait(false);
        if (header.Length < 2) return null;
        int count = Math.Min(header[0] | (header[1] << 8), Macros.MaxSteps);
        int length = 4 + count * 4;
        if (address + length > Macros.AreaEnd) return null;

        var content = new byte[length];
        header.AsSpan(0, Math.Min(4, header.Length)).CopyTo(content);
        for (int position = 4; position < length;)
        {
            int n = Math.Min(Macros.Chunk, length - position);
            var chunk = await ReadMacroAreaCoreAsync(address + position, n).ConfigureAwait(false);
            if (chunk.Length == 0) break;
            chunk.AsSpan(0, Math.Min(n, chunk.Length)).CopyTo(content.AsSpan(position));
            position += n;
        }
        return Macros.Decode(content);
    }

    public Task<List<MacroStep>?[]> ReadMacrosAsync() => RunAsync(async () =>
    {
        var index = await ReadMacroAreaCoreAsync(Macros.IndexOffset, Macros.IndexLength).ConfigureAwait(false);
        var result = new List<MacroStep>?[Macros.Slots];
        for (int slot = 0; slot < Macros.Slots; slot++)
        {
            if (index.Length < (slot + 1) * 2) continue;
            int address = index[slot * 2] | (index[slot * 2 + 1] << 8);
            if (address is 0 or 0xFFFF) continue;
            result[slot] = await ReadMacroCoreAsync(address).ConfigureAwait(false);
        }
        return result;
    });

    public Task<Curve?> ReadCurveAsync(int mode) => RunAsync(async () =>
    {
        int address = Curves.Address(mode);
        var reply = await RequestAsync(M001.ReadArea(address, Curves.Length(mode)), ReplyAt(M001.Cmd.ReadArea, address))
            .ConfigureAwait(false);
        return M001.Status(reply) == 0 ? Curves.Parse(mode, M001.Data(reply)) : null;
    });

    public Task<bool> WriteSettingAsync(SettingId id, int value) => TransactAsync([M001.EncodeSetting(id, value)]);

    public Task<bool> WriteButtonAsync(int slot, KeyTriplet binding) => TransactAsync([M001.SetKey(slot, binding)]);

    public Task<bool> WriteCustomCurveAsync(Curve curve) => TransactAsync([Curves.EncodeCustom(curve)]);

    public Task<bool> WriteMacroAsync(int slot, IReadOnlyList<MacroStep> steps) => RunAsync(async () =>
    {
        var index = await ReadMacroAreaCoreAsync(Macros.IndexOffset, Macros.IndexLength).ConfigureAwait(false);
        var allocations = new List<Macros.Allocation>();
        for (int p = 0; p < Macros.Slots; p++)
        {
            if (index.Length < (p + 1) * 2) continue;
            int address = index[p * 2] | (index[p * 2 + 1] << 8);
            if (address is 0 or 0xFFFF || !Macros.IsValidAddress(address)) continue;
            var header = await ReadMacroAreaCoreAsync(address, 4).ConfigureAwait(false);
            if (header.Length < 2) continue;
            int count = header[0] | (header[1] << 8);
            int length = 4 + count * 4;
            bool overlaps = allocations.Any(a => address < a.Address + a.Length && a.Address < address + length);
            if (count > 0 && Macros.Fits(address, length) && !overlaps) allocations.Add(new(p, address, length));
        }

        var plan = Macros.PlanWrite(slot, steps, index, allocations);
        var commands = plan.Select(w => M001.WriteMacroArea(w.Offset, w.Bytes)).ToList();
        bool confirmed = await TransactCoreAsync(commands).ConfigureAwait(false);

        var readBack = await ReadMacroAreaCoreAsync(Macros.IndexOffset, Macros.IndexLength).ConfigureAwait(false);
        var entry = plan.First(w => w.Offset == Macros.IndexOffset + slot * 2);
        int target = entry.Bytes[0] | (entry.Bytes[1] << 8);
        int stored = readBack.Length >= (slot + 1) * 2 ? readBack[slot * 2] | (readBack[slot * 2 + 1] << 8) : -1;
        if (stored != target) throw new InvalidOperationException("Macro index did not stick — try again");
        if (target != 0)
        {
            var back = await ReadMacroCoreAsync(target).ConfigureAwait(false);
            if (back is null || back.Count != steps.Count) throw new InvalidOperationException("Macro content did not verify — try again");
        }
        return confirmed;
    });

    public Task<bool> TransactAsync(IReadOnlyList<byte[]> commands) => RunAsync(() => TransactCoreAsync(commands));

    async Task<bool> TransactCoreAsync(IReadOnlyList<byte[]> commands)
    {
        if (!_responsive)
        {
            await RequestAsync(M001.Simple(M001.Cmd.ReadVersion), ReplyTo(M001.Cmd.ReadVersion), 2).ConfigureAwait(false);
        }

        if (_fastBeginEchoes)
        {
            try
            {
                await RequestAsync(M001.Simple(M001.Cmd.FastBegin), ReplyTo(M001.Cmd.FastBegin)).ConfigureAwait(false);
            }
            catch (MouseTimeoutException)
            {
                await RequestAsync(M001.Simple(M001.Cmd.ReadVersion), ReplyTo(M001.Cmd.ReadVersion), 2).ConfigureAwait(false);
                _fastBeginEchoes = false;
                _responsive = true;
                await Task.Delay(500, _cts.Token).ConfigureAwait(false);
            }
        }
        else
        {
            await SendAsync(M001.Simple(M001.Cmd.FastBegin)).ConfigureAwait(false);
            await Task.Delay(500, _cts.Token).ConfigureAwait(false);
        }

        bool confirmed = true;
        byte rejected = 0;
        var pending = new List<Waiter>();
        try
        {
            foreach (var command in commands)
            {
                var echo = ReplyAt(command[2], command[4] | (command[5] << 8));
                if (_preCommitEcho)
                {
                    try
                    {
                        var reply = await RequestAsync(command, echo).ConfigureAwait(false);
                        if (M001.Status(reply) != 0) rejected = M001.Status(reply);
                        continue;
                    }
                    catch (MouseTimeoutException)
                    {
                        _preCommitEcho = false;
                        _responsive = true;
                        pending.Add(AddWaiter(echo));
                        continue;
                    }
                }
                pending.Add(AddWaiter(echo));
                await SendAsync(command).ConfigureAwait(false);
            }
            if (pending.Count > 0) await Task.Delay(100, _cts.Token).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await RequestAsync(M001.Simple(M001.Cmd.FastEnd), ReplyTo(M001.Cmd.FastEnd)).ConfigureAwait(false);
            }
            catch (MouseTimeoutException)
            {
                confirmed = false;
                _responsive = true;
            }
            _quietUntil = DateTime.UtcNow.AddMilliseconds(PostCommitCooldownMs);
        }

        foreach (var waiter in pending)
        {
            if (await Task.WhenAny(waiter.Result.Task, Task.Delay(2000, _cts.Token)).ConfigureAwait(false) == waiter.Result.Task)
            {
                var reply = await waiter.Result.Task.ConfigureAwait(false);
                if (M001.Status(reply) != 0) rejected = M001.Status(reply);
            }
            else
            {
                RemoveWaiter(waiter);
                confirmed = false;
            }
        }

        if (rejected != 0) throw new MouseRejectedException(rejected);
        return confirmed;
    }

    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        foreach (var thread in _readers) thread.Join(500);
        foreach (var connection in _connections) connection.Dispose();
        lock (_lock)
        {
            foreach (var waiter in _waiters) waiter.Result.TrySetCanceled();
            _waiters.Clear();
        }
    }
}
