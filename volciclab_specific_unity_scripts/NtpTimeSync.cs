using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/*
 * NtpTimeSync — a corrected UTC clock for the headset.
 *
 * WHY
 * ----
 * The Quest keeps its own system clock, which is NTP-synced to the internet only
 * loosely. For experiments that correlate timestamps across several lab machines
 * (measuring network + streaming latency with unix time stamps), every clock
 * must sit in the same frame. This object is that anchor: at start it asks a real
 * NTP server for the time (a few samples), derives the offset between the
 * headset's local clock and that reference, and then exposes the corrected time
 * to every other script as a plain function call.
 *
 * HOW OTHER SCRIPTS USE IT
 * ------------------------
 *     long   nowMs = NtpTimeSync.UnixTimeNowInMs();          // corrected UTC, ms
 *     string now    = NtpTimeSync.UnixTimeNowInMs().ToString();// same, as a string
 *     if (!NtpTimeSync.IsSynced) Debug.LogWarning("timestamp is UNCORRECTED");
 *
 * This is the drop-in replacement for
 *     DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
 * — i.e. for the raw-clock timestamp `DataTransmitter` used to compute (its
 * `TimestampMs` now calls this directly). Before the first good
 * sample has landed (or if the server is unreachable) `IsSynced` is false and
 * `UnixTimeNowInMs()` returns the raw system clock (uncorrected), so callers
 * never have to special-case this and nothing ever blocks.
 *
 * ATTACH TO A PERMANENTLY ACTIVE OBJECT (e.g. [BuildingBlock] Camera Rig).
 * It is the single source of corrected time; do not duplicate or destroy it.
 * README setup step 48. Sibling doc: NtpTimeSync.cs.md.
 */
public class NtpTimeSync : MonoBehaviour
{
    [Header("NTP reference (must be in the same frame as the rest of the lab)")]
    // Default: the Africa/Europe-area public pool. For the tightest agreement with
    // the other lab machines, point this at whatever they sync to (the same pool),
    // or at a local NTP server on the OptiTrack PC / file server. An IP literal
    // (no DNS) also works if you want zero name resolution at experiment time.
    public string ntpServerHost = "ae.pool.ntp.org";
    public int ntpServerPort = 123;

    [Header("Sampling")]
    public int sampleCount = 2;            // samples to take, keep the min-delay one
    public int perRequestTimeoutMs = 750;  // per-request timeout
    public int saneMaxDelayMs = 3000;       // ignore a sample slower than this (suspect)
    public int resyncPeriodSeconds = 0;    // 0 = one-shot at start / on demand (the norm here)

    [Header("Diagnostics")]
    public bool verboseDiagnostics = false; // per-sample Debug.Log lines; the outcome is always a LogWarning

    // ------------------------------------------------------------------
    // Public surface — call from anywhere, including otherMonoBehaviours.
    // ------------------------------------------------------------------
    public static NtpTimeSync Instance { get; private set; }

    /// True after at least one adequate NTP sample has been applied.
    public static bool IsSynced => Instance != null && Instance._hasGoodOffset;

    /// server = local + OffsetMs  (a positive offset means the reference is ahead of the headset).
    public static long OffsetMs => Instance != null ? Instance._offsetMs : 0L;

    /// Best observed NTP round-trip delay, ms (the sample that supplied OffsetMs).
    public static long LastDelayMs => Instance != null ? Instance._lastDelayMs : long.MaxValue;

    /// Corrected "now", Unix ms = local now + offset.
    /// Drop-in for DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(). Millisecond precision.
    public static long UnixTimeNowInMs()
    {
        long local = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return local + (IsSynced ? OffsetMs : 0L);
    }

    /// String convenience (same value as UnixTimeNowInMs, formatted).
    public static string UnixTimeNowInMsString() => UnixTimeNowInMs().ToString(CultureInfo.CurrentCulture);

    /// Re-run the NTP pass on demand (after a long sleep, before a block of trials, …).
    /// Safe to call repeatedly; overlapping calls are coalesced into a single pass.
    public static void RequestResync() => Instance?.RunPass();

    // ------------------------------------------------------------------
    // Internal state.
    // ------------------------------------------------------------------
    private readonly object _stateLock = new object();
    private long _offsetMs = 0L;                 // written under lock; read as a long (atomic on 64-bit)
    private long _lastDelayMs = long.MaxValue;
    private bool _hasGoodOffset = false;
    private bool _passRunning = false;
    private long _lastPassUtcMs = 0L;

    // 1970-01-01 expressed in NTP-epoch (1900-01-01) seconds.
    private const long NTP_UNIX_SECONDS_DIFF = 2208988800L;

    void Awake()
    {
        Instance = this;
        // Pin culture, exactly like the rest of the lab scripts (decimal formatting).
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");

        Diag("NtpTimeSync: starting — server = " + ntpServerHost + ":" + ntpServerPort
             + ", samples = " + sampleCount + ", timeout = " + perRequestTimeoutMs + " ms.");
        RunPass();   // one-shot by default; resyncPeriodSeconds>0 turns it into a watcher
    }

    void Update()
    {
        if (resyncPeriodSeconds <= 0) return;
        long nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (nowUtcMs - _lastPassUtcMs >= (long)resyncPeriodSeconds * 1000L)
        {
            _lastPassUtcMs = nowUtcMs;
            RunPass();
        }
    }

    void OnDisable() { if (Instance == this) Instance = null; }

    // Fire-and-forget: NTP round trips off-thread, writes the offset under lock.
    // Never blocks the render frame; the async work continues on the sync context.
    void RunPass()
    {
        lock (_stateLock)
        {
            if (_passRunning) return;             // coalesce concurrent requests
            _passRunning = true;
        }
        _ = RunPassAsync();
    }

    async Task RunPassAsync()
    {
        try
        {
            long bestOffsetMs = long.MinValue;
            long bestDelayMs = long.MaxValue;
            int good = 0;

            for (int i = 0; i < Math.Max(1, sampleCount); i++)
            {
                NtpSample s = await TryOneSample();
                if (s.valid)
                {
                    good++;
                    // Keep the sample with the smallest round-trip delay
                    // (the classic NTP "closest sample" rule — removes the asymmetric-wire bias).
                    if (s.delayMs < bestDelayMs)
                    {
                        bestDelayMs = s.delayMs;
                        bestOffsetMs = s.offsetMs;
                    }
                    LogSample(i, s.offsetMs, s.delayMs);
                }
                else
                {
                    LogSample(i, long.MinValue, long.MaxValue);
                }
            }

            bool apply = good > 0 && bestDelayMs <= saneMaxDelayMs;
            lock (_stateLock)
            {
                if (apply)
                {
                    _offsetMs = bestOffsetMs;
                    _lastDelayMs = bestDelayMs;
                    _hasGoodOffset = true;
                }
                _passRunning = false;
            }
            _lastPassUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // LogWarning on purpose: the lab's MQDH hides Info, and we verify by marker.
            if (apply)
                Debug.LogWarning($"[NtpTimeSync] offset applied: {_offsetMs} ms vs {ntpServerHost} "
                                 + $"(round-trip {bestDelayMs} ms). IsSynced is now true.");
            else
                Debug.LogWarning($"[NtpTimeSync] no adequate sample (best delay "
                                 + (good > 0 ? bestDelayMs.ToString() : "n/a")
                                 + " ms >= sane ceiling " + saneMaxDelayMs
                                 + " ms). Timestamps fall back to UNCORRECTED system clock.");
        }
        catch (Exception e)
        {
            Debug.LogError("[NtpTimeSync] NTP pass crashed: " + e);
            lock (_stateLock) { _passRunning = false; }
        }
    }

    // Result of one NTP round trip. (An async method cannot take out params, so the
    // sample is returned as a value; valid=false means this sample failed.)
    struct NtpSample
    {
        public bool valid;
        public long offsetMs;
        public long delayMs;
        public static NtpSample Invalid() => new NtpSample { valid = false, offsetMs = long.MinValue, delayMs = long.MaxValue };
    }

    // One NTP client round trip -> an NtpSample (valid=false on any failure).
    async Task<NtpSample> TryOneSample()
    {
        IPAddress serverIp;
        try
        {
            serverIp = PickIPv4(await System.Net.Dns.GetHostAddressesAsync(ntpServerHost));
        }
        catch (Exception e)
        {
            Diag("DNS failed for " + ntpServerHost + ": " + e.Message);
            return NtpSample.Invalid();
        }
        if (serverIp == null) { Diag("no IPv4 address for " + ntpServerHost); return NtpSample.Invalid(); }

        var ep = new IPEndPoint(serverIp, ntpServerPort);
        byte[] pkt = BuildRequest();

        using (var client = new UdpClient(AddressFamily.InterNetwork))
        {
            long t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();   // origin timestamp
            await client.SendAsync(pkt, pkt.Length, ep);

            var recvTask = client.ReceiveAsync();
            var timeoutTask = Task.Delay(perRequestTimeoutMs);
            var completed = await Task.WhenAny(recvTask, timeoutTask);

            if (completed != recvTask)
            {
                ObserveTask(recvTask);   // don't leave a pending UDP receive unobserved after we drop it
                Diag("request timed out after " + perRequestTimeoutMs + " ms");
                return NtpSample.Invalid();
            }

            long t3 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();   // destination timestamp
            UdpReceiveResult r;
            try { r = await recvTask; }
            catch (Exception e) { Diag("receive failed: " + e.Message); return NtpSample.Invalid(); }

            byte[] b = r.Buffer;
            if (b == null || b.Length < 48) { Diag("short NTP reply (" + (b == null ? 0 : b.Length) + " bytes)"); return NtpSample.Invalid(); }

            int mode = b[0] & 0x07;
            if (mode != 4 && mode != 5) { Diag("not a server reply (mode " + mode + ")"); return NtpSample.Invalid(); }

            long t1 = NtpFieldToUnixMs(b, 32);   // receive  timestamp (server)
            long t2 = NtpFieldToUnixMs(b, 40);   // transmit  timestamp (server)
            if (t1 == long.MinValue || t2 == long.MinValue) { Diag("server timestamps missing / leap"); return NtpSample.Invalid(); }

            // Symmetric-latency model (using both server timestamps, so it does not
            // depend on which single one a given server fills in):
            //   offset = server_midpoint - client_midpoint = ((t1 - t0) + (t2 - t3)) / 2
            //   delay  = (t3 - t0) - (t2 - t1)
            long offset = ((t1 - t0) + (t2 - t3)) / 2;
            long delay = (t3 - t0) - (t2 - t1);
            if (delay < 0) { Diag("negative delay — out of range sample"); return NtpSample.Invalid(); }

            return new NtpSample { valid = true, offsetMs = offset, delayMs = Math.Max(0, delay) };
        }
    }

    // NTP 64-bit timestamp (32 s + 32 fraction, seconds since 1900) -> Unix ms.
    // Returns long.MinValue if not usable (zero / leap / malformed).
    static long NtpFieldToUnixMs(byte[] p, int idx)
    {
        if (p == null || p.Length < idx + 8) return long.MinValue;
        uint sec = (uint)(p[idx] << 24 | p[idx + 1] << 16 | p[idx + 2] << 8 | p[idx + 3]);
        uint frac = (uint)(p[idx + 4] << 24 | p[idx + 5] << 16 | p[idx + 6] << 8 | p[idx + 7]);
        if (sec == 0U) return long.MinValue;   // zero / leap / not set by this server
        long secondsSinceUnix = (long)sec - NTP_UNIX_SECONDS_DIFF;
        long fractionMs = (long)((frac * 1000UL) >> 32);
        return secondsSinceUnix * 1000L + fractionMs;
    }

    static IPAddress PickIPv4(IPAddress[] ips)
    {
        if (ips == null) return null;
        return Array.Find(ips, x => x.AddressFamily == AddressFamily.InterNetwork);
    }

    static byte[] BuildRequest()
    {
        byte[] b = new byte[48];
        b[0] = 0x1B;   // LI=00, version=111 (3), mode=011 (client)
        return b;
    }

    // Mark a possibly-pending Task as observed so a fault does not go unobserved.
    static void ObserveTask(Task t)
    {
        try
        {
            t?.ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch { /* already swallowed */ }
    }

    void Diag(string s) { if (verboseDiagnostics) Debug.Log("[NtpTimeSync] " + s); }

    void LogSample(int i, long offsetMs, long delayMs)
    {
        if (!verboseDiagnostics) return;
        if (offsetMs == long.MinValue) Debug.Log($"[NtpTimeSync] sample {i}: failed / timeout");
        else                           Debug.Log($"[NtpTimeSync] sample {i}: offset={offsetMs} ms  delay={delayMs} ms");
    }
}
