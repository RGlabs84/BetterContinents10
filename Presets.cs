// Modified by Wubarrk on 2026-09-22 for Valheim 1.0.15 support (0.8.0) and alt-biome planting (0.8.1), and on 2026-09-24 for world export and import (0.9.0).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BetterContinents;

public class Presets
{
  // Valheim 1.0 (Unity 6) added ReadOnlySpan<byte> overloads to Texture2D.LoadImage. Calling LoadImage at
  // all - even with a byte[] - makes the compiler resolve System.ReadOnlySpan<T> just to build the overload
  // set, and net4.8's corlib has no such type. The netstandard 2.1 facade the Unity modules require (see the
  // netstandard reference in the csproj, without which ImageConversionModule fails CS1705) forwards
  // ReadOnlySpan to an assembly that is not part of a net4.8 compilation, so it fails CS0518 no matter which
  // Span shim is referenced - System.Memory 4.5.5 and 4.6.3, a System.Runtime facade and the game's own
  // System.Memory.dll were all tried. Moving the project to netstandard2.1 fixes the type but removes
  // System.Reflection.Emit.ILGenerator, which Harmony's transpilers need, so that is not an option either.
  // Binding the byte[] overload once through reflection sidesteps the overload set entirely.
  private static readonly MethodInfo LoadImageMethod = typeof(ImageConversion).GetMethod(
      nameof(ImageConversion.LoadImage), new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) });

  private static void LoadImageCompat(Texture2D tex, byte[] data)
  {
    if (LoadImageMethod == null)
    {
      BetterContinents.LogError("Could not bind ImageConversion.LoadImage(Texture2D, byte[], bool); preset preview icon will not be shown.");
      return;
    }
    LoadImageMethod.Invoke(null, new object[] { tex, data, false });
  }

  private static string? presetsDir;

  /// <summary>&lt;save data (local)&gt;/BetterContinents/presets: every "*.BetterContinents" file there is a choice in the
  /// New World screen. Read on first use, not at type load, so the offline harness can point it elsewhere.</summary>
  internal static string PresetsDir
  {
    get => presetsDir ??= Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "BetterContinents", "presets");
    set => presetsDir = value;
  }

  /// <summary>The New World screen's presets (FejdStartupPatch keeps the one instance).</summary>
  internal static Presets? Active { get; private set; }

  internal const string DisabledName = Disabled;
  internal const string FromConfigName = FromConfig;

  private static AssetBundle? assetBundle;

  private static bool DisabledPreset => BetterContinents.ConfigSelectedPreset.Value == Disabled;
  private static bool ConfigPreset => BetterContinents.ConfigSelectedPreset.Value == FromConfig;

  private List<string> presets = [];
  private const string Disabled = "Disabled";
  private const string FromConfig = "From Config";

#nullable disable
  private Dropdown dropdown;
  private GameObject previewPanel;
  private RawImage previewImage;

  private Texture2D logoIcon;
  private Texture2D settingsIcon;
#nullable enable
  private Texture2D? loadedIcon;
  public Presets()
  {
    Active = this;
    Refresh();
  }

  /// <summary>Awake: whenever the selected preset changes outside the dropdown (bc_import, an edit of
  /// BetterContinents.cfg that LiveConfig reads), the dropdown shows it and lists any new preset file.</summary>
  internal static void WatchSelection()
  {
    BetterContinents.ConfigSelectedPreset.SettingChanged += (_, _) =>
    {
      try
      {
        Active?.SyncSelection();
      }
      catch (Exception ex)
      {
        BetterContinents.LogWarning($"Could not update the preset dropdown: {ex.Message}");
      }
    };
  }

  /// <summary>Lists the presets folder again (a preset was added) and shows the selected preset. Main thread.</summary>
  internal static void RefreshActive()
  {
    try
    {
      Active?.Refresh();
    }
    catch (Exception ex)
    {
      BetterContinents.LogWarning($"Could not refresh the preset dropdown: {ex.Message}");
    }
  }

  // The selection changed: nothing to do when the dropdown already shows it (it made the change itself).
  private void SyncSelection()
  {
    if (dropdown == null)
      return;
    int idx = presets.FindIndex(p => string.Equals(p, BetterContinents.ConfigSelectedPreset.Value, StringComparison.CurrentCultureIgnoreCase));
    if (idx != -1 && idx == dropdown.value)
      return;
    Refresh();
  }

  public void InitUI(FejdStartup __instance)
  {
    var panel = (RectTransform)__instance.m_newWorldSeed.transform.parent;

    if (assetBundle == null)
    {
      assetBundle = GameUtils.GetAssetBundleFromResources("bcassets");
      BetterContinents.Log("Loaded asset bundle");

      GameUtils.UnpackDirectoryFromResources("BetterContinents.assets.BCAssets.Presets", PresetsDir);
    }

    logoIcon = assetBundle.LoadAsset<Texture2D>("Assets/logo256.png");
    settingsIcon = assetBundle.LoadAsset<Texture2D>("Assets/settings256.png");

    var prefab = assetBundle.LoadAsset<GameObject>("Assets/BCPresetPrefab.prefab");

    var item = Object.Instantiate(prefab, panel);
    item.FixReferences(typeof(Image));

    previewPanel = item.transform.Find("MapPreview").gameObject;
    previewImage = previewPanel.GetComponentInChildren<RawImage>();
    previewPanel.SetActive(false);

    dropdown = item.GetComponentInChildren<Dropdown>();
    dropdown.onValueChanged.AddListener(idx =>
    {
      if (idx >= 0 && idx < presets.Count)
      {
        BetterContinents.ConfigSelectedPreset.Value = presets[idx];
        UpdatePreview();
      }
    });
    Refresh();
    UpdatePreview();

    BetterContinents.Log("Setup UI");
  }

  private void UpdatePreview()
  {
    if (previewPanel != null)
    {
      if (DisabledPreset)
      {
        previewPanel.SetActive(false);
      }
      else if (ConfigPreset)
      {
        previewPanel.SetActive(true);
        previewImage.texture = settingsIcon;
      }
      else
      {
        previewPanel.SetActive(true);
        // "<name>.png" beside "<name>.BetterContinents". (This took the path up to its first dot, which on Linux is
        // the one in ~/.config, so no preset showed its picture there, nor where the user name has a dot.)
        string configIconPath = Path.ChangeExtension(BetterContinents.ConfigSelectedPreset.Value, ".png");
        // The previous picture goes: the preview is redrawn on every refresh now (bc_import, a config edit).
        if (loadedIcon != null)
          Object.Destroy(loadedIcon);
        loadedIcon = null;
        if (File.Exists(configIconPath))
        {
          var icon = new Texture2D(2, 2);
          LoadImageCompat(icon, File.ReadAllBytes(configIconPath));
          previewImage.texture = loadedIcon = icon;
        }
        else
        {
          previewImage.texture = logoIcon;
        }
      }
    }
  }

  internal void Refresh()
  {
    // The file name without ".BetterContinents" (UpTo(".") cut "My.World" to "My").
    static string NameFromPath(string path) => Path.GetFileNameWithoutExtension(path).AddSpacesToWords();

    // A failed listing leaves just the two built-in choices (it used to keep the old list and add them again).
    presets = [];
    try
    {
      Directory.CreateDirectory(PresetsDir);

      presets = Directory
              .GetFiles(PresetsDir, "*.BetterContinents")
              .ToList()
          ;
    }
    catch (Exception ex)
    {
      BetterContinents.LogError($"Error while loading presets: {ex.Message}");
    }

    presets.Insert(0, Disabled);
    presets.Add(FromConfig);

    if (dropdown != null)
    {
      dropdown.ClearOptions();
      var presetNames = presets.Select(NameFromPath).ToList();
      Debug.Log($"Preset names = {string.Join(", ", presetNames)}");
      dropdown.itemText.horizontalOverflow = HorizontalWrapMode.Overflow;
      dropdown.itemText.verticalOverflow = VerticalWrapMode.Overflow;
      dropdown.AddOptions(presetNames);
      int idx = presets.FindIndex(p => string.Equals(p, BetterContinents.ConfigSelectedPreset.Value,
          StringComparison.CurrentCultureIgnoreCase));
      if (idx != -1)
      {
        dropdown.SetValueWithoutNotify(idx);
      }
    }

    UpdatePreview();
  }

  public static BetterContinents.BetterContinentsSettings LoadActivePreset()
  {
    if (DisabledPreset)
    {
      return BetterContinents.BetterContinentsSettings.Disabled();
    }

    if (ConfigPreset)
    {
      return BetterContinents.BetterContinentsSettings.Create();
    }

    if (!File.Exists(BetterContinents.ConfigSelectedPreset.Value))
    {
      BetterContinents.LogError($"Selected preset path {BetterContinents.ConfigSelectedPreset.Value} doesn't exist, BC is disabled for this world!");
      return BetterContinents.BetterContinentsSettings.Disabled();
    }

    try
    {
      var settings = BetterContinents.BetterContinentsSettings.Load(BetterContinents.ConfigSelectedPreset.Value);
      // A preset saved before 0.8.1 (including the four shipped ones) has no alt-biome options. This is a NEW
      // world, so it gets the new-world defaults from the config rather than the legacy behaviour that an
      // existing world without the key keeps. A preset carries no alt-biome map unless it was saved with one.
      if (settings.EnabledForThisWorld && settings.AltBiomes == null)
        settings.AltBiomes = BetterContinents.AltBiomeSettings.FromConfig();
      return settings;
    }
    catch (Exception ex)
    {
      BetterContinents.Log($"Couldn't load preset {BetterContinents.ConfigSelectedPreset.Value} ({ex.Message}), BC is disabled for this world!");
      return BetterContinents.BetterContinentsSettings.Disabled();
    }
  }

  public static void Save(BetterContinents.BetterContinentsSettings settings, string name)
  {
    var path = Path.Combine(PresetsDir, BetterContinents.GetBCFile(name));
    if (File.Exists(path))
      File.Move(path, path + ".old-" + DateTime.Now.ToString("yyyy-dd-M-HH-mm-ss"));
    settings.Save(path);
    var pngPath = Path.Combine(PresetsDir, name + ".png");
    if (File.Exists(pngPath))
      File.Move(pngPath, pngPath + ".old-" + DateTime.Now.ToString("yyyy-dd-M-HH-mm-ss"));
    GameUtils.SaveMinimap(pngPath, 256);
  }
}
