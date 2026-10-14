// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).
// Unity editor script: builds bcwater, Better Continents' water bundle: BetterContinents/Water (the sea) and BetterContinents/WaterProbe (the probe of
// `bc water probe`). It is not part of the mod's build (the csproj leaves unity/** out).
//
// Use: tools/build-water.sh copies the shaders, the includes, SeaWavesCheck and this file into the Unity project and runs
// -executeMethod BuildBcWater.Build with a graphics device (never -nographics). The bundle is built for StandaloneWindows64 with Direct3D 11,
// Vulkan and OpenGL Core programs, so the one file serves Windows (Direct3D 11) and Linux (Vulkan, OpenGL Core) clients, as VALtima's snow bundle does.
// A failure throws, so the editor exits with an error. The build fails when: a shader has errors or is not supported by this device, a property of
// the game's Custom/Water is missing or has another type, the GPU wave check (SeaWavesCheck) fails, or the bundle is empty. tools/build-water.sh then
// checks the bundle's contents (all graphics APIs in every pass) with tools/water-tests/facts/bundle_check.py.
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildBcWater
{
    private const string BundleName = "bcwater";
    private const string Folder = "Assets/BCWater";
    private const string WaterPath = Folder + "/BetterContinentsWater.shader";
    private const string ProbePath = Folder + "/BetterContinentsWaterProbe.shader";
    // Not named like the bundle: the bundle build writes a manifest bundle called after this folder (lower-cased), which would overwrite a bundle called bcwater.
    private const string OutputFolder = "Build/Out";

    // the 33 properties of the game's Custom/Water (libs-Tools WATER-SHADER-FACTS.md, section 2): the game and other mods write them by name
    private static readonly string[] Properties =
    {
        "_ColorTop:Color", "_ColorBottom:Color", "_ColorBottomShallow:Color", "_Glossiness:Range", "_Metallic:Range",
        "_FoamTex:Texture", "_FoamHighTex:Texture", "_RandomFoamTex:Texture", "_Normal:Texture", "_NormalFine:Texture",
        "_NormalPower:Range", "_NormalScale:Float", "_WaveVel:Float", "_DepthFade:Float", "_ShoreFade:Float", "_FoamDepth:Float",
        "_FoamColor:Color", "_RefractionScale:Float", "_RefractionMax:Float", "_SurfaceColor:Color", "_Tess:Range", "_IsLod:Float",
        "_VisibleMaxDistance:Float", "_WaterEdge:Float", "_CurlTex:Texture", "_BubbleTexture:Texture", "_AshlandsDistance:Float",
        "_AshlandsYOffset:Float", "_AshlandsSurfaceColor:Color", "_AshlandsFoamColor:Color", "_AshlandsColorTop:Color",
        "_AshlandsColorBottom:Color", "_AshlandsColorBottomShallow:Color",
    };

    [MenuItem("BetterContinents/Build Water Bundle")]
    public static void Build()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            throw new Exception("[BCWater] no graphics device: run the editor without -nographics");
        // the game is in the Linear colour space and Unity's shader library compiles differently in Gamma (a hand-made project starts in Gamma)
        if (PlayerSettings.colorSpace != ColorSpace.Linear)
        {
            PlayerSettings.colorSpace = ColorSpace.Linear;
            AssetDatabase.SaveAssets();
            Debug.Log("[BCWater] colour space set to Linear");
        }
        AssetDatabase.Refresh();
        var water = Load(WaterPath);
        var probe = Load(ProbePath);
        CheckProperties(water);
        Debug.Log($"[BCWater] BetterContinents/Water: {water.passCount} passes, supported here {water.isSupported}; BetterContinents/WaterProbe: {probe.passCount} passes, supported here {probe.isSupported}");

        try { MaterialSwapProbe.Run(); }
        catch (Exception e) { Debug.Log("[BCWater] probe material keeps values: failed (" + e.GetType().Name + ": " + e.Message + ")"); }

        SeaWavesCheck.Run();

        // one bundle for Windows and Linux: Direct3D 11, Vulkan and OpenGL Core programs (Valheim's Linux build runs Vulkan or OpenGL Core)
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLCore });

        Directory.CreateDirectory(OutputFolder);
        var build = new AssetBundleBuild { assetBundleName = BundleName, assetNames = new[] { WaterPath, ProbePath } };
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(OutputFolder, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        if (manifest == null)
            throw new Exception("[BCWater] the bundle build failed; the errors are above");
        string path = Path.Combine(OutputFolder, BundleName);
        long bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (bytes == 0)
            throw new Exception("[BCWater] " + path + " is missing or empty");
        Debug.Log($"[BCWater] built {path}: {bytes} bytes");
    }

    private static Shader Load(string path)
    {
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
        if (shader == null)
            throw new Exception("[BCWater] no shader at " + path);
        var messages = ShaderUtil.GetShaderMessages(shader);
        foreach (var m in messages)
            Debug.Log($"[BCWater] {shader.name} {m.severity}: {m.message} ({m.platform}, {m.file} line {m.line})");
        if (messages.Any(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error))
            throw new Exception($"[BCWater] {shader.name} has errors; see above");
        if (!shader.isSupported)
            throw new Exception($"[BCWater] {shader.name} is not supported on this device");
        return shader;
    }

    private static void CheckProperties(Shader shader)
    {
        int count = shader.GetPropertyCount();
        var found = Enumerable.Range(0, count).ToDictionary(i => shader.GetPropertyName(i), i => shader.GetPropertyType(i).ToString());
        var missing = Properties.Where(p => { var kv = p.Split(':'); return !found.TryGetValue(kv[0], out var type) || type != kv[1]; }).ToArray();
        if (missing.Length > 0)
            throw new Exception("[BCWater] BetterContinents/Water lacks or retypes properties of Custom/Water: " + string.Join(", ", missing) + " (it has " + string.Join(", ", found.Select(kv => kv.Key + ":" + kv.Value)) + ")");
        Debug.Log($"[BCWater] BetterContinents/Water has all {Properties.Length} properties of Custom/Water ({count} in all)");
    }
}
