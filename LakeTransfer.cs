// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5).

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace BetterContinents;

// A change of the world's lakes during play, to the players in it (BUILD-SPEC-TARN.md 2.5). At join nothing is new: the lake blob and the mask's are in the settings
// package, which a joining player has read before PeerInfo, and caches by its id. A change (bc_lake here, remove, depth, load, import) is made on the machine that runs
// the world, which swaps its Settings.Lakes and sends the new blob 72 to every client whose join is over, in one RPC that follows BetterContinentsSettingsUpdate: registered
// beside it when a client connects, sent through the same loop over BakedTransfer.Clients that skips clients still joining (they get the new set in their package).
//
//   BetterContinentsLakes  server to client: (ZPackage: int revision; int rawLength; byte[] deflated blob 72)
//
// The mask is not pushed: only the machine that runs the world places vegetation, and a client's copy is for its exports (a late joiner gets the new one in its package).
// A late joiner after a change gets a settings package with a new id, so a client that had the old one cached downloads it whole (decided: the lake edit is an author's act).
internal static class LakeTransfer
{
  internal const string RpcLakes = "BetterContinentsLakes";

  // The most a pushed blob may inflate to (5,000 lakes are about 450 KB): a package that says more is refused before it is opened.
  internal const int MaxRawLength = 1 << 20;

  // Called on this machine after Settings.Lakes was replaced (by a command, or by a push that arrived): the lake engine's toggles and map follow. The lake engine (agent B)
  // sets it once, when it starts; the clearing mask's toggle is switched by whoever replaces the mask (Patcher.PatchClearingToggles).
  internal static Action<LakeSet?>? LakesChanged;

  // The sent package: the set's blob, deflated, with the revision (so that both sides can say which set they speak of) and the blob's length.
  internal static ZPackage Pack(LakeSet set)
  {
    var raw = set.ToBlock();
    byte[] packed;
    using (var ms = new MemoryStream(raw.Length / 2 + 64))
    {
      using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, true))
        deflate.Write(raw, 0, raw.Length);
      packed = ms.ToArray();
    }
    var pkg = new ZPackage();
    pkg.Write(set.Revision);
    pkg.Write(raw.Length);
    pkg.Write(packed);
    return pkg;
  }

  // The set a package holds. Throws LakeFormatException when it does not read; never opens more than MaxRawLength bytes.
  internal static LakeSet Unpack(ZPackage pkg)
  {
    int revision, rawLength;
    byte[] packed;
    try
    {
      revision = pkg.ReadInt();
      rawLength = pkg.ReadInt();
      packed = pkg.ReadByteArray();
    }
    catch (Exception e) when (e is EndOfStreamException or IOException or ArgumentException or OverflowException)
    {
      throw new LakeFormatException("the pushed lakes are cut short");
    }
    if (rawLength < 12 || rawLength > MaxRawLength)
      throw new LakeFormatException($"the pushed lakes say {rawLength} bytes");
    var raw = new byte[rawLength];
    try
    {
      using var inflate = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
      int got = 0;
      while (got < raw.Length)
      {
        int n = inflate.Read(raw, got, raw.Length - got);
        if (n <= 0)
          throw new LakeFormatException($"the pushed lakes end after {got} of {rawLength} bytes");
        got += n;
      }
      if (inflate.ReadByte() >= 0)
        throw new LakeFormatException($"the pushed lakes go on past {rawLength} bytes");
    }
    catch (InvalidDataException e)
    {
      throw new LakeFormatException("the pushed lakes do not open (" + e.Message + ")");
    }
    var read = LakeSet.FromBlock(raw);
    return new LakeSet(read.Lakes, read.DepthScale, revision);
  }

  // The machine that runs the world: the set, to every client whose join is over. Returns how many it went to.
  internal static int Broadcast(LakeSet set)
  {
    var pkg = Pack(set);
    int sent = 0;
    foreach (var client in BakedTransfer.Clients.ToList())
      if (client.JoinDone && client.Link.Connected)
      {
        client.Link.Invoke(RpcLakes, pkg);
        sent++;
      }
    if (sent > 0)
      BetterContinents.Log($"Lakes: revision {set.Revision} ({set.Lakes.Count:N0} lakes, depth scale {set.DepthScale:0.##}) sent to {sent} player{(sent == 1 ? "" : "s")}");
    return sent;
  }

  // A client: registered beside BetterContinentsSettingsUpdate when the connection is made.
  internal static void RegisterClient(ZRpc rpc) => rpc.Register(RpcLakes, (ZRpc _, ZPackage pkg) => Receive(pkg));

  // A client takes the server's new lakes: Settings.Lakes is swapped, and the lake engine follows (LakesChanged). A package that does not read leaves the client's lakes as
  // they are, with a line in the log. A client whose world is not a Better Continents world (its settings are the game's) has no use for them.
  internal static bool Receive(ZPackage pkg)
  {
    try
    {
      var set = Unpack(pkg);
      var settings = BetterContinents.Settings;
      if (!settings.EnabledForThisWorld)
      {
        BetterContinents.Log("Lakes: the server sent its lakes, but this world has no Better Continents settings here: left out");
        return false;
      }
      settings.ReplaceLakes(set);
      BetterContinents.Log($"Lakes: the server's lakes, revision {set.Revision}: {set.Lakes.Count:N0} lakes, depth scale {set.DepthScale:0.##}");
      LakesChanged?.Invoke(set);
      return true;
    }
    catch (LakeFormatException e)
    {
      BetterContinents.LogError("Lakes: the server's lakes could not be read, so this client keeps the ones it has: " + e.Message);
      return false;
    }
  }
}
