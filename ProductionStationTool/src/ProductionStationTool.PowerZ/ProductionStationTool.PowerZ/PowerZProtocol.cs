namespace ProductionStationTool.PowerZ;

/// <summary>
/// KINGMETER KM001（POWER-Z 贴牌，0483:5750）HID 应用层协议。
///
/// 协议来源：Power-Z.exe 官方上位机（SourceForge power-z-km001）逆向，
/// 帧格式经 WSL /dev/hidraw0 实测验证（2026-10-30）。
///
/// 输出报文（ReportID 0x55，64B）：
///   [0]=0x55 ReportID  [1]=0x06 载荷长  [2]=服务  [3..4]=命令字(LE)
///   [5]=子命令  [6]=参数  [7]=SUM8([0..6])  [8..9]=CRC16-Modbus([0..6])
/// 输入报文（ReportID 0xAA，64B）：
///   [0]=0xAA ReportID  [1]=长度  [2]=服务|0x40  [3..4]=命令字(LE)  [5]=子命令  [6..]=数据
///
/// 采样路径：服务 0x22（ReadDataByIdentifier）+ 命令字 0x0B05，
/// 子命令 1 触发连续采样流，每帧含 V/I 原始值（mV/mA，/1000 → V/A）。
/// </summary>
public static class PowerZProtocol
{
    /// <summary>输出报文 ReportID。</summary>
    public const byte ReportIdOut = 0x55;

    /// <summary>输入报文 ReportID。</summary>
    public const byte ReportIdIn = 0xAA;

    /// <summary>通用服务（应答魔数 0x71）。</summary>
    public const byte SvcGeneral = 0x31;

    /// <summary>ReadDataByIdentifier 服务（应答魔数 0x62）。</summary>
    public const byte SvcReadData = 0x22;

    /// <summary>测量数据命令字（V/I 采样）。</summary>
    public const ushort CmdMeasurement = 0x0B05;

    /// <summary>设备信息命令字。</summary>
    public const ushort CmdDeviceInfo = 0xF180;

    // ── 帧构造 ──────────────────────────────────────────────

    /// <summary>CRC16-Modbus（poly 0xA001，init 0xFFFF）。</summary>
    public static ushort Crc16Modbus(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    /// <summary>构造输出报文（64B，report id 0x55 打头）。</summary>
    public static byte[] BuildFrame(byte service, ushort cmd, byte sub = 0, byte param = 0)
    {
        var f = new byte[64];
        f[0] = ReportIdOut;
        f[1] = 0x06;
        f[2] = service;
        f[3] = (byte)(cmd & 0xFF);
        f[4] = (byte)(cmd >> 8);
        f[5] = sub;
        f[6] = param;
        byte sum = 0;
        for (int i = 0; i < 7; i++) sum += f[i];
        f[7] = sum;
        ushort crc = Crc16Modbus(f.AsSpan(0, 7));
        f[8] = (byte)(crc & 0xFF);
        f[9] = (byte)(crc >> 8);
        return f;
    }

    /// <summary>启动测量采样流（服务 0x22 + 0x0B05 + 子命令 1）。</summary>
    public static byte[] StartStreaming() => BuildFrame(SvcReadData, CmdMeasurement, sub: 1);

    /// <summary>读一次完整测量块（服务 0x22 + 0x0B05 + 子命令 0）。</summary>
    public static byte[] ReadBlock() => BuildFrame(SvcReadData, CmdMeasurement, sub: 0);

    /// <summary>读设备信息（服务 0x22 + 0xF180）。</summary>
    public static byte[] ReadDeviceInfo(byte sub = 0) => BuildFrame(SvcReadData, CmdDeviceInfo, sub: sub);

    // ── 帧解析 ──────────────────────────────────────────────

    /// <summary>输入报文是否为有效应答（ReportID 0xAA + 服务魔数匹配）。</summary>
    public static bool IsResponse(byte[] buf, out byte service, out ushort cmd, out byte sub)
    {
        service = 0; cmd = 0; sub = 0;
        if (buf.Length < 6 || buf[0] != ReportIdIn)
            return false;
        byte svc = (byte)(buf[2] & 0x3F);
        if (svc != SvcGeneral && svc != SvcReadData)
            return false;
        service = svc;
        cmd = (ushort)(buf[3] | (buf[4] << 8));
        sub = buf[5];
        return true;
    }

    /// <summary>
    /// 解析测量采样帧（服务 0x22 / 命令 0x0B05 / 子命令 1）。
    /// 数据区（报文偏移 6）：
    ///   [0..3]   u32 计数器/时间戳
    ///   [4..7]   u32 状态标志（0xFFFFFFFF=无效/未充电，1/3/6=有效）
    ///   [8..11]  u32 计数器2
    ///   [12..15] u32 电压原始值（mV）→ /1000 = V
    ///   [16..19] u32 电流原始值（mA）→ /1000 = A
    ///   [20..23] u32 计数器3
    ///   [24..27] u32 状态2
    ///   [28..29] i16 温度/辅助值（/10）
    /// </summary>
    public static bool TryParseSample(byte[] buf, out PowerSample sample)
    {
        sample = default;
        if (buf.Length < 26 || buf[0] != ReportIdIn)
            return false;
        if ((byte)(buf[2] & 0x3F) != SvcReadData)
            return false;
        ushort cmd = (ushort)(buf[3] | (buf[4] << 8));
        if (cmd != CmdMeasurement || buf[5] != 1)
            return false;

        int vRaw = BitConverter.ToInt32(buf, 6 + 12);
        int iRaw = BitConverter.ToInt32(buf, 6 + 16);
        if (vRaw < 0 || iRaw < 0 || vRaw > 100_000 || iRaw > 100_000)
            return false;

        sample = new PowerSample(vRaw / 1000.0, iRaw / 1000.0, DateTime.Now);
        return true;
    }

    /// <summary>解析完整测量块（子命令 0）：V/I 在数据区偏移 36/40，同样 /1000。</summary>
    public static bool TryParseBlock(byte[] buf, out PowerSample sample)
    {
        sample = default;
        if (buf.Length < 50 || buf[0] != ReportIdIn)
            return false;
        if ((byte)(buf[2] & 0x3F) != SvcReadData)
            return false;
        ushort cmd = (ushort)(buf[3] | (buf[4] << 8));
        if (cmd != CmdMeasurement || buf[5] != 0)
            return false;

        int vRaw = BitConverter.ToInt32(buf, 6 + 36);
        int iRaw = BitConverter.ToInt32(buf, 6 + 40);
        if (vRaw < 0 || iRaw < 0 || vRaw > 100_000 || iRaw > 100_000)
            return false;

        sample = new PowerSample(vRaw / 1000.0, iRaw / 1000.0, DateTime.Now);
        return true;
    }
}
