// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WorldCache = BetterContinents.BetterContinents.ZNetPatch.WorldCache;

namespace BetterContinents;

// The client's cache of baked layers (build spec 5.4): <save data>/BetterContinents/cache/<id>.bcl, beside the settings packages (.bc), in
// the folder ZNetPatch.WorldCache owns. A layer's id is the SHA-512 prefix of its bytes, as a settings package's, so the two kinds of id
// never collide and the handshake's cache list holds both (WorldCache.GetCacheList adds Ids()).
//
//  * A file is written to <id>.bcl.tmp and renamed, as WorldCache.Add does, so a crash never leaves half a layer under its id.
//  * Each use touches the file; at the first handshake of a session, files not used for 30 days are deleted (Prune).
//  * A client that stores a new revision of the layer during a session (a push) deletes the layer file of the revision before it (Replaced).
internal static class BakedCache
{
  internal const string Extension = ".bcl";

  /// <summary>Layer files not used for this long are deleted at the first handshake of a session.</summary>
  internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

  private static string Folder => WorldCache.WorldCachePath;

  internal static string PathOf(string id) => Path.Combine(Folder, id + Extension);

  /// <summary>The id a layer's bytes have (the first <paramref name="length"/> of them).</summary>
  internal static string IdOf(byte[] data, int length) => WorldCache.PackageID(data, length);

  /// <summary>The ids of the layers in the cache, as the handshake lists them: lower case, as the settings packages' are.</summary>
  internal static List<string> Ids() =>
    Directory.Exists(Folder)
      ? Directory.GetFiles(Folder, "*" + Extension).Where(f => Path.GetExtension(f) == Extension).Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant()).ToList()
      : [];

  internal static bool Exists(string id) => File.Exists(PathOf(id));

  /// <summary>Stores a layer under its id (the file may be there already, with the same bytes: it is replaced).</summary>
  internal static void Add(string id, byte[] data, int length)
  {
    var path = PathOf(id);
    Directory.CreateDirectory(Path.GetDirectoryName(path));
    using (var file = File.Create(path + ".tmp"))
      file.Write(data, 0, length);
    if (File.Exists(path))
      File.Delete(path);
    File.Move(path + ".tmp", path);
  }

  /// <summary>The bytes of a stored layer, and its use noted.</summary>
  internal static byte[] Load(string id)
  {
    var path = PathOf(id);
    var bytes = File.ReadAllBytes(path);
    Touch(id);
    return bytes;
  }

  internal static void Touch(string id)
  {
    try
    {
      File.SetLastWriteTimeUtc(PathOf(id), DateTime.UtcNow);
    }
    catch (Exception e)
    {
      BetterContinents.Log($"Could not note the use of the cached baked pieces {id}: {e.Message}");
    }
  }

  internal static void Delete(string id)
  {
    try
    {
      File.Delete(PathOf(id));
    }
    catch (Exception e)
    {
      BetterContinents.Log($"Could not delete the cached baked pieces {id}: {e.Message}");
    }
  }

  // The layer file this session stored or loaded last: the one the next revision replaces. A new connection starts with none.
  private static string? sessionId;

  internal static void SessionStarts() => sessionId = null;

  /// <summary>The session's layer is now <paramref name="id"/>: the file of the one before it is not needed any more.</summary>
  internal static void Replaced(string id)
  {
    var before = sessionId;
    sessionId = id;
    if (before != null && before != id)
      Delete(before);
  }

  private static bool pruned;

  /// <summary>Once a session (its first handshake): the layer files nobody has used for 30 days go. Returns how many.</summary>
  internal static int PruneOnce(DateTime? now = null)
  {
    if (pruned)
      return 0;
    pruned = true;
    return Prune(now ?? DateTime.UtcNow);
  }

  internal static int Prune(DateTime now)
  {
    int deleted = 0;
    try
    {
      if (!Directory.Exists(Folder))
        return 0;
      foreach (var file in Directory.GetFiles(Folder, "*" + Extension))
      {
        if (Path.GetExtension(file) != Extension || now - File.GetLastWriteTimeUtc(file) < MaxAge)
          continue;
        File.Delete(file);
        deleted++;
      }
      if (deleted > 0)
        BetterContinents.Log($"Deleted {deleted} cached baked layer(s) not used for {MaxAge.Days} days.");
    }
    catch (Exception e)
    {
      BetterContinents.Log($"Could not prune the cached baked layers: {e.Message}");
    }
    return deleted;
  }

  /// <summary>For the offline tests: a session that has not pruned yet.</summary>
  internal static void ForgetPruned() => pruned = false;
}
