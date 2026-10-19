namespace QiChargerStationTool.Core.Protocol;

/// <summary>CAN / UDS identifiers and codes (same as firmware can_protocol.h).</summary>
public static class ProtoIds
{
    public const uint UdsReqId = 0x18DA0D03;
    public const uint UdsRespId = 0x18DA030D;
    public const uint LifecycleId = 0x18FF260D;
    public const uint BootDiagId = 0x18FF480D;
    public const uint LpDiagId = 0x18FF270D;

    public const byte SidDsc = 0x10;
    public const byte SidRdbi = 0x22;
    public const byte SidWdbi = 0x2E;
    public const byte SidSa = 0x27;
    public const byte SidRc = 0x31;
    public const byte SidRd = 0x34;
    public const byte SidTd = 0x36;
    public const byte SidRte = 0x37;
    public const byte SidTp = 0x3E;

    public const byte SidNrc = 0x7F;
    public const byte SidPr = 0x40;
    public const byte NrcRcrrp = 0x78;

    public const ushort DidSwVersion = 0xF195;
    public const ushort DidHwVersion = 0xF193;
    public const ushort DidSn = 0xF18C;
    public const ushort DidSlot = 0x2113;
    public const ushort DidFwType = 0x2010;
    public const ushort DidLpStatus = 0x2119;

    // ---- Qi chip (see qi_wireless_code_app can_protocol.h / zcanpro_qi_*.py) ----
    public const ushort DidQiIapStatus = 0x2132;   // 9B: state/progress/ver3/sentLE/totalLE
    public const ushort DidQiFwVersion = 0x2133;   // 3B major.minor.patch
    public const ushort DidChargeState = 0x2102;   // u8
    public const ushort DidOutputPower = 0x2104;   // u16 LE mW
    public const ushort DidInputVi = 0x2105;       // 2×u8 raw V / I
    public const ushort DidPcbTemp = 0x2108;       // u8 ℃
    public const ushort DidFodStatus = 0x2109;     // u8
    public const ushort DidFaultCode = 0x210B;     // u8
    public const ushort DidThermalDerate = 0x210C; // u8, 100=无降额
    public const ushort DidPowerLimit = 0x210D;    // u8 档位码, 仅 0x01/0x02/0x03

    /// <summary>
    /// DID 0x210D 功率档位码 —— 与 Qi 芯片协议 QI_POWER_* 一致：
    /// 0x01=5W / 0x02=10W / 0x03=15W。UI 的 5W/10W/15W 映射到这三个码。
    /// </summary>
    public const byte Power5W = 0x01;
    public const byte Power10W = 0x02;
    public const byte Power15W = 0x03;
}
