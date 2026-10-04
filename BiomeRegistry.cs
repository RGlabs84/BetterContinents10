// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BetterContinents;

// What Better Continents knows about biomes, in one place: which ones the game can index (the vanilla nine, and with
// Expand World Data the biomes its expand_biomes yaml adds), which ones the world can use now, their names in a
// legend (Expand World Data's included) and how a world's settings store them. The biome map (ImageMapBiome) and
// everything else that indexes, names or stores a biome asks here. Moved here unchanged from ImageMapBiome.
public static class BiomeRegistry
{
    // Valheim 1.0 converts biomes to indices and back (BiomeHelpers.ToBiomeIndex / ToBiome) with switches that throw
    // for anything that is not one of the ten known values, so combined or unused values must never reach the game.
    // Expand World Data patches both conversions to cover every single flag bit, and gives the biomes it adds (its
    // expand_biomes yaml) the bits after Mistlands: 0x400 upward, then 0x80. Their indices run past
    // BiomeIndex.Count, so anything indexed by biome is sized by IndexCount, not by BiomeIndex.Count.
    // RefreshTable reads what the game accepts from those two conversions themselves.
    private sealed class BiomeTable(Heightmap.Biome[] indexToBiome, Heightmap.Biome supported)
    {
        // Index -> biome, None where no biome has that index.
        public readonly Heightmap.Biome[] IndexToBiome = indexToBiome;
        // Every single-bit biome the game can index.
        public readonly Heightmap.Biome Supported = supported;
    }
    private static readonly Heightmap.Biome VanillaBiomes = Heightmap.Biome.Meadows | Heightmap.Biome.Swamp | Heightmap.Biome.Mountain
        | Heightmap.Biome.BlackForest | Heightmap.Biome.Plains | Heightmap.Biome.AshLands | Heightmap.Biome.DeepNorth
        | Heightmap.Biome.Ocean | Heightmap.Biome.Mistlands;
    // One immutable table, swapped whole: the terrain builder thread reads it while the main thread refreshes it.
    private static volatile BiomeTable Table = new(
        [.. Enumerable.Range(0, (int)Heightmap.BiomeIndex.Count).Select(i => ((Heightmap.BiomeIndex)i).ToBiome())], VanillaBiomes);

    internal static bool IsSingleBit(Heightmap.Biome biome)
    {
        var bits = (uint)biome;
        return bits != 0 && (bits & (bits - 1)) == 0;
    }
    public static bool IsVanilla(Heightmap.Biome biome) => biome == Heightmap.Biome.None || (IsSingleBit(biome) && (VanillaBiomes & biome) != 0);
    // A biome the game can index (None or one flag bit the conversions accept); IsUsable says whether the world
    // can use it now.
    public static bool IsValid(Heightmap.Biome biome) => biome == Heightmap.Biome.None || (IsSingleBit(biome) && (Table.Supported & biome) != 0);
    // Same as the game's ToIndex extension, but without throwing on unknown values.
    public static int ToSafeIndex(Heightmap.Biome biome) => IsValid(biome) ? biome.ToIndex() : 0;
    // The size of anything indexed by ToSafeIndex: BiomeIndex.Count in vanilla, 33 with Expand World Data.
    public static int IndexCount => Table.IndexToBiome.Length;
    // The biome of an index below IndexCount; None for any other index.
    public static Heightmap.Biome ToSafeBiome(int index)
    {
        var table = Table.IndexToBiome;
        return index >= 0 && index < table.Length ? table[index] : Heightmap.Biome.None;
    }
    // Biomes beyond vanilla the game can index (Expand World Data's), None without them.
    public static Heightmap.Biome Extra => Table.Supported & ~VanillaBiomes;

    // Biomes the world can use now. Indexing is not enough: the game's alt-biome grid (AltBiomeWorldData) gets a
    // BiomeTypeInfo only for the values Enum.GetValues gives, which with Expand World Data are the biomes its yaml
    // names at that moment, and any other value in the grid throws KeyNotFoundException, so the world never loads. A
    // map keeps whatever it holds, so nothing is lost while Expand World Data is missing or its yaml changes;
    // GetValue reads the rest as None, where the default generation decides. Refreshed at Start and before every
    // VerifyBiomeData, which builds that grid (world load, and Expand World Data's regeneration after a yaml change).
    internal static volatile Heightmap.Biome Usable = VanillaBiomes;
    public static bool IsUsable(Heightmap.Biome biome) => biome == Heightmap.Biome.None || (IsSingleBit(biome) && (Usable & biome) != 0);
    internal static void RefreshUsable()
    {
        var usable = VanillaBiomes;
        try
        {
            foreach (var value in Enum.GetValues(typeof(Heightmap.Biome)))
                if (value is Heightmap.Biome biome && IsSingleBit(biome) && IsValid(biome))
                    usable |= biome;
        }
        catch (Exception e)
        {
            BetterContinents.LogWarning($"Cannot list the game's biomes ({e.Message}); biome maps use the vanilla biomes only.");
        }
        Usable = usable;
    }
    // At Start, once every mod has applied its patches in Awake: asks the game's own conversions which further flag
    // bits have an index that converts back to the same biome. Vanilla throws for each; Expand World Data answers for
    // all of them.
    internal static void RefreshTable()
    {
        var byIndex = new Dictionary<int, Heightmap.Biome>();
        for (int i = 0; i < (int)Heightmap.BiomeIndex.Count; i++)
            byIndex[i] = ((Heightmap.BiomeIndex)i).ToBiome();
        var supported = VanillaBiomes;
        for (int bit = 0; bit < 32; bit++)
        {
            var biome = (Heightmap.Biome)(int)(1u << bit);
            if ((VanillaBiomes & biome) != 0)
                continue;
            int index;
            try
            {
                index = (int)biome.ToBiomeIndex();
                if (index <= 0 || byIndex.ContainsKey(index) || ((Heightmap.BiomeIndex)index).ToBiome() != biome)
                    continue;
            }
            catch (Exception)
            {
                continue;
            }
            byIndex[index] = biome;
            supported |= biome;
        }
        var indexToBiome = new Heightmap.Biome[byIndex.Keys.Max() + 1];
        foreach (var kv in byIndex)
            indexToBiome[kv.Key] = kv.Value;
        Table = new BiomeTable(indexToBiome, supported);
        BetterContinents.RefreshPlainSectors();
        RefreshUsable();
        if (Extra != Heightmap.Biome.None)
            BetterContinents.Log($"Biome maps accept {byIndex.Count - (int)Heightmap.BiomeIndex.Count} biomes beyond vanilla (Expand World Data's), indices up to {indexToBiome.Length - 1}.");
    }

    // Names in a biome map's legend (biomemap.txt), any case: the game's biome names, the names Expand World Data gives
    // the biomes it adds (its expand_biomes yaml), or a number, which is how Enum.ToString writes an added biome.
    // The result must be one biome the game can index: Enum.TryParse also returns combinations like "All".
    internal static bool TryParse(string name, out Heightmap.Biome biome)
    {
        name = name.Trim();
        // With Expand World Data this also finds its biomes, as it patches Enum.TryParse for biomes (then only names
        // parse); EWD.TryGetBiome asks it directly in case that patch does not reach this call.
        if (Enum.TryParse(name, true, out biome) && IsValid(biome))
            return true;
        if (EWD.TryGetBiome(name, out biome) && IsValid(biome))
            return true;
        if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && IsValid((Heightmap.Biome)value))
        {
            biome = (Heightmap.Biome)value;
            return true;
        }
        // Loosely: "Black Forest", "deep_north", "Ash-Lands" (the game's own spelling has spaces).
        var loose = new string([.. name.Where(ch => ch != ' ' && ch != '_' && ch != '-')]);
        if (loose.Length > 0 && loose != name && TryParse(loose, out biome))
            return true;
        biome = Heightmap.Biome.None;
        return false;
    }
    // The legend name TryParse reads back: the enum name for a vanilla biome, Expand World Data's name for one it
    // added, else the number.
    internal static string Name(Heightmap.Biome biome)
    {
        if (IsVanilla(biome))
            return biome.ToString();
        return EWD.TryGetName(biome, out var name) ? name : ((int)biome).ToString(CultureInfo.InvariantCulture);
    }
    // Name, but a number for every biome beyond vanilla: for legends read back before Expand World Data may have
    // its names (a client receiving a world's settings from the server).
    internal static string StableName(Heightmap.Biome biome) =>
        IsVanilla(biome) ? biome.ToString() : ((int)biome).ToString(CultureInfo.InvariantCulture);

    // Biomes are stored one byte per pixel as the bit index of the flag plus one (0 = None, 1 = Meadows, ...,
    // 10 = Mistlands, 11 = the first biome Expand World Data adds, up to 32), and read back as that bit whether or not
    // this game has the biome now (see Usable), so a save never drops one.
    internal static Heightmap.Biome FromByte(byte value) =>
        value >= 1 && value <= 32 ? (Heightmap.Biome)(int)(1u << (value - 1)) : Heightmap.Biome.None;
    // The bit index of a single bit, by de Bruijn multiplication (no loop per pixel of a 67-million-pixel map).
    private static readonly byte[] DeBruijnBit =
        [0, 1, 28, 2, 29, 14, 24, 3, 30, 22, 20, 15, 25, 17, 4, 8, 31, 27, 13, 23, 21, 19, 16, 7, 26, 12, 18, 6, 11, 5, 10, 9];
    internal static byte ToByte(Heightmap.Biome biome) =>
        IsSingleBit(biome) ? (byte)(DeBruijnBit[unchecked((uint)biome * 0x077CB531u) >> 27] + 1) : (byte)0;
    internal static string NameHelp()
    {
        var names = string.Join(", ", VanillaNames);
        if (Extra == Heightmap.Biome.None)
            return $"The biomes are {names}; the biomes Expand World Data adds need Expand World Data installed.";
        return $"The biomes are {names}, and the biomes Expand World Data adds, by the name in its expand_biomes yaml.";
    }
    private static readonly string[] VanillaNames = [.. Enumerable.Range(0, (int)Heightmap.BiomeIndex.Count).Select(i => ((Heightmap.BiomeIndex)i).ToBiome().ToString())];
}
