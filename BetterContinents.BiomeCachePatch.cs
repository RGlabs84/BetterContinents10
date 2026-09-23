// Added by Wubarrk on 2026-09-22 for alt-biome planting (0.8.1).

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace BetterContinents;

public partial class BetterContinents
{
  // Valheim 1.0.7 and 1.0.12 cached the biome point grid of a world in <save data>/cache/<world name>_biomedatacache.bin,
  // keyed on the world NAME and checked against the world version only (VerifyBiomeData -> TryLoadCache / SaveCache).
  // Such a cache survives anything that changes what the grid should contain without renaming the world: a Better
  // Continents settings file replaced, maps edited, a world copied in from another machine, a Better Continents
  // update. A stale grid means wrong terrain biomes, wrong alt biomes, and locations placed from another map's points.
  //
  // Valheim 1.0.15 (client and dedicated server, read from their IL) no longer uses the cache at all: VerifyBiomeData
  // is RemoveCache + GenerateBiomePoints + GenerateSectors, and nothing calls TryLoadCache or SaveCache. On 1.0.15 these
  // two patches therefore never run. They stay as a guard for any game version that reads the cache again.
  //
  // Better Continents worlds get a fingerprint appended to the cache file - SHA-256 of the settings that shape the
  // grid, the seed, the grid geometry, and the Better Continents and game versions - and a cache whose fingerprint
  // does not match is not loaded, so vanilla regenerates and rewrites it. The fingerprint is a trailer after
  // vanilla's payload, which vanilla's reader never reaches, so the file stays readable by the game without the mod.
  // Being inside the file, it can never outlive or be separated from the data it describes. The alt-biome settings
  // and map are left out on purpose: they change sectors, never the grid.
  internal static class BiomeCacheFingerprint
  {
    public const int TrailerVersion = 1;
    public const int HashLength = 32;
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("BCFP");
    public static readonly int TrailerLength = HashLength + 4 + Magic.Length;

    // SaveCache writes int 0, int world version, int size, then size*size points of (float height, byte biome).
    public static long VanillaLength(int size) => 12L + 5L * size * size;

    public static byte[] Compute(World world)
    {
      var pkg = new ZPackage();
      Settings.Serialize(pkg, network: true, includeAltBiomes: false);
      var header = Encoding.UTF8.GetBytes(
        $"BetterContinents biome cache|{ModInfo.Version}|{Version.CurrentVersion}|{world.m_seed}|{world.m_worldGenVersion}|{AltBiomeControl.GridProbe()}|");
      var body = pkg.GetArray();
      using var sha = SHA256.Create();
      sha.TransformBlock(header, 0, header.Length, null, 0);
      sha.TransformFinalBlock(body, 0, body.Length);
      return sha.Hash;
    }

    public static bool Matches(string path, byte[] expected, out string reason)
    {
      using var fs = File.OpenRead(path);
      using var br = new BinaryReader(fs);
      if (fs.Length < 12)
      {
        reason = "is truncated";
        return false;
      }
      br.ReadInt32();
      br.ReadInt32();
      int size = br.ReadInt32();
      if (size <= 0 || size > 16384)
      {
        reason = $"declares an impossible grid size {size}";
        return false;
      }
      long vanilla = VanillaLength(size);
      if (fs.Length == vanilla)
      {
        reason = "has no Better Continents fingerprint (written without Better Continents, or before 0.8.1)";
        return false;
      }
      if (fs.Length != vanilla + TrailerLength)
      {
        reason = $"has an unexpected length ({fs.Length} bytes)";
        return false;
      }
      fs.Position = vanilla;
      var hash = br.ReadBytes(HashLength);
      var version = br.ReadInt32();
      var magic = br.ReadBytes(Magic.Length);
      if (!magic.SequenceEqual(Magic) || version != TrailerVersion)
      {
        reason = "has an unknown fingerprint format";
        return false;
      }
      if (!hash.SequenceEqual(expected))
      {
        reason = "was built for different Better Continents settings, seed, grid or version (fingerprint mismatch)";
        return false;
      }
      reason = "";
      return true;
    }

    // True when the file ends in a Better Continents fingerprint of any kind.
    public static bool HasTrailer(string path)
    {
      using var fs = File.OpenRead(path);
      if (fs.Length < TrailerLength + 12)
        return false;
      fs.Position = fs.Length - Magic.Length;
      var magic = new byte[Magic.Length];
      return fs.Read(magic, 0, magic.Length) == magic.Length && magic.SequenceEqual(Magic);
    }

    // Appends the trailer to a freshly written vanilla cache file. Never stacks a second trailer.
    public static bool Append(string path, int size, byte[] fingerprint)
    {
      var info = new FileInfo(path);
      if (!info.Exists)
        return false;
      if (info.Length != VanillaLength(size))
      {
        LogWarning($"Biome cache {path} is {info.Length} bytes, not the {VanillaLength(size)} expected; not fingerprinting it (it will be rebuilt next load).");
        return false;
      }
      using var fs = new FileStream(path, FileMode.Append, FileAccess.Write);
      using var bw = new BinaryWriter(fs);
      bw.Write(fingerprint);
      bw.Write(TrailerVersion);
      bw.Write(Magic);
      return true;
    }

    public static string Short(byte[] hash) => string.Concat(hash.Take(6).Select(b => b.ToString("x2")));
  }

  [HarmonyPatch(typeof(AltBiomeWorldData))]
  private class BiomeCachePatch
  {
    // public static bool TryLoadCache(World world) - AltBiomeWorldData.cs:504. Called only from VerifyBiomeData
    // (:74), which regenerates the grid whenever world.m_biomeData is still null afterwards (:75).
    [HarmonyPrefix, HarmonyPatch(nameof(AltBiomeWorldData.TryLoadCache))]
    private static bool TryLoadCachePrefix(World world, ref bool __result)
    {
      if (world == null)
        return true;
      try
      {
        if (!FileHelpers.LocalStorageSupportedAndAllowed)
          return true;
        var path = AltBiomeWorldData.GetFilePath(world);
        if (!File.Exists(path))
        {
          if (Settings.EnabledForThisWorld)
            Log($"Alt biomes: no biome data cache for '{world.m_name}' yet; generating the grid.");
          return true;
        }
        if (!Settings.EnabledForThisWorld)
        {
          // A world without Better Continents behaves exactly as vanilla - unless the grid in the cache was built by
          // Better Continents for another world of the same name, which vanilla would happily reuse.
          return BiomeCacheFingerprint.HasTrailer(path)
            ? Reject(world, ref __result, $"Alt biomes: the biome data cache {path} was built for a Better Continents world and this world does not use Better Continents; rebuilding it.")
            : true;
        }
        // VerifyBiomeData regenerates for any other world version even after a successful load. That is every
        // remote client (its World is built from wire fields and never gets m_worldVersion) and a world's first
        // session, so do not read 20 MB just to throw it away.
        if (world.m_worldVersion != Version.World.DeepNorth)
          return Reject(world, ref __result, null);
        var expected = BiomeCacheFingerprint.Compute(world);
        if (BiomeCacheFingerprint.Matches(path, expected, out var reason))
        {
          Log($"Alt biomes: the biome data cache for '{world.m_name}' matches this world (fingerprint {BiomeCacheFingerprint.Short(expected)}); reusing it.");
          return true;
        }
        return Reject(world, ref __result, $"Alt biomes: the biome data cache for '{world.m_name}' {reason}; rebuilding it.");
      }
      catch (Exception ex)
      {
        return Reject(world, ref __result, $"Alt biomes: could not check the biome data cache ({ex.Message}); rebuilding it.");
      }
    }

    private static bool Reject(World world, ref bool result, string? message)
    {
      if (message != null)
        Log(message);
      // VerifyBiomeData only regenerates when m_biomeData is null, and a World object reused for a second session
      // in the same game still holds the first session's data.
      world.m_biomeData = null;
      result = false;
      return false;
    }

    // public void SaveCache() - AltBiomeWorldData.cs:488. Runs right after GenerateBiomePoints in VerifyBiomeData,
    // and after a debug-mode rebuild of the point grid.
    [HarmonyPostfix, HarmonyPatch(nameof(AltBiomeWorldData.SaveCache))]
    private static void SaveCachePostfix(AltBiomeWorldData __instance)
    {
      if (!Settings.EnabledForThisWorld || !FileHelpers.LocalStorageSupportedAndAllowed)
        return;
      var world = __instance.m_world;
      if (world == null)
        return;
      try
      {
        var fingerprint = BiomeCacheFingerprint.Compute(world);
        if (BiomeCacheFingerprint.Append(AltBiomeWorldData.GetFilePath(world), __instance.Size, fingerprint))
          Log($"Alt biomes: wrote the biome data cache for '{world.m_name}' with fingerprint {BiomeCacheFingerprint.Short(fingerprint)}.");
      }
      catch (Exception ex)
      {
        LogWarning($"Alt biomes: could not fingerprint the biome data cache: {ex.Message}");
      }
    }
  }
}
