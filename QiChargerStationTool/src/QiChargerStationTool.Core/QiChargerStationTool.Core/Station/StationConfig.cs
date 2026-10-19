namespace QiChargerStationTool.Core.Station;

public sealed class StationConfig
{
    public string ComPort { get; set; } = "COM9";
    public int Baud { get; set; } = 2_000_000;
    public string? FirmwarePath { get; set; }
    public string? SnCode { get; set; }
    public bool DoReadVersion { get; set; } = true;
    public bool DoReadSn { get; set; } = true;
    public bool DoWriteSn { get; set; }
    public bool DoOtaUpgrade { get; set; } = true;
    public bool DoChargeTest { get; set; }
    public bool DoQiStatusQuery { get; set; } = true;
    public bool DoSetQiPower { get; set; }
    /// <summary>DID 0x210D 档位：500=5W / 1000=10W / 1500=15W（固件仅认这三个值）。</summary>
    public ushort QiPowerLimit { get; set; } = 1500;
    public ushort ChargePowerLevel { get; set; } = 0x03; // 15W
    public int TransferBlock { get; set; } = 128;
    public string LogDirectory { get; set; } = "logs";
}

public enum TestStepStatus { Pending, Running, Passed, Failed, Skipped }

public sealed class TestStepResult
{
    public required string Name { get; init; }
    public TestStepStatus Status { get; set; } = TestStepStatus.Pending;
    public string Message { get; set; } = "";
    public TimeSpan Elapsed { get; set; }

    public override string ToString()
        => $"[{Status}] {Name} {Elapsed.TotalSeconds:F1}s {Message}";
}

public interface ITestStep
{
    string Name { get; }
    /// <summary>Failure aborts the remaining flow when true.</summary>
    bool Critical { get; }
    Task<TestStepResult> RunAsync(StationContext ctx, CancellationToken ct);
}

/// <summary>Shared runtime for a station run.</summary>
public sealed class StationContext
{
    public required StationConfig Config { get; init; }
    public required QiChargerStationTool.Core.Protocol.UdsClient Uds { get; init; }
    public string? SwVersion { get; set; }
    public string? Sn { get; set; }
    public byte Slot { get; set; }
    public QiChargerStationTool.Core.Protocol.QiChipStatus? QiStatus { get; set; }
    public event Action<string>? OnLog;

    public void Log(string m) => OnLog?.Invoke(m);
}
