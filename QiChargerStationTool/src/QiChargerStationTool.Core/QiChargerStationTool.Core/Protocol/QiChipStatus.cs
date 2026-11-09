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
    /// <summary>DID 0x2118 PA0 霍尔：0x00=磁铁/手机在位，0x01=不在位。</summary>
    public byte? ClampState { get; init; }

    public string IapStateName => QiChipStatusCodec.IapStateName(IapState);
    public string ChargeStateName => ChargeState is { } s ? QiChipStatusCodec.ChargeStateName(s) : "读取失败";
    public string FodName => FodStatus is { } f ? QiChipStatusCodec.FodName(f) : "读取失败";
    public string FaultName => FaultCode is { } f ? QiChipStatusCodec.FaultName(f) : "读取失败";
    public string PowerLimitName => PowerLimit is { } p ? QiChipStatusCodec.PowerLimitName(p) : "读取失败";
    /// <summary>磁铁存在与否（实测极性：0x01=存在）；读取失败为 null。</summary>
    public bool? MagnetPresent => ClampState is { } s ? s == 0x01 : null;
    public string MagnetName => QiChipStatusCodec.MagnetName(ClampState);
    /// <summary>实际充电判定（与固件 board_charge_poll 门禁同逻辑）。</summary>
    public string ChargingVerdict(bool chargeEnabled) => QiChipStatusCodec.ChargingVerdict(chargeEnabled, ClampState);
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
    /// <summary>磁铁状态名。实测极性（2026-11-05）：0x00=磁铁不存在（充电中），
    /// 0x01=磁铁存在（未充电），null=读取失败。</summary>
    public static string MagnetName(byte? clampState) => clampState switch
    {
        0x00 => "磁铁不存在",
        0x01 => "磁铁存在",
        null => "读取失败",
        _ => $"未知(0x{clampState:X2})",
    };

    /// <summary>
    /// 充电状态判定（与固件 board_charge_poll 门禁同极性）：
    /// 充电使能后 0x00=充电中、0x01=未充电；未使能=充电失能。
    /// </summary>
    public static string ChargingVerdict(bool chargeEnabled, byte? clampState)
    {
        if (!chargeEnabled)
            return "充电失能";
        return clampState switch
        {
            0x00 => "磁铁不存在·充电中",
            0x01 => "磁铁存在·未充电",
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
