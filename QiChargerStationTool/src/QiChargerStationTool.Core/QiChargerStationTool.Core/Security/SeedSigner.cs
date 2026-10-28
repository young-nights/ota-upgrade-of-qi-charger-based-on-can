using System.Security.Cryptography;
using QiChargerStationTool.Core.Protocol;

namespace QiChargerStationTool.Core.Security;

/// <summary>
/// ECDSA P-256 seed signer. Loads PEM private key from
/// 仓库 docs/keys/private.pem 或输出目录 keys/private.pem
/// (same key as python_tools packaging/sign_seed.py).
/// Signature = r||s (64B raw) over SHA256(seed) — matches firmware uECC_verify.
/// </summary>
public sealed class SeedSigner
{
    private readonly ECDsa _ecdsa;

    private SeedSigner(ECDsa ecdsa) => _ecdsa = ecdsa;

    public static SeedSigner? FromDefaultKey()
    {
        foreach (var p in EnumerateKeyCandidates())
        {
            if (!File.Exists(p)) continue;
            try
            {
                return FromPem(File.ReadAllText(p));
            }
            catch { /* try next */ }
        }
        return null;
    }

    /// <summary>
    /// 密钥搜索顺序：
    /// 1. 输出目录 keys/private.pem（产线拷贝）
    /// 2. 从工作目录/输出目录向上找仓库 docs/keys/private.pem
    ///    （开发时直接指向 ota-upgrade-of-qi-charger-based-on-can/docs/keys）
    /// </summary>
    private static IEnumerable<string> EnumerateKeyCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "keys", "private.pem");

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            // 深度 16：bin/Release/net8.0-windows/win-x64 到仓库根要 8 跳，
            // 旧值 8 差一层够不着 docs/keys（Release 目录启动 SA 解锁找不到私钥）
            for (int i = 0; i < 16 && dir is not null; i++, dir = dir.Parent)
            {
                yield return Path.Combine(dir.FullName, "docs", "keys", "private.pem");
                yield return Path.Combine(dir.FullName, "keys", "private.pem");
            }
        }
    }

    public static SeedSigner FromPem(string pem)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(pem);
        return new SeedSigner(ecdsa);
    }

    /// <summary>Sign 32-byte seed → 64-byte r||s.</summary>
    public byte[] SignSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32) throw new ArgumentException("seed must be 32 bytes");
        byte[] hash = SHA256.HashData(seed.ToArray());
        var sig = _ecdsa.SignHash(hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (sig.Length != 64) throw new InvalidOperationException($"unexpected sig len {sig.Length}");
        return sig;
    }
}

/// <summary>SecurityAccess unlock: 27 01 seed → 27 03×16 → 27 02. 全程 TryRequest，不抛异常。</summary>
public static class SecurityAccess
{
    public static async Task<bool> UnlockAsync(
        UdsClient uds, SeedSigner signer, Action<string> log, CancellationToken ct)
    {
        await Task.Yield();
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            if (ct.IsCancellationRequested) return false;
            try
            {
                var seedR = uds.TryRequest(ProtoIds.SidSa, new byte[] { 0x01 }, timeoutMs: 5000, ct: ct);
                if (!seedR.Ok)
                {
                    log($"27 01 失败: {seedR.Error}");
                    if (seedR.Nrc is 0x36 or 0x37)
                    {
                        log("SA 锁定，3E 保活等待 30s…");
                        await KeepAliveWaitAsync(uds, ct);
                        continue;
                    }
                    await Task.Delay(500, ct);
                    continue;
                }
                var seedResp = seedR.Data;
                if (seedResp.Length < 34)
                {
                    log($"27 01 响应过短 {seedResp.Length}");
                    continue;
                }
                var seed = seedResp.AsSpan(2, 32).ToArray();
                if (seed.All(b => b == 0))
                {
                    log("已解锁 (seed=0)");
                    return true;
                }
                log($"seed {Convert.ToHexString(seed)}");

                var sig = signer.SignSeed(seed);
                log("签名完成 64B，27 03 分片…");

                // 27 03 chunks 4B × 16 + 3E 80 keepalive every 4
                bool chunksOk = true;
                for (int seq = 1; seq <= 16; seq++)
                {
                    if (ct.IsCancellationRequested) return false;
                    if (seq is 5 or 9 or 13) uds.KeepAlive();
                    var piece = sig.AsSpan((seq - 1) * 4, 4).ToArray();
                    var chunk = new byte[] { 0x03, (byte)seq, piece[0], piece[1], piece[2], piece[3] };
                    bool oneOk = false;
                    for (int t = 0; t < 3; t++)
                    {
                        var cr = uds.TryRequest(ProtoIds.SidSa, chunk, timeoutMs: 3000, expectSub: 0x03, ct: ct);
                        if (cr.Ok) { oneOk = true; break; }
                        await Task.Delay(200, ct);
                    }
                    if (!oneOk)
                    {
                        log($"27 03 分片 seq={seq} 失败");
                        chunksOk = false;
                        break;
                    }
                }
                if (!chunksOk) continue;

                var fin = uds.TryRequest(ProtoIds.SidSa, new byte[] { 0x02 }, timeoutMs: 15_000,
                    waitPendingMs: 90_000, expectSub: 0x02, ct: ct);
                if (!fin.Ok)
                {
                    log($"27 02 失败: {fin.Error}");
                    if (fin.Nrc is 0x36 or 0x37)
                    {
                        log("SA 锁定，3E 保活等待 30s…");
                        await KeepAliveWaitAsync(uds, ct);
                    }
                    continue;
                }
                log("SA 解锁成功");
                return true;
            }
            catch (Exception ex)
            {
                log($"SA 第 {attempt} 次: {ex.Message}");
                await Task.Delay(500, ct);
            }
        }
        return false;
    }

    private static async Task KeepAliveWaitAsync(UdsClient uds, CancellationToken ct)
    {
        for (int i = 0; i < 10; i++)
        {
            if (ct.IsCancellationRequested) return;
            uds.KeepAlive();
            await Task.Delay(3000, ct);
        }
    }
}
