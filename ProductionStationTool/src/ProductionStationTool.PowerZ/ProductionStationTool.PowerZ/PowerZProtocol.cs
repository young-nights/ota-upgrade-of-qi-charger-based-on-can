namespace ProductionStationTool.PowerZ;

/// <summary>
/// POWER-Z KM003C 应用层协议（基本 ADC 轮询路径，无需加密）。
///
/// 协议来源：okhsunrog/km003c-protocol-research docs/protocol_reference.md
/// （KM003C V1.9.9 固件逆向 + USB 抓包）。包头 4 字节：
///   Byte0=[type:7][reserved:1]  Byte1=transaction_id  Byte2-3=attribute&lt;&lt;1(LE)
/// 基本轮询流程：Connect(0x02)→Accept(0x05)；GetData(0x0C,attr=0x0001)
/// →PutData(0x41)携带 ADC 44B（vbus_uV/ibus_uA/…）；Disconnect(0x03)。
///
/// 注意：用户设备若为 0483:5750（ST 方案兼容机）而非 5FC9:0063，
/// 本协议为最佳尝试——不响应时由上层把原始报文入日志供实测校准。
/// </summary>
public static class PowerZProtocol
{
    public const byte CmdConnect = 0x02;
    public const byte CmdDisconnect = 0x03;
    public const byte CmdAccept = 0x05;
    public const byte CmdReject = 0x06;
    public const byte CmdGetData = 0x0C;
    public const byte CmdPutData = 0x41;

    /// <summary>ADC 属性号（GetData 掩码位 0）。</summary>
    public const ushort AttrAdc = 0x0001;

    /// <summary>transaction_id 循环计数（0-255）。</summary>
    public static byte NextTid(ref byte tid) => tid++;

    public static byte[] Connect(byte tid) => new byte[] { CmdConnect, tid, 0x00, 0x00 };

    public static byte[] Disconnect(byte tid) => new byte[] { CmdDisconnect, tid, 0x00, 0x00 };

    /// <summary>GetData ADC：属性编码 = (attr &lt;&lt; 1) 进 Byte2-3（LE）。</summary>
    public static byte[] GetDataAdc(byte tid)
        => new byte[] { CmdGetData, tid, (byte)(AttrAdc << 1), 0x00 };

    /// <summary>响应包头类型（Byte0 低 7 位）。</summary>
    public static byte RespType(byte[] buf)
        => (byte)(buf[0] & 0x7F);

    /// <summary>
    /// 解析 PutData(0x41) 中的 ADC 载荷：
    /// [主头4B][扩展头4B][载荷]…，扩展头 bits0-14=attr、bit15=next、
    /// bits22-31=size。ADC 载荷 44B：i32 vbus_uV、i32 ibus_uA、…（LE）。
    /// </summary>
    public static bool TryParseAdc(byte[] buf, out byte tid, out PowerSample sample)
    {
        tid = 0;
        sample = default;
        if (buf.Length < 8)
            return false;

        byte type = RespType(buf);
        tid = buf[1];
        if (type != CmdPutData)
            return false;

        // 遍历逻辑包链，取 attr==ADC 的第一个
        int pos = 4;
        while (pos + 4 <= buf.Length)
        {
            uint ext = BitConverter.ToUInt32(buf, pos);
            ushort attr = (ushort)(ext & 0x7FFF);
            bool next = (ext & 0x8000) != 0;
            int size = (int)((ext >> 22) & 0x3FF);
            pos += 4;

            if (attr == AttrAdc && size >= 8 && pos + 8 <= buf.Length)
            {
                int vbusUv = BitConverter.ToInt32(buf, pos);
                int ibusUa = BitConverter.ToInt32(buf, pos + 4);
                sample = new PowerSample(vbusUv / 1_000_000.0,
                                         ibusUa / 1_000_000.0,
                                         DateTime.Now);
                return true;
            }

            pos += size;
            if (!next)
                break;
        }

        return false;
    }
}
