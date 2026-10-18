using System.Security.Cryptography;
using QiChargerStationTool.Core.Protocol;

namespace QiChargerStationTool.Core.Security;

/// <summary>
/// ECDSA P-256 seed signer. Loads PEM private key from docs/keys/private.pem
/// (same key as python_tools packaging/sign_seed.py).
/// Signature = r||s (64B raw) over SHA256(seed) — matches firmware uECC_verify.
/// </summary>
public sealed class SeedSigner
{
    private readonly ECDsa _ecdsa;

    private SeedSigner(ECDsa ecdsa) => _ecdsa = ecdsa;

    public static SeedSigner? FromDefaultKey()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "keys", "private.pem"),
            Path.Combine(AppContext.BaseDirectory, "docs", "keys", "private.pem"),
            "docs/keys/private.pem",
        };
        foreach (var p in candidates)
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

/// <summary>SecurityAccess unlock: 27 01 seed → 27 03×16 → 27 02.</summary>
public static class SecurityAccess
{
    public static async Task<bool> UnlockAsync(
        UdsClient uds, SeedSigner signer, Action<string> log, CancellationToken ct)
    {
        await Task.Yield();
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var seedResp = uds.Request(ProtoIds.SidSa, new byte[] { 0x01 }, timeoutMs: 5000);
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
                log($"签名完成 64B，27 03 分片…");

                // 27 03 chunks 4B × 16 + 3E 80 keepalive every 4
                for (int seq = 1; seq <= 16; seq++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (seq is 5 or 9 or 13) uds.KeepAlive();
                    var piece = sig.AsSpan((seq - 1) * 4, 4).ToArray();
                    var chunk = new byte[] { 0x03, (byte)seq, piece[0], piece[1], piece[2], piece[3] };
                    for (int t = 0; t < 3; t++)
                    {
                        try
                        {
                            uds.Request(ProtoIds.SidSa, chunk, timeoutMs: 3000, expectSub: 0x03);
                            break;
                        }
                        catch when (t < 2)
                        {
                            await Task.Delay(200, ct);
                        }
                    }
                }

                uds.Request(ProtoIds.SidSa, new byte[] { 0x02 }, timeoutMs: 15_000,
                            waitPendingMs: 90_000, expectSub: 0x02);
                log("SA 解锁成功");
                return true;
            }
            catch (UdsException ex) when (ex.Nrc is 0x36 or 0x37)
            {
                log($"SA 锁定 NRC 0x{ex.Nrc:X2}，3E 保活等待 30s…");
                for (int i = 0; i < 10; i++)
                {
                    uds.KeepAlive();
                    await Task.Delay(3000, ct);
                }
            }
            catch (Exception ex)
            {
                log($"SA 第 {attempt} 次: {ex.Message}");
                await Task.Delay(500, ct);
            }
        }
        return false;
    }
}
