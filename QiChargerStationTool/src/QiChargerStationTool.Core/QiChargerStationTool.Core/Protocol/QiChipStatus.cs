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

    public string IapStateName => QiChipStatusCodec.IapStateName(IapState);
    public string ChargeStateName => ChargeState is { } s ? QiChipStatusCodec.ChargeStateName(s) : "读取失败";
    public string FodName => FodStatus is { } f ? QiChipStatusCodec.FodName(f) : "读取失败";
    public string FaultName => FaultCode is { } f ? QiChipStatusCodec.FaultName(f) : "读取失败";
    public string PowerLimitName => PowerLimit is { } p ? QiChipStatusCodec.PowerLimitName(p) : "读取失败";

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
        };
        return string.Join("\n", lines);
    }
}

/// <summary>Qi 状态 DID 编解码（纯函数，便于单测）。</summary>
public static class QiChipStatusCodec
{
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

    /// <summary>完整 RDBI 正响应（62 xx xx …）→ 数据区。</summary>
    public static ReadOnlySpan<byte> DidData(ReadOnlySpan<byte> rdbiResponse)
    {
        if (rdbiResponse.Length < 3) throw new ArgumentException("RDBI 响应过短");
        return rdbiResponse[3..];
    }
}
