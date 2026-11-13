namespace QiChargerStationTool.Core.Transport;

/// <summary>
/// ZQWL-CANFD USB CDC serial protocol (49 3B config / 5A…A5 data).
/// VID:PID 3562:0101 → COM port. See AGENTS.md「WSL2 串口 CAN 监听」
/// and python_tools/wsl/zqwl_can_listen.py (open_can0_250k 同构).
/// </summary>
public sealed class ZqwlSerialTransport : ICanTransport
{
    private const byte CanHdr = 0x5A;
    private const byte CanFtr = 0xA5;

    private readonly string _portName;
    private readonly System.IO.Ports.SerialPort? _port;
    private readonly object _rxLock = new();
    private readonly object _writeLock = new();
    private readonly Queue<CanFrame> _rxQueue = new();
    private readonly byte[] _buf = new byte[4096];
    private int _bufLen;
    private bool _disposed;
    private volatile bool _writeDead;
    private bool _healing;   // 自愈重连中：防 ApplyBoxConfig 失败递归再入

    // ── 队列积压根治（2026-11-12）：无 ACK 帧灌满盒子 TX 队列 →
    //    SerialPort.Write 挂起 = 历次「杀口/断开」的根因 ──
    private long _lastCanRxTickMs;   // 最近收到 CAN 数据帧时刻（总线活性：设备在发帧=在 ACK）
    private long _lastSendTickMs;    // 最近发送时刻（静默限速用）
    private long _congestedUntilMs;  // 拥塞退避截止：写挂起/慢写后一段时间内限速发送
    private int _abortStreak;        // 连续 purge 中止计数（达到上限才升级杀口）

    private const int BusSilentAfterMs = 800;     // 总线静默判定：无 CAN 回流超 0.8s
    private const int SilentFrameGapMs = 250;     // 静默/拥塞时最小帧间隔（同唤醒 burst 节奏）
    private const int WriteStallAbortMs = 2000;   // 写阻塞达此值进入静默排水阶段
    private const int WriteDrainMs = 8000;        // 静默排水：停止灌入、等盒子队列自然排空
    private const int WriteAbortStreakLimit = 3;  // 连续 purge 中止次数达此值才升级杀口+自愈
    private const int WriteBackpressureMs = 300;  // 写耗时超此值视背压，冷却防队列打满
    private const int CongestedPaceMs = 3000;     // 写挂起/慢写后的拥塞退避时长

    /// <summary>
    /// 调试日志：只报 CAN 数据帧。盒子心跳(5A FF/FE)、配置应答(49 3B) 一律不上报。
    /// </summary>
    public event Action<string>? OnRawLog;

    public ZqwlSerialTransport(string portName, int baud = 2_000_000)
    {
        _portName = portName;
        _port = new System.IO.Ports.SerialPort(portName, baud)
        {
            ReadTimeout = 50,
            WriteTimeout = 500,
            ReadBufferSize = 8192,
            WriteBufferSize = 8192,
        };
        _port.DataReceived += OnData;
    }

    /// <summary>
    /// 最近一次收到睡眠入口标记帧（SLEEP 生命周期 0x04 / E0~E3 睡前快照）的
    /// Tick64；0=未见。供上层在设备入睡临界窗（SLEEP→E0~E3→commit 约 4s）
    /// 内避让不写总线——临界窗内帧打在入睡途中无效，且挤占 WUF 配置期。
    /// </summary>
    public long LastSleepMarkerTickMs { get; private set; }

    public bool IsOpen => _port?.IsOpen == true;

    private void WriteRaw(byte[] pkt, string tag)
    {
        WriteBytes(pkt);
        // 配置帧只在打开阶段打一行摘要，不进 CAN 日志
        if (tag.Length > 0) OnRawLog?.Invoke($"[CFG] {tag}");
    }

    /// <summary>
    /// 带看门狗的串口写入。USB-CDC 驱动挂起时 SerialPort.Write 可无限
    /// 阻塞（WriteTimeout 对 USB 串口并不总是生效），会把 UI/操作线程
    /// 永久卡死（点击「连接」卡住、必须重启软件的根因）。后台写 + 5s
    /// 看门狗：超时抛错让上层提示拔插盒子，调用线程不再被拖死。
    /// 超时后必须杀口（2026-11-10 station_log_184741 教训）：被遗弃的
    /// 僵尸写任务仍阻塞在 SerialPort.Write 里并占着内部写锁，此后每次
    /// 写都会挂满看门狗再超时（日志实证首写卡死后全部写超时、永不自愈）——
    /// 置 _writeDead + Close 杀掉僵尸写，后续快速失败；写路径
    /// 串行化（_writeLock）杜绝并发 Write（SerialPort 并发写即卡死）。
    /// 写异常在 lambda 内就地消化（2026-11-10 第二次升级现场教训）：
    /// 让它以任务异常形态抛出会弹「The port is closed」调试中断/未观察
    /// 任务异常——错误统一在同步路径转成 InvalidOperationException。
    /// 2026-11-12 自愈：杀口后尝试重开同一 COM 并重配盒子（设备仍枚举时
    /// 无需拔插）——设备仍在总线（生命周期照播）而写通道挂起的场景
    /// （IAP 升级后门禁写超时实证）不再把连接变「断开」；本帧不重写
    /// （防半包+重发拼接污染盒子解析流），调用方按写失败重试即可。
    /// </summary>
    /// <summary>
    /// 静默/拥塞限速（队列积压根治）：① 总线静默（无 CAN 回流 = 设备离线/休眠，
    /// 无 ACK）时强制 ≥250ms 帧间隔——快速连发的无 ACK 帧会灌满盒子 TX 队列；
    /// ② 拥塞退避（最近写挂起/慢写后 3s 内）同样限速——停止灌入让积压排空，
    /// 无论总线活性。唤醒 burst 本就 250ms 节奏不受影响；正常传输不限速。
    /// </summary>
    private void PaceIfNeeded()
    {
        long now = Environment.TickCount64;
        bool silent = now - Volatile.Read(ref _lastCanRxTickMs) > BusSilentAfterMs;
        bool congested = now < Volatile.Read(ref _congestedUntilMs);
        if (!silent && !congested)
            return;
        lock (_writeLock)
        {
            long wait = Volatile.Read(ref _lastSendTickMs) + SilentFrameGapMs - Environment.TickCount64;
            if (wait > 0)
                Thread.Sleep((int)(wait > SilentFrameGapMs ? SilentFrameGapMs : wait));
            Volatile.Write(ref _lastSendTickMs, Environment.TickCount64);
        }
    }

    private void WriteBytes(byte[] buf)
    {
        if (_writeDead || _port is not { IsOpen: true })
            throw new InvalidOperationException(
                $"ZQWL 盒子已失联（{_portName}）：请拔插 CAN 盒子后重新连接");
        lock (_writeLock)
        {
            if (_writeDead || _port is not { IsOpen: true })
                throw new InvalidOperationException(
                    $"ZQWL 盒子已失联（{_portName}）：请拔插 CAN 盒子后重新连接");
            var port = _port;
            Exception? writeEx = null;
            long tWriteStart = Environment.TickCount64;
            var writeTask = Task.Run(() =>
            {
                try { port.Write(buf, 0, buf.Length); }
                catch (Exception ex) { writeEx = ex; }   // 就地消化，任务永不 fault
            });

            // 写挂起三级处置（2026-11-12 二修）：purge 在本 USB 驱动上拦不断
            // 挂起写（用户实测杀口复发）——首选「静默排水」：停止灌入、等盒子
            // 队列自然排空（无 ACK 帧经 CAN 错误重试终结后出队），僵尸写自然
            // 返回、端口无恙；排不动才 purge 中止；仍不行才杀口+自愈（最后手段）。
            bool stalled = !writeTask.Wait(WriteStallAbortMs);
            bool purged = false;
            if (stalled)
            {
                Volatile.Write(ref _congestedUntilMs, Environment.TickCount64 + CongestedPaceMs);
                stalled = !writeTask.Wait(WriteDrainMs);   // 静默排水：不 purge、不杀口
            }
            if (stalled)
            {
                purged = true;
                try { port.DiscardOutBuffer(); } catch { /* ignore */ }
                stalled = !writeTask.Wait(500);
            }
            if (!stalled && writeEx is null)
            {
                // 写最终完整成功（含排水后自然完成）：清中止计数；慢写视背压
                // 冷却 + 拥塞退避，后续发送自动放慢防再积压
                _abortStreak = 0;
                long took = Environment.TickCount64 - tWriteStart;
                if (took > WriteBackpressureMs)
                {
                    Volatile.Write(ref _congestedUntilMs, Environment.TickCount64 + CongestedPaceMs);
                    Thread.Sleep((int)(took > 2000 ? 2000 : took));
                }
                Volatile.Write(ref _lastSendTickMs, Environment.TickCount64);
                return;
            }

            _abortStreak++;
            Volatile.Write(ref _congestedUntilMs, Environment.TickCount64 + CongestedPaceMs);
            string busHint = Environment.TickCount64 - Volatile.Read(ref _lastCanRxTickMs) > BusSilentAfterMs
                ? "总线静默（设备离线/休眠中）"
                : "盒子队列积压/写通道卡顿";
            string reason = stalled
                ? $"串口写挂起（{Environment.TickCount64 - tWriteStart}ms，purge 无效）"
                : purged
                    ? $"串口写挂起已中止（{Environment.TickCount64 - tWriteStart}ms，purge 拦截）"
                    : "串口写入失败：" + writeEx!.Message;

            // 端口健在且未达升级阈值：不杀口——写通道保活，调用方重试即可
            if (!stalled && _port is { IsOpen: true } && _abortStreak < WriteAbortStreakLimit)
                throw new InvalidOperationException(
                    $"{reason}（{busHint}）：请稍后重试本次操作");

            // 升级：purge 拦不住的僵尸写 / 连续中止 3 次 / 端口已死 → 杀口+自愈
            _writeDead = true;
            try { port.Close(); } catch { /* ignore */ }
            try { writeTask.Wait(1000); } catch { /* ignore */ }   // 等僵尸写退干净
            if (TryReopenAndReconfigure())
            {
                _abortStreak = 0;
                throw new InvalidOperationException(
                    $"{reason}（{busHint}）：已自动重连盒子，请重试本次操作");
            }
            throw new InvalidOperationException(
                $"{reason}（{busHint}）且自动重连失败：USB 盒子无响应。请拔插 CAN 盒子后重新连接");
        }
    }

    /// <summary>
    /// 写挂起/失败后的自愈：关口（打断僵尸写）→ 重开同一 COM（设备仍枚举时
    /// 无需拔插）→ 重下盒子配置（与 Open 同序列，幂等）→ 清收包残帧。
    /// 成功返回 true（连接保持可用）；失败置 _writeDead 并关端口。
    /// </summary>
    private bool TryReopenAndReconfigure()
    {
        if (_healing || _disposed)
            return false;
        _healing = true;
        try
        {
            var port = _port;
            if (port is null)
                return false;
            if (!port.IsOpen)
                port.Open();
            _writeDead = false;
            _abortStreak = 0;
            Volatile.Write(ref _lastCanRxTickMs, Environment.TickCount64);
            Volatile.Write(ref _congestedUntilMs, 0);
            ApplyBoxConfig();
            lock (_rxLock)
            {
                _rxQueue.Clear();
                _bufLen = 0;
            }
            OnRawLog?.Invoke("[自愈] 写通道挂起已杀口并自动重连、重配盒子");
            return true;
        }
        catch
        {
            _writeDead = true;
            try { _port?.Close(); } catch { /* ignore */ }
            return false;
        }
        finally
        {
            _healing = false;
        }
    }

    /// <summary>49 3B 配置帧：func + rw + 16B payload + 45 2E。</summary>
    private static byte[] Cfg(byte func, byte rw, byte[]? payload = null)
    {
        var d = new byte[22];
        d[0] = 0x49;
        d[1] = 0x3B;
        d[2] = func;
        d[3] = rw;
        if (payload is { Length: > 0 })
            Array.Copy(payload, 0, d, 4, Math.Min(16, payload.Length));
        d[20] = 0x45;
        d[21] = 0x2E;
        return d;
    }

    public void Open()
    {
        if (_port is null) throw new ObjectDisposedException(nameof(ZqwlSerialTransport));
        _writeDead = false;   // 重开（含拔插后新实例）恢复写通道
        _abortStreak = 0;
        // 新连接按总线活跃起算（未见 CAN 帧前不限速，避免开流配置被节流）
        Volatile.Write(ref _lastCanRxTickMs, Environment.TickCount64);
        Volatile.Write(ref _congestedUntilMs, 0);
        TimerResolution.Enter();   // 1ms 节拍：Thread.Sleep(1) 才是真 1ms，否则 15ms
        bool ok = false;
        try
        {
            try
            {
                if (!_port.IsOpen) _port.Open();
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException(
                    $"打开 {_portName} 被拒绝（串口被占用）：" +
                    "①关掉其它占用该口的程序（旧的上位机/串口助手/SecureCRT）；" +
                    "②若曾 attach 到 WSL，管理员 PowerShell 执行 usbipd detach；" +
                    "③拔插一次 USB 盒子；④任务管理器结束残留的 QiChargerStationTool 进程。" +
                    " 详情：" + ex.Message, ex);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"打开 {_portName} 失败：设备可能已拔出或驱动异常，请重新插拔 USB。详情：{ex.Message}", ex);
            }

            OnRawLog?.Invoke($"[OPEN] {_portName} @ {_port.BaudRate}");

            // 与 zqwl_can_listen.py open_can0_250k() 同构，缺一步 CAN 就不会开
            ApplyBoxConfig();
            OnRawLog?.Invoke("[OPEN] CAN0 250k ext ready");
            ok = true;
        }
        finally
        {
            /* 打开失败也要归还定时器精度引用（成功路径由 Close 归还） */
            if (!ok) TimerResolution.Leave();
        }
    }

    /// <summary>
    /// 下发盒子配置（与 zqwl_can_listen.py open_can0_250k() 同构，缺一步
    /// CAN 就不会开；重下发幂等，Open 与写挂起自愈共用）。
    /// </summary>
    private void ApplyBoxConfig()
    {
        // 1) 读设备信息
        WriteRaw(Cfg(0x40, 0x52), "read device info");
        Thread.Sleep(200);
        Drain(80);

        // 2) 仲裁 250k / 数据 500k → 0x45，ch=0
        WriteRaw(Cfg(0x42, 0x57, new byte[] { 0x00, 0x00, 0x45 }), "baud 250k/500k");
        Thread.Sleep(50);

        // 3) 滤波组0 扩展帧全收；组1 标准帧全收
        WriteRaw(Cfg(0x43, 0x57, new byte[]
        {
            0x00, 0x00, 0x01, 0x01,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        }), "filter ext all");
        Thread.Sleep(50);
        WriteRaw(Cfg(0x43, 0x57, new byte[]
        {
            0x00, 0x01, 0x01, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        }), "filter std all");
        Thread.Sleep(50);

        // 4) 打开 CAN0：不写 flash、不复位（0x44 的复位位会把 USB 打掉）
        WriteRaw(Cfg(0x44, 0x57, new byte[] { 0x00, 0x00, 0x01, 0x00 }), "open CAN0");
        Thread.Sleep(150);
        Drain(80);
    }

    public void Close()
    {
        try { _port?.Close(); } catch { /* ignore */ }
        TimerResolution.Leave();
    }

    public void Send(in CanFrame frame)
    {
        if (_port is not { IsOpen: true })
            throw new InvalidOperationException($"ZQWL port {_portName} not open");
        PaceIfNeeded();

        byte[] data = frame.Data.Length >= 8 ? frame.Data.AsSpan(0, 8).ToArray() : frame.Data;
        byte dlc = (byte)data.Length;
        uint id = frame.Id & 0x1FFFFFFF;
        var pkt = new byte[3 + 4 + dlc + 1];
        pkt[0] = CanHdr;
        pkt[1] = (byte)(dlc & 0x7F);
        pkt[2] = 0x04; // extended data frame
        pkt[3] = (byte)((id >> 24) & 0x7F);
        pkt[4] = (byte)((id >> 16) & 0xFF);
        pkt[5] = (byte)((id >> 8) & 0xFF);
        pkt[6] = (byte)(id & 0xFF);
        data.CopyTo(pkt, 7);
        pkt[^1] = CanFtr;
        WriteBytes(pkt);
        OnRawLog?.Invoke($"[CAN-TX] {id:X8} {Convert.ToHexString(data)}");
    }

    /// <summary>
    /// 多帧单次写入。Windows USB-CDC 每次 SerialPort.Write 有 ~30ms 往返，
    /// 18 个 CF 逐帧写=640ms/块；拼成一个缓冲区一次写掉。
    /// </summary>
    public void SendBatch(IReadOnlyList<CanFrame> frames)
    {
        if (_port is not { IsOpen: true })
            throw new InvalidOperationException($"ZQWL port {_portName} not open");
        if (frames.Count == 0) return;
        if (frames.Count == 1) { Send(frames[0]); return; }
        // 总线静默/拥塞退避时拆单帧发送（每帧限速）：整批一次写入等于把一批
        // 无 ACK 帧灌进盒子 TX 队列——队列积压挂起的直接来源
        if (Environment.TickCount64 - Volatile.Read(ref _lastCanRxTickMs) > BusSilentAfterMs ||
            Environment.TickCount64 < Volatile.Read(ref _congestedUntilMs))
        {
            foreach (var f in frames)
                Send(f);
            return;
        }

        var pkts = new byte[frames.Count][];
        int total = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            byte[] data = f.Data.Length >= 8 ? f.Data.AsSpan(0, 8).ToArray() : f.Data;
            byte dlc = (byte)data.Length;
            uint id = f.Id & 0x1FFFFFFF;
            var pkt = new byte[3 + 4 + dlc + 1];
            pkt[0] = CanHdr;
            pkt[1] = (byte)(dlc & 0x7F);
            pkt[2] = 0x04;
            pkt[3] = (byte)((id >> 24) & 0x7F);
            pkt[4] = (byte)((id >> 16) & 0xFF);
            pkt[5] = (byte)((id >> 8) & 0xFF);
            pkt[6] = (byte)(id & 0xFF);
            data.CopyTo(pkt, 7);
            pkt[^1] = CanFtr;
            pkts[i] = pkt;
            total += pkt.Length;
        }
        var buf = new byte[total];
        int off = 0;
        foreach (var p in pkts)
        {
            Buffer.BlockCopy(p, 0, buf, off, p.Length);
            off += p.Length;
        }
        WriteBytes(buf);
        // 批量帧只打一条汇总：逐帧日志在 0x36 传输（36 CF/块）期间是纯开销
        if (frames.Count <= 4)
        {
            foreach (var f in frames)
                OnRawLog?.Invoke($"[CAN-TX] {f.Id & 0x1FFFFFFF:X8} {Convert.ToHexString(f.Data)}");
        }
        else
        {
            OnRawLog?.Invoke($"[CAN-TX] batch {frames.Count} 帧 id={frames[0].Id & 0x1FFFFFFF:X8}");
        }
    }

    public bool TryReceive(out CanFrame frame, int timeoutMs = 0)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            // 每轮主动泵串口——不能依赖 DataReceived 事件（USB-CDC 虚拟
            // 串口事件不可靠，断触即全静默；timeoutMs=0 时旧逻辑根本走不到
            // PumpSerial，接收完全押在事件上）
            PumpSerial();
            lock (_rxLock)
            {
                if (_rxQueue.Count > 0)
                {
                    frame = _rxQueue.Dequeue();
                    return true;
                }
            }
            if (Environment.TickCount64 >= deadline)
            {
                frame = default;
                return false;
            }
            Thread.Sleep(1);
        }
    }

    public void Drain(int quietMs = 50)
    {
        var last = Environment.TickCount64;
        while (Environment.TickCount64 - last < quietMs)
        {
            PumpSerial();
            lock (_rxLock)
            {
                if (_rxQueue.Count > 0)
                {
                    _rxQueue.Clear();
                    last = Environment.TickCount64;
                }
            }
            Thread.Sleep(2);
        }
    }

    private void OnData(object sender, System.IO.Ports.SerialDataReceivedEventArgs e)
        => PumpSerial();

    private void PumpSerial()
    {
        if (_port is not { IsOpen: true }) return;
        try
        {
            int n = _port.BytesToRead;
            if (n <= 0) return;
            var tmp = new byte[n];
            int got = _port.Read(tmp, 0, n);
            if (got <= 0) return;
            lock (_rxLock)
            {
                // 满了先丢弃旧数据，避免 _bufLen 越界
                if (_bufLen + got > _buf.Length)
                    _bufLen = 0;
                if (got > _buf.Length)
                {
                    // 单次超大块：只保留尾部可容纳部分
                    tmp.AsSpan(got - _buf.Length).CopyTo(_buf);
                    _bufLen = _buf.Length;
                }
                else
                {
                    Buffer.BlockCopy(tmp, 0, _buf, _bufLen, got);
                    _bufLen += got;
                }
                ParseBuffer();
            }
        }
        catch (TimeoutException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void ParseBuffer()
    {
        // 防御：_bufLen 不得超过缓冲区
        if (_bufLen > _buf.Length) _bufLen = _buf.Length;
        if (_bufLen < 0) _bufLen = 0;

        int i = 0;
        while (i < _bufLen)
        {
            // heartbeat / cfg replies start with 49 3B —— 静默丢弃
            if (i + 1 < _bufLen && _buf[i] == 0x49 && _buf[i + 1] == 0x3B)
            {
                if (_bufLen - i < 22) break;
                i += 22;
                continue;
            }
            if (_buf[i] != CanHdr)
            {
                i++;
                continue;
            }
            if (_bufLen - i < 8) break;
            byte b1 = _buf[i + 1];
            if (b1 is 0xFF or 0xFE)
            {
                // 盒子状态心跳（FF=1/2路17B / FE=4路32B）：静默丢弃，不进日志
                int hb = b1 == 0xFF ? 17 : 32;
                if (_bufLen - i < hb) break;
                i += hb;
                continue;
            }
            int dlc = b1 & 0x7F;
            if (dlc > 64) { i++; continue; }
            int total = 3 + 4 + dlc + 1;
            if (_bufLen - i < total) break;
            if (_buf[i + total - 1] != CanFtr) { i++; continue; }

            uint id = ((uint)(_buf[i + 3] & 0x7F) << 24)
                    | ((uint)_buf[i + 4] << 16)
                    | ((uint)_buf[i + 5] << 8)
                    | _buf[i + 6];
            var data = new byte[dlc];
            Buffer.BlockCopy(_buf, i + 7, data, 0, dlc);
            _rxQueue.Enqueue(new CanFrame(id, data));
            // 总线活性：收到 CAN 帧 = 设备在发帧 = 其控制器在 ACK——
            // 静默限速与写挂起诊断的依据
            Volatile.Write(ref _lastCanRxTickMs, Environment.TickCount64);
            // 睡眠入口标记（SLEEP 生命周期 0x18FF260D[0]=04 / E0~E3 快照
            // 0x18FF270D[0]=E0..E3）：记录时刻供上层入睡临界窗避让
            if (data.Length > 0 &&
                ((id == 0x18FF260DU && data[0] == 0x04) ||
                 (id == 0x18FF270DU && data[0] is >= 0xE0 and <= 0xE3)))
            {
                LastSleepMarkerTickMs = Environment.TickCount64;
            }
            OnRawLog?.Invoke($"[CAN-RX] {id:X8} {Convert.ToHexString(data)}");
            i += total;
        }
        if (i > 0)
        {
            int left = _bufLen - i;
            if (left > 0) Buffer.BlockCopy(_buf, i, _buf, 0, left);
            _bufLen = left;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
        _port?.Dispose();
    }
}
