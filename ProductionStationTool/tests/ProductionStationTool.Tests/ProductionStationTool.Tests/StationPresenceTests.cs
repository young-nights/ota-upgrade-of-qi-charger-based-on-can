using System.Net.Http;
using System.Text;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.Tests;

/// <summary>工位机自动接入通道：监听按 IP 记录上报、心跳上报端到端。</summary>
public class StationPresenceTests
{
    [Fact]
    public async Task Listener_RecordsReport_WithRemoteIp()
    {
        using var listener = new StationPresenceListener();
        var seen = new TaskCompletionSource<StationPresence>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.MachineSeen += p => seen.TrySetResult(p);
        listener.Start(0);   // 临时端口

        using var http = new HttpClient();
        var json = """{"code":"ST-01-1","stationNo":1,"machineId":"1"}""";
        var resp = await http.PostAsync(
            $"http://127.0.0.1:{listener.Port}/hello",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.True(resp.IsSuccessStatusCode);

        var got = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ST-01-1", got.Code);
        Assert.Equal(1, got.StationNo);
        Assert.Equal("1", got.MachineId);
        Assert.Equal("127.0.0.1", got.Ip);
    }

    [Fact]
    public async Task Reporter_Heartbeats_AndListenerSeesIt()
    {
        using var listener = new StationPresenceListener();
        var seen = new TaskCompletionSource<StationPresence>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.MachineSeen += p => seen.TrySetResult(p);
        listener.Start(0);

        using var reporter = new StationPresenceReporter(
            new StationIdentity(3, "2"), "127.0.0.1", listener.Port, TimeSpan.FromMilliseconds(200));
        reporter.Start();

        var got = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ST-03-2", got.Code);
        Assert.Equal(3, got.StationNo);
        Assert.Equal("2", got.MachineId);
        Assert.Equal("127.0.0.1", got.Ip);
    }

    [Fact]
    public async Task Listener_MalformedBody_StillRecordsIp()
    {
        using var listener = new StationPresenceListener();
        var seen = new TaskCompletionSource<StationPresence>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.MachineSeen += p => seen.TrySetResult(p);
        listener.Start(0);

        using var http = new HttpClient();
        await http.PostAsync($"http://127.0.0.1:{listener.Port}/hello",
            new StringContent("not-json", Encoding.UTF8, "application/json"));

        var got = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("127.0.0.1", got.Code);   // 无身份时以 IP 代编码，仍按 IP 记录
        Assert.Equal("127.0.0.1", got.Ip);
    }
}
