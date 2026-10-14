// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

/// <summary>Where the client side reads its settings (the plan's 8.2): bound by SettingsSchema, null where the config is not (the offline suites).</summary>
internal static class SeaSettings
{
  internal static ConfigEntry<bool>? OwnWater, SoftFarSea;
}

// BC'S OWN WATER ON A CLIENT (the plan's 6.4 to 6.8): BetterContinents/Water, a shader of BC's own in the embedded bundle, goes onto the game's
// own sea materials in place of Custom/Water, and reads the world's sea from the SeaField (a texture, the globals below). Everything is live:
//
// - The sea is the two materials `water` (each zone's WaterVolume surface) and `water_lod` (the far sea, the Water component under _GameMain's
//   WaterPlane); the hot tub, barber, mud and both interior waters keep the game's shader (the lead's decision 10). Each is swapped on the
//   Material instance the game already made, so everything that keys on that Material (GO's dials, BC's own _WaterEdge) keeps working, and the
//   values it holds survive the swap (they are checked by name and put back if one does not).
// - It is on while: the sea field is ready and is not the game's own, Own Water is on, the bundle loaded, and the shader runs on this graphics
//   device. When any of that stops being true (the setting turned off, the world left, a refresh), the game's shader is put back, kept from the
//   first swap. Surfaces that start later are swapped as they start (WaterVolume.Start), and the ones already alive when it turns on, and the far
//   sea, are swapped in the frame it does.
// - Otherwise the client draws the game's water with 0.10.3's colours (AshlandsWater), and ONE line in the log names the cause. The felt waves stay
//   the world's on every machine (SeaPatches).
//
// The shader's inputs are globals, so no material changes shape: _BCSeaField (RG16, the contract at the top of SeaField.cs), _BCSeaRect,
// _BCSeaDepth and _BCSeaDepthRect (the far sea's depth grid, Soft Far Sea), _BCSeaSoft (1 with Soft Far Sea on and the grid there).
internal static class SeaDraw
{
  internal const string ShaderName = "BetterContinents/Water";
  internal const string GameShaderName = "Custom/Water";
  // The bundle's name among the DLL's resources (csproj: LogicalName); it must not end as bcassets does (GameUtils.GetAssetBundleFromResources).
  internal const string BundleResource = "BetterContinents.bcwater";

  private static bool? headless;
  private static bool Headless => headless ??= SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

  // ---- DynamicPatch and Update ------------------------------------------------------------------------------------

  /// <summary>DynamicPatch, after the patches are switched: the world, or its settings, changed. The field is asked for again (SeaPatches), and the
  /// water's colours are worked out again (AshlandsWater). Engine-free: DynamicPatch also runs offline.</summary>
  internal static void Refresh()
  {
    blocked = null;
    SeaPatches.Refresh();
    AshlandsWater.Refresh();
  }

  /// <summary>Every frame (BetterContinents.Update).</summary>
  internal static void Tick()
  {
    SeaPatches.Tick();
    try
    {
      Reconcile();
    }
    catch (Exception e)
    {
      if (!failedOnce)
        LogError($"Water: drawing Better Continents' own sea failed ({e.GetType().Name}: {e.Message}); the game's water is drawn.");
      failedOnce = true;
      TurnOff("an error, said in the log", log: false);
    }
    AshlandsWater.Tick();
  }

  // ---- the state ---------------------------------------------------------------------------------------------------

  private static bool on;
  private static bool failedOnce;
  // Why the game's water is drawn now, and the cause last said in the log.
  private static string cause = "no world";
  private static string? said;
  // Another mod's shader on the sea: BC steps aside until the next refresh.
  private static string? blocked;

  private static bool bundleTried;
  private static AssetBundle? bundle;
  private static Shader? bcShader, gameShader;
  private static string? bundleError;

  private static SeaField? uploadedFor;
  private static Texture2D? fieldTexture, depthTexture;
  private static Task<byte[]>? preparing;
  private static SeaField? preparingFor;

  // The sea materials BC has changed, by renderer, and whether each is the far sea's.
  private static readonly Dictionary<Renderer, (Material Material, bool Far)> Swapped = [];
  private static int restoredValues, restoredDepths, frame;
  private static bool? softSet;

  // Unity's side, made when it is first used: DynamicPatch runs the rest of this class offline, without the engine.
  private static class Ids
  {
    internal static readonly int Field = Shader.PropertyToID("_BCSeaField"), Rect = Shader.PropertyToID("_BCSeaRect"),
      Depth = Shader.PropertyToID("_BCSeaDepth"), DepthRect = Shader.PropertyToID("_BCSeaDepthRect"), Soft = Shader.PropertyToID("_BCSeaSoft"),
      WaterDepth = Shader.PropertyToID("_depth");
  }

  /// <summary>Whether BC's shader draws this renderer's water now (AshlandsWater then leaves its colours alone).</summary>
  internal static bool Draws(Renderer? renderer) => renderer != null && Swapped.ContainsKey(renderer);

  // ---- when it is on -------------------------------------------------------------------------------------------------

  // Why BC's shader is not to be drawn now, or null when everything but the bundle and the textures is ready.
  private const string NoGraphics = "this machine draws nothing", NotReady = "the sea field is not ready yet", GameSea = "this world's sea is the game's",
    GameSeaAnyway = "this world's sea is the game's own", NoHot = "the sea field has no hot channel", OwnWaterOff = "Own Water is off";

  // The causes that are not a fallback: nothing to say in the log (SeaPatches says when the field is ready, 'bc water' says why).
  private static bool Quiet(string why) => why == NoGraphics || why == NotReady || why == GameSea || why == GameSeaAnyway || why == NoHot;

  private static string? WhyNot(SeaField? field) =>
    Cause(Headless, field, SeaPatches.Snapshot.Pending || SeaPatches.Snapshot.Building, SeaSettings.OwnWater is { Value: false }, blocked);

  /// <summary>Why the game's water is drawn instead of BC's, or null when nothing but the bundle and the textures stands in the way. Pure, for
  /// the offline tests. Quiet causes first: nothing to draw (no graphics, no field yet, a world whose sea is the game's), then the ones a player
  /// or another mod chose.</summary>
  internal static string? Cause(bool headless, SeaField? field, bool fieldComing, bool ownWaterOff, string? blockedBy)
  {
    if (headless)
      return NoGraphics;
    if (field == null)
      return fieldComing ? NotReady : GameSea;
    if (field.SameAsGame)
      return GameSeaAnyway;
    if (field.Hot == null)
      return NoHot;
    if (ownWaterOff)
      return OwnWaterOff;
    return blockedBy;
  }

  private static bool Idle => !on && Swapped.Count == 0 && fieldTexture == null && depthTexture == null && preparing == null;

  private static void Reconcile()
  {
    frame++;
    var field = SeaField.Current;
    if (field == null && Idle)
    {
      // The usual case on a vanilla world, in the menu and on a server: no engine calls at all.
      cause = SeaPatches.Snapshot.Pending || SeaPatches.Snapshot.Building ? NotReady : GameSea;
      return;
    }
    var why = WhyNot(field);
    var step = why == null ? Prepare(field!, out why) : Step.Failed;
    if (step == Step.Failed)
    {
      if (Idle)
        cause = why!;
      else
        TurnOff(why!, log: !Quiet(why!));
      if (Idle && !Quiet(why!) && said != why)
      {
        Log($"Water: the game's own water is drawn: {why}.");
        said = why;
      }
      return;
    }
    if (step == Step.Waiting)
    {
      cause = why!;
      return;
    }
    UploadDepth(field!);
    SetSoft();
    SwapAlive(everything: !on || frame % 30 == 0);
    if (blocked != null)
    {
      TurnOff(blocked, log: true);
      return;
    }
    if (!on)
    {
      on = true;
      cause = "";
      said = null;
      failedOnce = false;
      int zones = Swapped.Values.Count(v => !v.Far), far = Swapped.Count - zones;
      Log($"Water: Better Continents' own sea is on: {zones} zone water surface(s) and {far} far sea drawn with {ShaderName}"
        + (SeaSettings.SoftFarSea is not { Value: false } && depthTexture != null ? ", the far sea takes the depth of the sea under it" : "")
        + $" (the field: {field}).");
      // The zone colours of 0.10.3 give way to the shader's own (AshlandsWater leaves a renderer BC draws).
      AshlandsWater.Refresh();
    }
  }

  private enum Step { Ready, Waiting, Failed }

  // The bundle, the shader, and the field's textures. Failed: why not. Waiting: the field's bytes are being made on a worker.
  private static Step Prepare(SeaField field, out string? why)
  {
    why = null;
    if (uploadedFor == field && fieldTexture != null && bcShader != null)
      return Step.Ready;
    if (!EnsureShader(out why))
      return Step.Failed;
    if (!SystemInfo.SupportsTextureFormat(TextureFormat.RG16))
    {
      why = "this graphics device has no two-channel 8 bit texture format (RG16)";
      return Step.Failed;
    }
    // The texture's bytes are made on a worker (about 30 ms of the main thread otherwise), then uploaded in one go.
    if (preparingFor != field || preparing == null)
    {
      preparingFor = field;
      preparing = Task.Run(() => field.Rg16());
    }
    if (!preparing.IsCompleted)
    {
      why = "the sea field is being uploaded";
      return Step.Waiting;
    }
    if (preparing.IsFaulted)
    {
      why = "the sea field's texture could not be made (" + preparing.Exception!.GetBaseException().Message + ")";
      return Step.Failed;
    }
    Upload(field, preparing.Result);
    preparing = null;
    preparingFor = null;
    return Step.Ready;
  }

  private static void Upload(SeaField field, byte[] bytes)
  {
    DestroyTextures();
    var texture = new Texture2D(field.Size, field.Size, TextureFormat.RG16, false, true)
    {
      name = "BCSeaField", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, anisoLevel = 0,
    };
    texture.LoadRawTextureData(bytes);
    texture.Apply(false, true);
    fieldTexture = texture;
    Shader.SetGlobalTexture(Ids.Field, texture);
    Shader.SetGlobalVector(Ids.Rect, new Vector4(field.OriginX, field.OriginZ, field.InvSpacing, field.Size));
    uploadedFor = field;
    softSet = null;
    UploadDepth(field);
  }

  // The far sea's depth grid, when the field has it: with the field, or later (the ground's heights can be asked only once the world generator is
  // made, which can be after the field is built).
  private static SeaField? depthTriedFor;

  private static void UploadDepth(SeaField field)
  {
    if (depthTriedFor == field || field.Depth is not { } bytes)
      return;
    depthTriedFor = field;
    if (!SystemInfo.SupportsTextureFormat(TextureFormat.R8))
      return;
    var depth = new Texture2D(field.DepthSize, field.DepthSize, TextureFormat.R8, false, true)
    {
      name = "BCSeaDepth", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, anisoLevel = 0,
    };
    depth.LoadRawTextureData(bytes);
    depth.Apply(false, true);
    if (depthTexture != null)
      UnityEngine.Object.Destroy(depthTexture);
    depthTexture = depth;
    Shader.SetGlobalTexture(Ids.Depth, depth);
    Shader.SetGlobalVector(Ids.DepthRect, new Vector4(field.DepthOriginX, field.DepthOriginZ, 1f / SeaField.DepthSpacing, field.DepthSize));
    softSet = null;
  }

  // Soft Far Sea is live: _BCSeaSoft follows the setting (and whether the depth grid is there).
  private static void SetSoft()
  {
    bool soft = SeaSettings.SoftFarSea is not { Value: false } && depthTexture != null;
    if (softSet == soft)
      return;
    Shader.SetGlobalFloat(Ids.Soft, soft ? 1f : 0f);
    softSet = soft;
  }

  private static void DestroyTextures()
  {
    if (fieldTexture != null)
      UnityEngine.Object.Destroy(fieldTexture);
    if (depthTexture != null)
      UnityEngine.Object.Destroy(depthTexture);
    fieldTexture = depthTexture = null;
    uploadedFor = null;
    depthTriedFor = null;
  }

  // ---- the bundle ------------------------------------------------------------------------------------------------------

  private static bool EnsureShader(out string? why)
  {
    why = null;
    if (!bundleTried)
    {
      bundleTried = true;
      try
      {
        LoadBundle();
      }
      catch (Exception e)
      {
        bundleError = $"the water bundle could not be loaded ({e.GetType().Name}: {e.Message})";
      }
    }
    if (bcShader == null)
    {
      why = bundleError ?? "the water bundle holds no shader";
      return false;
    }
    if (!bcShader.isSupported)
    {
      why = "the water shader is not supported on this graphics device";
      return false;
    }
    return true;
  }

  // The embedded bundle is read into memory and loaded from there (LoadFromMemory keeps its own copy): a loader of its own, since the helper
  // for bcassets disposes its stream after LoadFromStream.
  private static void LoadBundle()
  {
    var assembly = Assembly.GetExecutingAssembly();
    if (!assembly.GetManifestResourceNames().Contains(BundleResource))
    {
      bundleError = "this build of Better Continents carries no water shader";
      return;
    }
    byte[] bytes;
    using (var stream = assembly.GetManifestResourceStream(BundleResource)!)
    using (var copy = new MemoryStream())
    {
      stream.CopyTo(copy);
      bytes = copy.ToArray();
    }
    bundle = AssetBundle.LoadFromMemory(bytes);
    if (bundle == null)
    {
      bundleError = "the water bundle could not be loaded (Unity refused it)";
      return;
    }
    bcShader = bundle.LoadAllAssets<Shader>().FirstOrDefault(s => s.name == ShaderName);
    if (bcShader == null)
      bundleError = $"the water bundle holds no shader named {ShaderName}";
    else
      Log($"Water: loaded {ShaderName} from the embedded bundle ({bytes.Length} bytes; LOD {bcShader.maximumLOD}, supported: {bcShader.isSupported}).");
  }

  // ---- swapping ----------------------------------------------------------------------------------------------------------

  /// <summary>WaterVolume.Start, after SetupMaterial and the other mods' postfixes: a zone's water that starts while BC's sea is on is swapped.</summary>
  internal static void Adopt(WaterVolume volume)
  {
    if (!on || volume == null)
      return;
    try
    {
      TrySwap(volume.m_waterSurface, far: false);
      if (blocked != null)
        TurnOff(blocked, log: true);
    }
    catch (Exception e)
    {
      if (!failedOnce)
        LogError($"Water: swapping a zone's water failed ({e.GetType().Name}: {e.Message}).");
      failedOnce = true;
    }
  }

  // The zone water and far sea that are alive and not swapped yet: the far sea every frame (one or two renderers), the zones every so often
  // (WaterVolume.Start swaps the ones that start while it is on), and the swapped ones that have gone are forgotten.
  private static void SwapAlive(bool everything)
  {
    foreach (var water in Water.Instances)
      if (water != null)
        TrySwap(water.GetComponent<MeshRenderer>(), far: true);
    if (!everything)
      return;
    foreach (var volume in AshlandsWater.Alive)
      if (volume != null)
        TrySwap(volume.m_waterSurface, far: false);
    if (Swapped.Count > 0 && Swapped.Keys.Any(r => r == null))
      foreach (var gone in Swapped.Keys.Where(r => r == null).ToList())
        Swapped.Remove(gone);
  }

  // The sea's materials: the zone water's `water` and the far sea's `water_lod`, not the hot tub, barber, mud or interior waters.
  private static bool IsSea(Material material) => IsSeaName(material.name);

  internal static bool IsSeaName(string name)
  {
    if (name.EndsWith(" (Instance)", StringComparison.Ordinal))
      name = name.Substring(0, name.Length - " (Instance)".Length);
    return name == "water" || name == "water_lod";
  }

  private static void TrySwap(MeshRenderer? renderer, bool far)
  {
    if (renderer == null || bcShader == null || Swapped.ContainsKey(renderer))
      return;
    // Only the sea's: looked at without making an instance of anything else's material.
    var shared = renderer.sharedMaterial;
    if (shared == null || !IsSea(shared))
      return;
    // .material: the game made an instance for a zone's water in SetupMaterial; the far sea gets its own now (one renderer).
    var material = renderer.material;
    if (material == null)
      return;
    var shader = material.shader;
    if (shader == bcShader)
    {
      Swapped[renderer] = (material, far);
      return;
    }
    if (shader == null || shader.name != GameShaderName)
    {
      blocked ??= $"another mod has replaced the game's water shader (the {material.name} material draws with {(shader == null ? "nothing" : shader.name)})";
      return;
    }
    gameShader ??= shader;
    bcShader.maximumLOD = gameShader.maximumLOD;
    var held = Capture(material);
    material.shader = bcShader;
    restoredValues += Restore(material, held);
    Swapped[renderer] = (material, far);
  }

  /// <summary>Back to the game's shader on every swapped material, the textures dropped, 0.10.3's colours again.</summary>
  private static void TurnOff(string why, bool log)
  {
    bool was = on || Swapped.Count > 0;
    if (gameShader != null)
      foreach (var (material, _) in Swapped.Values)
        if (material != null && material.shader == bcShader)
        {
          var held = Capture(material);
          material.shader = gameShader;
          restoredValues += Restore(material, held);
        }
    Swapped.Clear();
    on = false;
    DestroyTextures();
    preparing = null;
    preparingFor = null;
    if (softSet == true)
      Shader.SetGlobalFloat(Ids.Soft, 0f);
    softSet = null;
    cause = why;
    if (log && said != why)
      Log($"Water: the game's own water is drawn: {why}.");
    if (log)
      said = why;
    if (was)
      AshlandsWater.Refresh();
  }

  // ---- the values a material holds across the shader change -----------------------------------------------------------

  private sealed class Held
  {
    public readonly List<(int Id, float Value)> Floats = [];
    public readonly List<(int Id, Vector4 Value)> Vectors = [];
    public readonly List<(int Id, Texture? Value)> Textures = [];
    public float[]? Depth;
  }

  // The properties of the shader being left, and the zone's depth array, as the material holds them.
  private static Held Capture(Material material)
  {
    var held = new Held();
    var shader = material.shader;
    for (int i = 0, count = shader.GetPropertyCount(); i < count; i++)
    {
      int id = shader.GetPropertyNameId(i);
      switch (shader.GetPropertyType(i))
      {
        case ShaderPropertyType.Float:
        case ShaderPropertyType.Range:
          held.Floats.Add((id, material.GetFloat(id)));
          break;
        case ShaderPropertyType.Color:
        case ShaderPropertyType.Vector:
          held.Vectors.Add((id, material.GetVector(id)));
          break;
        case ShaderPropertyType.Texture:
          held.Textures.Add((id, material.GetTexture(id)));
          break;
      }
    }
    var depth = material.GetFloatArray(Ids.WaterDepth);
    held.Depth = depth is { Length: > 0 } ? depth : null;
    return held;
  }

  // Puts back any value that did not survive the change of shader (it is expected that none of the declared ones is lost: Unity keeps a material's values by name;
  // the zone depth array is the exception). Returns the number of declared values put back, for 'bc water'.
  private static int Restore(Material material, Held held)
  {
    int restored = 0;
    foreach (var (id, value) in held.Floats)
      if (material.HasProperty(id) && material.GetFloat(id) != value) { material.SetFloat(id, value); restored++; }
    foreach (var (id, value) in held.Vectors)
      if (material.HasProperty(id) && material.GetVector(id) != value) { material.SetVector(id, value); restored++; }
    foreach (var (id, value) in held.Textures)
      if (material.HasProperty(id) && material.GetTexture(id) != value) { material.SetTexture(id, value); restored++; }
    if (held.Depth != null)
    {
      // The zone water's four corner depths are an array the game sets and no shader declares (not the game's either); the first run in the game showed
      // that Unity gives it up at a shader change, so it is always put back, and counted apart from the declared values.
      var now = material.GetFloatArray(Ids.WaterDepth);
      if (now == null || now.Length != held.Depth.Length || !now.SequenceEqual(held.Depth)) { material.SetFloatArray(Ids.WaterDepth, held.Depth); restoredDepths++; }
    }
    return restored;
  }

  // ---- the game's Tessellation setting ----------------------------------------------------------------------------------

  // The game picks the cheap or the tessellated sub-shader of its water shaders by their maximumLOD (250 or 200); BC's shader takes the same.
  [HarmonyPatch(typeof(GraphicsSettingsManager), "ApplyTesselation")]
  private static class ApplyTesselationPatch
  {
    [HarmonyPostfix]
    private static void Postfix(GraphicsSettingsManager __instance)
    {
      try
      {
        var shaders = __instance.m_tesselationShaders;
        var game = shaders?.FirstOrDefault(s => s != null && s.name == GameShaderName);
        if (game != null && bcShader != null)
          bcShader.maximumLOD = game.maximumLOD;
      }
      catch (Exception e)
      {
        LogWarning($"Water: could not copy the game's tessellation setting to the water shader ({e.Message}).");
      }
    }
  }

  // ---- what 'bc water' says ------------------------------------------------------------------------------------------------

  internal static void Report(Action<string> output)
  {
    SeaPatches.Report(output);
    if (on)
    {
      int zones = Swapped.Values.Count(v => !v.Far), far = Swapped.Count - zones;
      output($"Water: drawn with {ShaderName}: {zones} zone water surface(s) and {far} far sea (LOD {bcShader?.maximumLOD}). "
        + $"Soft Far Sea is {(softSet == true ? "on" : "off")}.");
      output($"Water: {restoredValues} material value(s) did not survive a shader change and were put back{(restoredValues == 0 ? " (none: Unity keeps them by name)" : "")}; the zone depth arrays, which no shader declares, were put back {restoredDepths} time(s).");
    }
    else
    {
      output($"Water: the game's own water is drawn: {(cause.Length > 0 ? cause : "BC's sea is not on")}.");
    }
  }
}
