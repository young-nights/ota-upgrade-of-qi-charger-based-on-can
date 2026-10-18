namespace QiChargerStationTool.Core.Transport;

/// <summary>29-bit extended classical CAN frame.</summary>
public readonly record struct CanFrame(uint Id, byte[] Data)
{
    public int Dlc => Data.Length;

    public string ToHex()
        => $"{Id:X8} [{string.Join(" ", Data.Select(b => b.ToString("X2")))}]";

    public static CanFrame From(uint id, params byte[] data) => new(id, data);
}
