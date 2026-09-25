// Added by Wubarrk on 2026-09-24 for world export and import (0.9.0).

namespace BetterContinents;

/// <summary>"Settings Transfer Rate" (07 BetterContinents.Misc, config key "Settings Transfer Rate"): how fast the
/// machine that runs the world (the host, or a dedicated server) sends a joining player its Better Continents
/// settings and images over their Steam connection. Valheim pins every Steam connection to 153600 bytes/s
/// (ZSteamSocket.RegisterGlobalCallbacks sets SendRateMin and SendRateMax to it); BetterContinents.ZNetPatch.SendSettings
/// sets both to the preset's rate on that one connection for the transfer and restores them afterwards. Read only on
/// the sending side: a client's own value has no effect, and a PlayFab (crossplay) connection cannot be changed at all.
/// GameNetworkingSockets has no adaptive bandwidth (its header: the two values "should always be set to the same
/// value"), so a preset is a fixed rate, not a ceiling, and Unlimited is simply the highest one. The transfer loop
/// itself moves about 4 MB/s at most (two 128 KiB chunks in flight per server frame; 3.85 MB/s measured on loopback,
/// 2026-09-25), so anything above MB3 is only "as fast as it goes".</summary>
public enum TransferRatePreset
{
  Vanilla,
  KB256,
  KB384,
  KB512,
  KB768,
  MB1,
  MB1_5,
  MB3,
  Unlimited,
}

/// <summary>The preset -> bytes/second mapping, kept free of ZNet and Steam so it is unit-testable
/// (tools/livecfg-tests) without a connection.</summary>
public static class TransferRate
{
  public const TransferRatePreset Default = TransferRatePreset.KB512;

  /// <summary>Steam's send rate for a preset, in bytes/second: null means "do not touch the connection" (Vanilla:
  /// Valheim's own 150 KB/s keeps applying); otherwise the fixed rate set on the connection for the transfer.
  /// Unlimited is NOT the literal value 0: the GameNetworkingSockets header does not document 0 as meaning "no limit"
  /// (only passing a NULL pointer, never done here, clears a per-connection override back to the parent scope's
  /// default), so it is an explicit 100 MB/s. An unrecognised preset (for instance a value saved by a later version
  /// that added a new one) falls back to Default rather than throwing.</summary>
  public static int? BytesPerSecond(TransferRatePreset preset) => preset switch
  {
    TransferRatePreset.Vanilla => null,
    TransferRatePreset.KB256 => 256 * 1024,
    TransferRatePreset.KB384 => 384 * 1024,
    TransferRatePreset.KB512 => 512 * 1024,
    TransferRatePreset.KB768 => 768 * 1024,
    TransferRatePreset.MB1 => 1024 * 1024,
    TransferRatePreset.MB1_5 => 1536 * 1024,
    TransferRatePreset.MB3 => 3072 * 1024,
    TransferRatePreset.Unlimited => UnlimitedBytesPerSecond,
    _ => BytesPerSecond(Default),
  };

  /// <summary>The "Unlimited" preset: 100 MB/s, the user's choice (2026-09-25). Steam sends at exactly this rate with
  /// no congestion control, so it belongs on a server whose upload can take the bursts; the transfer loop caps the
  /// real throughput at about 4 MB/s anyway.</summary>
  public const int UnlimitedBytesPerSecond = 100 * 1024 * 1024;

  /// <summary>Text for the transfer-start log line ("Settings transfer rate: 512 KB/s (server setting)").</summary>
  public static string Describe(TransferRatePreset preset) => preset switch
  {
    TransferRatePreset.Vanilla => "vanilla",
    TransferRatePreset.KB256 => "256 KB/s",
    TransferRatePreset.KB384 => "384 KB/s",
    TransferRatePreset.KB512 => "512 KB/s",
    TransferRatePreset.KB768 => "768 KB/s",
    TransferRatePreset.MB1 => "1 MB/s",
    TransferRatePreset.MB1_5 => "1.5 MB/s",
    TransferRatePreset.MB3 => "3 MB/s",
    TransferRatePreset.Unlimited => "unlimited (100 MB/s)",
    _ => Describe(Default),
  };
}
