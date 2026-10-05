// Modified by Wubarrk on 2026-09-22 for Valheim 1.0.15 support (0.8.0) and alt-biome planting (0.8.1), and on 2026-10-04 for the unifying refactor (0.10.0).

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace BetterContinents;

public static class GameUtils
{
    public static void Reset()
    {
        FastMinimapRegen();
        // Valheim 1.0 takes every terrain chunk's biome from the alt-biome sector grid (HeightmapBuilder.Build
        // reads GetBiomeSector for its corners), and that grid is only built at world load. Rebuild it from the
        // new settings first, then reset the zones, so they regenerate against the new biomes and sectors.
        BetterContinents.AltBiomeControl.RequestRebuild(BetterContinents.AltBiomeControl.RebuildLevel.Points, "settings changed", ResetZones);
    }

    // The game's own calls that ResetZones makes, as fields that tools/zone-tests stands in for: the console runs a command,
    // and Better Continents' regeneration is requested.
    internal static Action<string> RunConsoleCommand = command => Console.instance.TryRunCommand(command);
    internal static Action RequestRegeneration = () => ZoneRegen.Request();
    // Poke takes the amount of frames to delay by, 1 matches the old "delayed" flag.
    internal static Action<Heightmap> PokeHeightmap = hm => hm.Poke(1);

    // After every "bc" change: the loaded terrain is built again and the zones generate again. Better Continents regenerates
    // the zones itself (ZoneRegen) unless Debug Reset Command holds a console command, which runs instead, as it always has.
    public static void ResetZones() => StartReset(rebuildTerrain: true);

    // "bc regen": the settings are as they were, so the loaded terrain is left as it was built. Building every loaded chunk
    // again (the distant terrain too) happens in one frame and stalls the game for most of a second, for nothing here.
    public static void RegenerateZones() => StartReset(rebuildTerrain: false);

    private static void StartReset(bool rebuildTerrain)
    {
        var command = BetterContinents.ConfigDebugResetCommand.Value;
        bool own = ZoneRegen.RunsOwn(command);
        if (!own)
            RunConsoleCommand(command);
        if (rebuildTerrain)
            PokeHeightmaps();
        if (own)
            RequestRegeneration();
    }

    // The terrain that is loaded was built from the settings before the change.
    internal static void PokeHeightmaps()
    {
        foreach (var hm in Heightmap.s_heightmaps)
        {
            hm.m_buildData = null;
            // Heightmap only ever appends to these (Heightmap.cs:434-448), so a chunk rebuilt in place would keep
            // the sectors and alt biomes of every earlier build; SpawnSystem reads m_cornerAltBiomes.
            hm.m_cornerBiomeList.Clear();
            hm.m_cornerAltBiomes.Clear();
            PokeHeightmap(hm);
        }
        // The distant terrain is not in s_heightmaps (Heightmap.cs:184-189, 220-225), and TerrainLod builds it again only once the
        // camera has moved 256 m (TerrainLod.cs:172), so it would show the old settings until then.
        PokeDistantTerrain(Heightmap.Instances, PokeHeightmap);
        DropStaleTerrain();
    }

    // The distant terrain TerrainLod has built (its nine heightmaps), to be built again where it is. One it has not built yet has
    // nothing old to show, and is left to it. How many were poked.
    internal static int PokeDistantTerrain(IEnumerable<IMonoUpdater> heightmaps, Action<Heightmap> poke)
    {
        int poked = 0;
        foreach (var updater in heightmaps)
        {
            if (updater is Heightmap { IsDistantLod: true, m_buildData: not null } hm)
            {
                hm.m_buildData = null;
                poke(hm);
                poked++;
            }
        }
        return poked;
    }

    // What was made from the settings before the change and is still around, for the terrain that was just poked to build again
    // (a delayed Poke runs in this frame's LateUpdate): the terrain builder's ready list holds up to 16 builds that the rebuild would
    // take as its own (HeightmapBuilder.cs:51-60, 110-146, 260-306), and the grass is cut from the ground as it is after that
    // rebuild, so it is cleared once the frame has run. The ready list is emptied now and again then, because a build that was under
    // way is added to it when it returns. In the menu or a closed world there is nothing of either to drop.
    internal static void DropStaleTerrain()
    {
        ClearReadyBuilds();
        var host = BetterContinents.instance;
        if (host)
            host.StartCoroutine(DropStaleTerrainLater());
    }

    private static IEnumerator DropStaleTerrainLater()
    {
        yield return null;
        ClearReadyBuilds();
        var clutter = ClutterSystem.instance;
        if (clutter)
            clutter.ClearAll();
    }

    // The builder's own lock guards its lists, and the build thread adds a finished build and trims the list in one hold of it. Only the
    // ready list is emptied: the thread reads m_toBuild[0] in a second hold after it has seen the list is not empty (:117-127), so a
    // list emptied from here in between would throw there and end the thread, and every terrain request would then wait for ever; and
    // what is still queued is built after the change anyway. A builder that was never made, or that was disposed of (which clears its
    // lock), has nothing to clear, and asking the game for it would make one.
    internal static void ClearReadyBuilds()
    {
        var builder = HeightmapBuilder.m_instance;
        var padlock = builder?.m_lock;
        if (builder == null || padlock == null)
            return;
        lock (padlock)
            builder.m_ready.Clear();
    }

    private static int MinimapOrigTextureSize = 0;
    private static float MinimapOrigPixelSize = 0;

    public static int MinimapDownscalingPower = 2;

    public static void FastMinimapRegen()
    {
        var map = Minimap.instance;
        int MinimapDownscaling = (int)Mathf.Pow(2, Mathf.Clamp(MinimapDownscalingPower, 0, 3));
        if (MinimapOrigTextureSize == 0
            || map.m_textureSize != MinimapOrigTextureSize / MinimapDownscaling)
        {
            if (MinimapOrigTextureSize == 0)
            {
                MinimapOrigTextureSize = map.m_textureSize;
                // The game's pixels, without what a world laid out to its own size multiplied them by.
                MinimapOrigPixelSize = map.m_pixelSize / WorldSizeHelper.MinimapScale;
            }
            var size = MinimapOrigTextureSize / MinimapDownscaling;
            map.m_textureSize = size;
            map.m_pixelSize = MinimapOrigPixelSize * MinimapDownscaling * WorldSizeHelper.MinimapScale;
            // Formats must match Minimap.Start, otherwise the map shaders get the wrong data.
            map.m_mapTexture = new(size, size, TextureFormat.RGB24, false)
            {
                wrapMode = TextureWrapMode.Clamp
            };
            // Mask and fog use a runtime selected graphics format, so let the game pick it.
            map.m_forestMaskTexture = map.CreateMapTexture([GraphicsFormat.B4G4R4A4_UNormPack16, GraphicsFormat.R4G4B4A4_UNormPack16], TextureFormat.RGBA32);
            map.m_forestMaskTexture.wrapMode = TextureWrapMode.Clamp;
            map.m_heightTexture = new(size, size, TextureFormat.RHalf, false, true)
            {
                wrapMode = TextureWrapMode.Clamp
            };
            map.m_fogTexture = map.CreateMapTexture([GraphicsFormat.R8G8_UNorm], TextureFormat.RGBA32);
            map.m_fogTexture.wrapMode = TextureWrapMode.Clamp;
            map.m_explored = new BitArray(size * size, false);
            map.m_exploredOthers = new BitArray(size * size, false);
            map.m_mapImageLarge.material.SetTexture("_MainTex", map.m_mapTexture);
            map.m_mapImageLarge.material.SetTexture("_MaskTex", map.m_forestMaskTexture);
            map.m_mapImageLarge.material.SetTexture("_HeightTex", map.m_heightTexture);
            map.m_mapImageLarge.material.SetTexture("_FogTex", map.m_fogTexture);
            map.m_mapImageSmall.material.SetTexture("_MainTex", map.m_mapTexture);
            map.m_mapImageSmall.material.SetTexture("_MaskTex", map.m_forestMaskTexture);
            map.m_mapImageSmall.material.SetTexture("_HeightTex", map.m_heightTexture);
            map.m_mapImageSmall.material.SetTexture("_FogTex", map.m_fogTexture);
            map.Reset();
        }
        map.ForceRegen();
        map.ExploreAll();
    }

    public static void SaveMinimap(string path, int size)
    {
        BetterContinents.instance.StartCoroutine(SaveMinimapImpl(path, size));
    }

    private static GameObject CreateQuad(float width, float height, float z, Material material)
    {
        var gameObject = new GameObject();
        MeshRenderer meshRenderer = gameObject.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = material;//new Material(Shader.Find("Standard"));

        MeshFilter meshFilter = gameObject.AddComponent<MeshFilter>();

        Mesh mesh = new();

        Vector3[] vertices =
        [
            new Vector3(-width / 2, -height / 2, z),
            new Vector3(width / 2, -height / 2, z),
            new Vector3(-width / 2, height / 2, z),
            new Vector3(width / 2, height / 2, z)
        ];
        mesh.vertices = vertices;

        int[] tris =
        [
            // lower left triangle
            0,
            2,
            1,
            // upper right triangle
            2,
            3,
            1
        ];
        mesh.triangles = tris;

        Vector3[] normals =
        [
            -Vector3.forward,
            -Vector3.forward,
            -Vector3.forward,
            -Vector3.forward
        ];
        mesh.normals = normals;

        Vector2[] uv =
        [
            new Vector2(0, 0),
            new Vector2(1, 0),
            new Vector2(0, 1),
            new Vector2(1, 1)
        ];
        mesh.uv = uv;

        meshFilter.mesh = mesh;

        return gameObject;
    }

    private static Texture? CloudTexture;
    private static Texture? TransparentTexture;

    public static bool MinimapCloudsEnabled =>
        Minimap.instance.m_mapImageLarge.material.GetTexture("_CloudTex") != TransparentTexture;

    public static void EnableMinimapClouds()
    {
        if (!MinimapCloudsEnabled)
        {
            Minimap.instance.m_mapImageLarge.material.SetTexture("_CloudTex", CloudTexture);
        }
    }

    public static void DisableMinimapClouds()
    {
        if (MinimapCloudsEnabled)
        {
            var mat = Minimap.instance.m_mapImageLarge.material;

            CloudTexture = mat.GetTexture("_CloudTex");
            if (TransparentTexture == null)
            {
                TransparentTexture = UI.CreateFillTexture(new Color32(0, 0, 0, 0));
            }

            mat.SetTexture("_CloudTex", TransparentTexture);
        }
    }

    private static IEnumerator SaveMinimapImpl(string path, int size)
    {
        bool wasLarge = Minimap.instance.m_largeRoot.activeSelf;
        if (!wasLarge)
        {
            Minimap.instance.SetMapMode(Minimap.MapMode.Large);
            Minimap.instance.CenterMap(Vector3.zero);
        }

        bool wasClouds = MinimapCloudsEnabled;
        DisableMinimapClouds();

        var mapPanelObject = CreateQuad(100, 100, 10, Minimap.instance.m_mapImageLarge.material);

        mapPanelObject.layer = 19;

        var renderTexture = new RenderTexture(size, size, 24);
        GameObject cameraObject = new()
        {
            layer = 19
        };
        var camera = cameraObject.AddComponent<Camera>();
        camera.targetTexture = renderTexture;
        camera.orthographic = true;
        camera.rect = new Rect(0, 0, renderTexture.width, renderTexture.height);
        camera.nearClipPlane = 0;
        camera.farClipPlane = 100;
        camera.orthographicSize = 50;
        camera.cullingMask = 1 << 19;
        camera.Render();

        yield return new WaitForEndOfFrame();

        RenderTexture.active = renderTexture;
        var tex = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        Console.instance.Print($"Screenshot of minimap saved to {path}");

        File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));

        Object.Destroy(mapPanelObject);
        Object.Destroy(cameraObject);
        Object.Destroy(renderTexture);
        Object.Destroy(tex);

        if (!wasLarge)
        {
            Minimap.instance.SetMapMode(Minimap.MapMode.Small);
        }

        if (wasClouds)
        {
            EnableMinimapClouds();
        }
    }

    // Does NOT support sub directories in the resources...
    public static void UnpackDirectoryFromResources(string resourceDirectory, string targetDirectory)
    {
        var execAssembly = Assembly.GetExecutingAssembly();

        try
        {
            Directory.CreateDirectory(targetDirectory);

            BetterContinents.Log($"Extracting all files from {resourceDirectory} to {targetDirectory}");

            if (!resourceDirectory.EndsWith("."))
                resourceDirectory += ".";

            foreach (string fullResourceName in execAssembly.GetManifestResourceNames()
                         .Where(str => str.StartsWith(resourceDirectory)))
            {
                string targetFileName =
                    Path.Combine(targetDirectory, fullResourceName.Replace(resourceDirectory, ""));
                if (!File.Exists(targetFileName))
                {
                    BetterContinents.Log($"Extracting {fullResourceName} to {targetFileName} ...");
                    using var stream = execAssembly.GetManifestResourceStream(fullResourceName);
                    using var targetStream = File.OpenWrite(targetFileName);
                    stream?.CopyTo(targetStream);
                }
                else
                {
                    BetterContinents.Log($"{targetFileName} already exists, skipping extraction");
                }
            }
        }
        catch (Exception ex)
        {
            BetterContinents.LogError($"Failed to unpack resource directory {resourceDirectory} to {targetDirectory}: {ex.Message}");
        }
    }

    public static AssetBundle GetAssetBundleFromResources(string partialResourceName)
    {
        var execAssembly = Assembly.GetExecutingAssembly();

        try
        {
            string fullResourceName = execAssembly.GetManifestResourceNames()
                .Single(str => str.EndsWith(partialResourceName));
            BetterContinents.Log($"Loading asset bundle {fullResourceName}");
            using var stream = execAssembly.GetManifestResourceStream(fullResourceName);
            return AssetBundle.LoadFromStream(stream);
        }
        catch (Exception ex)
        {
            BetterContinents.LogError($"Failed to get asset bundle {partialResourceName}: {ex.Message}");
            return null!;
        }
    }

    public static void ShowOnMap(params string[] list)
    {
        var locationInstances = ZoneSystem.instance.m_locationInstances;
        foreach (var lg in locationInstances.Values.GroupBy(l => l.m_location.m_prefabName))
        {
            if (list == null || list.Length == 0 || list.Any(f => lg.Key.ToLower().StartsWith(f.ToLower())))
            {
                BetterContinents.Log($"Marking {lg.Count()} {lg.Key} locations on map");
                int idx = 0;
                foreach (var li in lg)
                {
                    Minimap.instance.AddPin(li.m_position, Minimap.PinType.Icon3,
                        $"{li.m_location.m_prefabName} {idx++}", false, false);
                }
            }
        }
    }

    public static void HideOnMap(params string[] list)
    {
        var pins = Minimap.instance.m_pins;
        if (list == null || list.Length == 0)
        {
            foreach (var pin in pins.ToList())
            {
                Minimap.instance.RemovePin(pin);
            }
        }
        else
        {
            var locationInstances = ZoneSystem.instance.m_locationInstances;
            foreach (var lg in locationInstances.Values.GroupBy(l => l.m_location.m_prefabName))
            {
                if (list.Any(f => lg.Key.ToLower().StartsWith(f.ToLower())))
                {
                    BetterContinents.Log($"Hiding {lg.Count()} {lg.Key} locations from the map");
                    int idx = 0;
                    foreach (var li in lg)
                    {
                        var name = $"{li.m_location.m_prefabName} {idx++}";
                        var pin = pins.FirstOrDefault(p => p.m_name == name && p.m_pos == li.m_position);
                        if (pin != null)
                            Minimap.instance.RemovePin(pin);
                    }
                }
            }
        }
    }

    public static void SimpleParallelFor(int taskCount, int from, int to, Action<int> action)
    {
        var tasks = new Task[taskCount];
        int perTaskCount = (to - from) / taskCount;
        for (int i = 0, f = from; i < taskCount - 1; i++, f += perTaskCount)
        {
            int taskFrom = f;
            int taskTo = f + perTaskCount;
            tasks[i] = Task.Run(() =>
            {
                for (int j = taskFrom; j < taskTo; j++)
                {
                    action(j);
                }
            });
        }
        // Make sure last task definitely captures all the values
        tasks[taskCount - 1] = Task.Run(() =>
        {
            for (int j = from + (taskCount - 1) * perTaskCount; j < to; j++)
            {
                action(j);
            }
        });
        Task.WaitAll(tasks);
    }
}
