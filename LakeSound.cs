// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).

using System;
using UnityEngine;

namespace BetterContinents;

// A QUIET WATER SOUND NEAR LAKES [Tarn: Water.cs, LakeSound]: the game's own ocean loop (AudioMan.m_oceanAudio) through its ambient mixer, at the nearest lake tile, rising
// with the wind like the sea's. Cosmetic: any failure turns it off for the session. A client only (LakeStreamer.Tick); the volume is the live setting Lake Sound.
internal static class LakeSound
{
  private static AudioSource? source;
  private static float timer;
  private static Vector3 target;
  private static bool near, dead;

  internal static void Update(float dt)
  {
    if (dead)
      return;
    try
    {
      var audio = AudioMan.instance;
      float volume = LakeSettings.SoundVolume;
      if (audio == null || LakeWater.Volumes.Count == 0 || volume <= 0f)
      {
        if (source != null)
          source.volume = Mathf.MoveTowards(source.volume, 0f, 0.2f * dt);
        return;
      }
      if (source == null)
      {
        var go = new GameObject("BCLakeAmbient");
        UnityEngine.Object.DontDestroyOnLoad(go);
        source = go.AddComponent<AudioSource>();
        source.clip = audio.m_oceanAudio;
        source.outputAudioMixerGroup = audio.m_ambientMixer;
        source.loop = true;
        source.spatialBlend = 0.8f;
        source.minDistance = 12f;
        source.maxDistance = 70f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.dopplerLevel = 0f;
        source.volume = 0f;
        source.Play();
      }
      timer += dt;
      if (timer > 1f)
      {
        timer = 0f;
        var ear = LakeStreamer.ListenerPosition(out var at) ? at : Vector3.zero;
        if (Camera.main != null)
          ear = Camera.main.transform.position;
        float best = float.MaxValue;
        foreach (var v in LakeWater.Volumes.Values)
        {
          if (v.Water == null || v.Box)
            continue;
          var p = new Vector3(Mathf.Clamp(ear.x, v.Centre.x - 32f, v.Centre.x + 32f), v.Centre.y, Mathf.Clamp(ear.z, v.Centre.z - 32f, v.Centre.z + 32f));
          float d = (p - ear).sqrMagnitude;
          if (d < best)
          {
            best = d;
            target = p;
          }
        }
        near = best < 80f * 80f;
      }
      float wind = EnvMan.instance != null ? EnvMan.instance.GetWindIntensity() : 0.5f;
      float goal = near ? volume * Mathf.Lerp(0.4f, 1f, wind) : 0f;
      source.volume = Mathf.MoveTowards(source.volume, goal, 0.15f * dt);
      source.transform.position = Vector3.Lerp(source.transform.position, target, 0.1f);
    }
    catch (Exception e)
    {
      dead = true;
      BetterContinents.LogWarning($"Lakes: the lake sound is off ({e.GetType().Name}: {e.Message})");
    }
  }

  /// <summary>The world was left: the sound fades out and stays where it is (the object lives for the session).</summary>
  internal static void Reset()
  {
    near = false;
    timer = 1f;
  }
}
