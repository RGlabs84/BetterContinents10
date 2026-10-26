// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).
//
// The game's water on a world Better Continents shapes (the fallback, AshlandsWater): no bubble layer. What goes in a renderer's property block, without
// the engine: the plan (which renderers are managed, which get colours, which get the black bubble texture) and the block filled as the plan says, through a
// recording stand-in for MaterialPropertyBlock.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BetterContinents;
using UnityEngine;

internal static partial class Program
{
  private sealed class RecordingBlock : AshlandsWater.IBlockWriter
  {
    public readonly Dictionary<int, Color> Colours = [];
    public readonly Dictionary<int, object?> Textures = [];
    public void SetColor(int id, Color value) => Colours[id] = value;
    public void SetTexture(int id, Texture? value) => Textures[id] = value;
  }

  private static void BubbleTests()
  {
    Section("bubbles: the game's water on a world BC shapes has none");
    if (!running) return;
    // zone water (far: false), the far sea (far: true); share -1: the game's ring says what the world says
    var vanillaZone = AshlandsWater.Plan(active: false, drawnByBc: false, far: false, share: -1f);
    var vanillaFar = AshlandsWater.Plan(false, false, true, -1f);
    Check(vanillaZone is { Managed: false, Colours: false, BlackBubbles: false } && vanillaFar is { Managed: false, Colours: true, BlackBubbles: false },
      "a vanilla world (BC off): nothing is managed, no bubbles are blacked; the far sea's block keeps writing the game's own colours, as before");
    var shapedZone = AshlandsWater.Plan(true, false, false, -1f);
    var shapedHot = AshlandsWater.Plan(true, false, false, 0.3f);
    var shapedFar = AshlandsWater.Plan(true, false, true, -1f);
    Check(shapedZone is { Managed: true, Colours: false, BlackBubbles: true }, "a BC world, a zone's water where the game's ring agrees with the world: the black bubble texture only");
    Check(shapedHot is { Managed: true, Colours: true, BlackBubbles: true }, "where they disagree: the colour blend and the black bubble texture");
    Check(shapedFar is { Managed: true, Colours: true, BlackBubbles: true }, "the far sea: its colours and the black bubble texture");
    Check(AshlandsWater.Plan(true, true, false, 0.3f) is { Managed: false, Colours: false, BlackBubbles: false } && AshlandsWater.Plan(true, true, true, -1f) is { Managed: false, Colours: true, BlackBubbles: false },
      "a renderer BC's own shader draws is let go: no colours of its own for a zone, no black texture");

    var plain = new[] { new Color(0.1f, 0.2f, 0.3f), new Color(0.2f, 0.3f, 0.4f), new Color(0.3f, 0.4f, 0.5f), new Color(0.4f, 0.5f, 0.6f), new Color(0.5f, 0.6f, 0.7f) };
    var hot = new[] { new Color(0.9f, 0.1f, 0.1f), new Color(0.8f, 0.2f, 0.1f), new Color(0.7f, 0.3f, 0.1f), new Color(0.6f, 0.4f, 0.1f), new Color(0.5f, 0.5f, 0.1f) };
    int[] plainIds = [10, 11, 12, 13, 14], hotIds = [20, 21, 22, 23, 24];
    const int bubbleId = 99;
    var black = (Texture)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
    var own = (Texture)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));

    var zone = new RecordingBlock();
    AshlandsWater.Fill(zone, shapedZone, -1f, plain, hot, plainIds, hotIds, bubbleId, black, own, restoreBubbles: false);
    Check(zone.Colours.Count == 0 && zone.Textures.Count == 1 && ReferenceEquals(zone.Textures[bubbleId], black),
      "a zone's block where the ring agrees: only _BubbleTexture, and it is the black texture (the shader's bubble term is then exactly 0)");

    var blended = new RecordingBlock();
    AshlandsWater.Fill(blended, shapedHot, 0.25f, plain, hot, plainIds, hotIds, bubbleId, black, own, false);
    bool blend = true;
    for (int i = 0; i < 5; i++)
    {
      var want = Color.Lerp(plain[i], hot[i], 0.25f);
      blend &= blended.Colours[plainIds[i]] == want && blended.Colours[hotIds[i]] == want;
    }
    Check(blend && blended.Colours.Count == 10 && ReferenceEquals(blended.Textures[bubbleId], black) && blended.Textures.Count == 1,
      "where they disagree: all ten colours are the one blend, and the black texture");

    var far = new RecordingBlock();
    AshlandsWater.Fill(far, shapedFar, -1f, plain, hot, plainIds, hotIds, bubbleId, black, own, false);
    bool asTheGame = true;
    for (int i = 0; i < 5; i++)
      asTheGame &= far.Colours[plainIds[i]] == plain[i] && far.Colours[hotIds[i]] == hot[i];
    Check(asTheGame && far.Colours.Count == 10 && ReferenceEquals(far.Textures[bubbleId], black), "the far sea where the ring agrees: the game's own colours, and the black texture");

    // let go: the far sea gets its material's own texture back if it was given the black one; a zone's block is dropped by the caller, not filled
    var let = new RecordingBlock();
    var letGo = AshlandsWater.Plan(true, true, true, -1f);
    AshlandsWater.Fill(let, letGo, -1f, plain, hot, plainIds, hotIds, bubbleId, black, own, restoreBubbles: true);
    Check(let.Textures.Count == 1 && ReferenceEquals(let.Textures[bubbleId], own) && let.Colours.Count == 10, "a far sea let go gets the material's own bubble texture back (and its own colours)");
    var untouched = new RecordingBlock();
    AshlandsWater.Fill(untouched, vanillaFar, -1f, plain, hot, plainIds, hotIds, bubbleId, black, own, restoreBubbles: false);
    Check(untouched.Textures.Count == 0 && untouched.Colours.Count == 10, "a far sea that was never given the black one is not given anything: the game's bubbles are the game's");
    var vanillaBlock = new RecordingBlock();
    AshlandsWater.Fill(vanillaBlock, vanillaZone, -1f, plain, hot, plainIds, hotIds, bubbleId, black, own, false);
    Check(vanillaBlock.Colours.Count == 0 && vanillaBlock.Textures.Count == 0, "and a vanilla world's zone water has nothing written for it at all");
  }
}
