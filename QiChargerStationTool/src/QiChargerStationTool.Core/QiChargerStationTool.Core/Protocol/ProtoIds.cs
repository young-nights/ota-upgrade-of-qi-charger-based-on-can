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
}
