namespace QiChargerStationTool.Core.Transport;

/// <summary>
/// CAN transport abstraction. Production default = ZQWL COM (serial);
/// optional ZLG zlgcan.dll can implement the same interface later.
/// </summary>
public interface ICanTransport : IDisposable
{
    bool IsOpen { get; }

    /// <summary>Open transport (COM port / USB-CAN device).</summary>
    void Open();

    void Close();

    /// <summary>Send one classical CAN frame.</summary>
    void Send(in CanFrame frame);

    /// <summary>
    /// Try dequeue one received frame. Returns false when queue is empty.
    /// </summary>
    bool TryReceive(out CanFrame frame, int timeoutMs = 0);

    /// <summary>Drain all pending frames.</summary>
    void Drain(int quietMs = 50);
}
