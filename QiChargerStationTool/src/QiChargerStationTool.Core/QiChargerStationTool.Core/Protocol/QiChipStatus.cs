namespace QiChargerStationTool.Core.Protocol;

/// <summary>
/// Qi 芯片状态全量查询结果。字段与 zcanpro_qi_read_status.py / can_protocol.c 读 DID 一致。
/// 状态查询任意会话可读，无需 SA 解锁。
/// </summary>
public sealed class QiChipStatus
{
    // IAP 状态（DID 0x2132）
    public byte IapState { get; init; }
    public byte IapProgress { get; init; }
    public byte QiVerMajor { get; init; }
    public byte QiVerMinor { get; init; }
    public byte QiVerPatch { get; init; }
    public ushort IapSent { get; init; }
    public ushort IapTotal { get; init; }

    // 充电 / 传感器
    public byte? ChargeState { get; init; }
    public ushort? OutputPowerMw { get; init; }
    public byte? InputVoltageRaw { get; init; }
    public byte? InputCurrentRaw { get; init; }
    public byte? PcbTempC { get; init; }
    public byte? FodStatus { get; init; }
    public byte? FaultCode { get; init; }
    public byte? ThermalDerate { get; init; }
    /// <summary>DID 0x210D 档位码（0x01/0x02/0x03）。</summary>
    public byte? PowerLimit { get; init; }
    /// <summary>DID 0x210E 充电次数（Qi 状态字节2 b0 上升沿计数，NVM 持久化）。</summary>
    public uint? ChargeCount { get; init; }
    /// <summary>DID 0x2111 Qi 状态字节（含保护位，见 <see cref="QiChipStatusCodec.QiStatusBits"/>）。</summary>
    public byte? StatusRaw { get; init; }
    /// <summary>DID 0x2111 [1] 状态字节2：b0=充电触发位（30s 去抖，充电中持续 1）。</summary>
    public byte? Status2Raw { get; init; }
    /// <summary>DID 0x2118 PA0 霍尔：0x00=磁铁存在，0x01=磁铁不存在（实测极性）。</summary>
    public byte? ClampState { get; init; }

    public string IapStateName => QiChipStatusCodec.IapStateName(IapState);
    public string ChargeStateName => ChargeState is { } s ? QiChipStatusCodec.ChargeStateName(s) : "读取失败";
    public string FodName => FodStatus is { } f ? QiChipStatusCodec.FodName(f) : "读取失败";
    public string FaultName => FaultCode is { } f ? QiChipStatusCodec.FaultName(f) : "读取失败";
    public string PowerLimitName => PowerLimit is { } p ? QiChipStatusCodec.PowerLimitName(p) : "读取失败";
    /// <summary>磁铁存在与否（实测极性：0x00=存在）；读取失败为 null。</summary>
    public bool? MagnetPresent => ClampState is { } s ? s == 0x00 : null;
    public string MagnetName => QiChipStatusCodec.MagnetName(ClampState);
    /// <summary>单采样真充证据（原值判定、未去抖）；显示层请经 DebounceFilter 滑动平滑。</summary>
    public bool ChargingEvidence => QiChipStatusCodec.ChargingEvidenceFrom(OutputPowerMw, InputCurrentRaw, Status2Raw);
    /// <summary>充电状态判定（chargingActive 由调用方经 2s 滑动去抖后传入）。</summary>
    public string ChargingVerdict(bool chargeEnabled, bool? chargingActive)
        => QiChipStatusCodec.ChargingVerdict(chargeEnabled, ClampState, chargingActive);
    /// <summary>激活的保护位名列表（如 [OVP, FOD]）；StatusRaw 读取失败返回 null。</summary>
    public IReadOnlyList<string>? ActiveProtections
        => StatusRaw is { } raw ? QiChipStatusCodec.ActiveProtectionNames(raw) : null;

    /// <summary>多行摘要，便于写日志 / 操作记录。</summary>
    public string ToDisplayString()
    {
        var lines = new List<string>
        {
            $"IAP 状态: {IapStateName}",
            $"升级进度: {IapProgress}%",
            $"Qi 版本:  {QiVerMajor}.{QiVerMinor}.{QiVerPatch}",
            $"已发送:   {IapSent} / {IapTotal} 字节",
            $"充电状态: {ChargeStateName}",
            $"输出功率: {(OutputPowerMw is { } pw ? $"{pw} mW" : "读取失败")}",
            $"输入电压: {(InputVoltageRaw is { } v ? $"{v} (raw)" : "读取失败")}",
            $"输入电流: {(InputCurrentRaw is { } c ? $"{c} (raw)" : "读取失败")}",
            $"PCB 温度: {(PcbTempC is { } t ? $"{t}℃" : "读取失败")}",
            $"FOD 状态: {FodName}",
            $"故障码:   {FaultName}",
            $"热降额:   {(ThermalDerate is { } d ? $"{d}%" : "读取失败")}",
            $"功率档位: {PowerLimitName}",
            $"充电次数: {(ChargeCount is { } cc ? $"{cc} 次" : "读取失败")}",
            $"磁铁状态: {MagnetName}",
            $"保护位:   {(ActiveProtections is { } ap ? (ap.Count > 0 ? string.Join(" / ", ap) : "无") : "读取失败")}",
        };
        return string.Join("\n", lines);
    }
}

/// <summary>Qi 状态 DID 编解码（纯函数，便于单测）。</summary>
public static class QiChipStatusCodec
{
    // ---- DID 0x2111 Qi 状态字节保护位（qi_protocol.h QI_STATUS_*）----
    /// <summary>b3: 1=过压保护。</summary>
    public const byte QiStatusOvp = 1 << 3;
    /// <summary>b4: 1=欠压保护。</summary>
    public const byte QiStatusUvp = 1 << 4;
    /// <summary>b5: 1=过流保护。</summary>
    public const byte QiStatusOcp = 1 << 5;
    /// <summary>b6: 1=过温保护。</summary>
    public const byte QiStatusOtp = 1 << 6;
    /// <summary>b7: 1=异物检测保护。</summary>
    public const byte QiStatusFod = 1 << 7;
    /// <summary>b0: 1=检测到设备放置。</summary>
    public const byte QiStatusPing = 1 << 0;
    /// <summary>b1: 1=正在充电。</summary>
    public const byte QiStatusCharging = 1 << 1;
    /// <summary>b2: 1=电池已充满。</summary>
    public const byte QiStatusFull = 1 << 2;

    /// <summary>全部保护位 → 名称（用于 UI 指示灯）。</summary>
    public static readonly IReadOnlyList<(byte Bit, string Name)> ProtectionBits = new[]
    {
        (QiStatusOvp, "OVP 过压"),
        (QiStatusUvp, "UVP 欠压"),
        (QiStatusOcp, "OCP 过流"),
        (QiStatusOtp, "OTP 过温"),
        (QiStatusFod, "FOD 异物"),
    };

    /// <summary>状态字节中激活的保护位名列表（无激活返回空列表）。</summary>
    public static IReadOnlyList<string> ActiveProtectionNames(byte statusRaw)
    {
        var list = new List<string>();
        foreach (var (bit, name) in ProtectionBits)
            if ((statusRaw & bit) != 0)
                list.Add(name);
        return list;
    }

    // ---- DID 0x2118 PA0 霍尔 / 充电状态判定 ----
    /// <summary>磁铁状态名。实测极性：0x00=磁铁存在（未充电），0x01=磁铁不存在（充电中），
    /// null=读取失败。</summary>
    public static string MagnetName(byte? clampState) => clampState switch
    {
        0x00 => "磁铁存在",
        0x01 => "磁铁不存在",
        null => "读取失败",
        _ => $"未知(0x{clampState:X2})",
    };

    /// <summary>
    /// 真充证据（单采样，全部用 Qi 传回原值、不做单位换算）：
    /// 电流 raw>0 或 功率>0 或 充电触发位=1——任一为真即有证据。
    /// 充电时电流远大于 1A（raw 显著非 0）；空载时功率仅小幅跳动、电流趋 0。
    /// </summary>
    public static bool ChargingEvidenceFrom(ushort? powerMw, byte? currentRaw, byte? status2)
    {
        bool? trig = status2 is { } s ? (s & 0x01) != 0 : null;
        return (powerMw is { } p && p > 0)
            || (currentRaw is { } c && c > 0)
            || trig == true;
    }

    /// <summary>
    /// 真充判定（含 ~2s 滑动采样去抖）：单采样证据经 <see cref="DebounceFilter"/>
    /// 平滑后再出结论——空载小幅跳动不翻转状态，持续证据 2s 才判充电中。
    /// </summary>
    public static bool ChargingActiveFrom(ushort? powerMw, byte? currentRaw,
                                          byte? status2, DebounceFilter filter, long nowMs)
    {
        return filter.Update(ChargingEvidenceFrom(powerMw, currentRaw, status2), nowMs);
    }

    /// <summary>
    /// 充电状态判定：未使能=充电失能；磁铁存在=未充电；磁铁不存在时按真充
    /// 证据区分「充电中 / 未充电」。
    /// </summary>
    public static string ChargingVerdict(bool chargeEnabled, byte? clampState, bool? chargingActive)
    {
        if (!chargeEnabled)
            return "充电失能";
        return clampState switch
        {
            0x00 => "磁铁存在·未充电",
            0x01 => chargingActive == true ? "磁铁不存在·充电中" : "磁铁不存在·未充电",
            null => "充电使能·磁铁状态未知",
            _ => $"充电使能·磁铁状态未知(0x{clampState:X2})",
        };
    }
    public static readonly IReadOnlyDictionary<byte, string> IapStates = new Dictionary<byte, string>
    {
        [0x00] = "空闲 (IDLE)",
        [0x01] = "升级中 (IN_PROGRESS)",
        [0x02] = "成功 (SUCCESS)",
        [0x03] = "失败 (FAILED)",
    };

    public static readonly IReadOnlyDictionary<byte, string> ChargeStates = new Dictionary<byte, string>
    {
        [0x00] = "关闭 (DISABLED)",
        [0x01] = "待机 (STANDBY)",
        [0x02] = "设备已检测 (DEVICE_DETECTED)",
        [0x03] = "协商中 (NEGOTIATING)",
        [0x04] = "充电中 (CHARGING)",
        [0x05] = "充满 (COMPLETE)",
        [0x06] = "热保护暂停 (SUSPENDED_THERMAL)",
        [0x07] = "FOD 暂停 (SUSPENDED_FOD)",
        [0x08] = "故障 (FAULT)",
        [0x09] = "服务模式 (SERVICE_MODE)",
        [0x0A] = "低功耗 (LOW_POWER)",
    };

    public static readonly IReadOnlyDictionary<byte, string> FodStates = new Dictionary<byte, string>
    {
        [0x00] = "正常",
        [0x01] = "检测中",
        [0x02] = "已确认异物",
    };

    public static readonly IReadOnlyDictionary<byte, string> FaultCodes = new Dictionary<byte, string>
    {
        [0x00] = "无故障",
        [0x04] = "输入过压 (OVP)",
        [0x05] = "输入欠压 (UVP)",
        [0x06] = "异物检测 (FOD)",
        [0x07] = "线圈过温 (OTP)",
        [0x0A] = "输入过流 (OCP)",
    };

    public static string IapStateName(byte v) => IapStates.TryGetValue(v, out var n) ? n : $"未知(0x{v:X2})";
    public static string ChargeStateName(byte v) => ChargeStates.TryGetValue(v, out var n) ? n : $"未知(0x{v:X2})";
    public static string FodName(byte v) => FodStates.TryGetValue(v, out var n) ? n : $"未知(0x{v:X2})";
    public static string FaultName(byte v) => FaultCodes.TryGetValue(v, out var n) ? n : $"未知(0x{v:X2})";

    /// <summary>DID 0x210D 档位码名。0x01=5W / 0x02=10W / 0x03=15W（Qi 芯片协议 QI_POWER_*）。</summary>
    public static string PowerLimitName(byte v) => v switch
    {
        ProtoIds.Power5W => "5W (0x01)",
        ProtoIds.Power10W => "10W (0x02)",
        ProtoIds.Power15W => "15W (0x03)",
        _ => $"未知(0x{v:X2})",
    };

    /// <summary>UI 档位标签（W）→ Qi 芯片档位码。</summary>
    public static byte PowerCodeFromWatts(int watts) => watts switch
    {
        5 => ProtoIds.Power5W,
        10 => ProtoIds.Power10W,
        15 => ProtoIds.Power15W,
        _ => throw new ArgumentOutOfRangeException(nameof(watts), watts, "功率仅支持 5 / 10 / 15 W"),
    };

    /// <summary>解析 DID 0x2132 载荷（去掉 62 21 32 后的 9 字节）。</summary>
    public static void ParseIapStatus(ReadOnlySpan<byte> d,
        out byte state, out byte progress,
        out byte verMajor, out byte verMinor, out byte verPatch,
        out ushort sent, out ushort total)
    {
        if (d.Length < 9) throw new ArgumentException($"DID 0x2132 载荷过短: {d.Length}");
        state = d[0];
        progress = d[1];
        verMajor = d[2];
        verMinor = d[3];
        verPatch = d[4];
        sent = (ushort)(d[5] | (d[6] << 8));
        total = (ushort)(d[7] | (d[8] << 8));
    }

    /// <summary>单字节 DID 数据区（UdsClient.ReadDid 已去掉 62 xx xx）。</summary>
    public static byte ReadU8(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1) throw new ArgumentException("DID 数据区为空");
        return data[0];
    }

    /// <summary>uint16 LE DID 数据区。</summary>
    public static ushort ReadU16Le(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) throw new ArgumentException("DID 数据区不足 2 字节");
        return (ushort)(data[0] | (data[1] << 8));
    }

    /// <summary>uint32 LE DID 数据区。</summary>
    public static uint ReadU32Le(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) throw new ArgumentException("DID 数据区不足 4 字节");
        return (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
    }

    /// <summary>完整 RDBI 正响应（62 xx xx …）→ 数据区。</summary>
    public static ReadOnlySpan<byte> DidData(ReadOnlySpan<byte> rdbiResponse)
    {
        if (rdbiResponse.Length < 3) throw new ArgumentException("RDBI 响应过短");
        return rdbiResponse[3..];
    }
}

/// <summary>
/// 滑动采样去抖（时间窗）：新状态连续保持 ≥windowMs 才切换，双向滤除
/// 单点跳变——空载功率小幅跳动不翻状态、充电中功率瞬时回落也不翻状态。
/// 采样时刻由调用方注入（便于单测）。
/// </summary>
public sealed class DebounceFilter
{
    private readonly long _windowMs;
    private bool _state;
    private bool _pending;
    private long _pendingSinceMs;
    private bool _hasSample;

    public DebounceFilter(long windowMs) => _windowMs = windowMs;

    /// <summary>当前平滑后的状态。</summary>
    public bool State => _state;

    /// <summary>喂一个采样，返回平滑后的状态。</summary>
    public bool Update(bool sample, long nowMs)
    {
        if (!_hasSample)
        {
            _hasSample = true;
            _state = sample;
            _pending = sample;
            _pendingSinceMs = nowMs;
            return _state;
        }
        if (sample == _state)
        {
            /* 现态成立：清挂起切换 */
            _pending = sample;
            _pendingSinceMs = nowMs;
            return _state;
        }
        if (sample != _pending)
        {
            /* 新的反向采样：起算持续时间 */
            _pending = sample;
            _pendingSinceMs = nowMs;
            return _state;
        }
        if ((nowMs - _pendingSinceMs) >= _windowMs)
        {
            _state = sample;
        }
        return _state;
    }

    /// <summary>清零（充电使能/失能切换时调用，避免旧状态残留）。</summary>
    public void Reset()
    {
        _hasSample = false;
        _state = false;
        _pending = false;
        _pendingSinceMs = 0;
    }
}
