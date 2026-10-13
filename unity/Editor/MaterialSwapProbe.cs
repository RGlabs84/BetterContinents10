// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).
// The material-keeps-values probe (WATER-SHADER-FACTS.md, section 11): does a Material keep the values of properties with the same name when its shader
// is changed (`material.shader = other`), including the values that the game writes without the shader declaring them (_depth, _WaterTime,
// _UseGlobalWind), and does it get its old values back when the old shader is set again? BC's water goes onto the game's materials this way
// (SeaDraw swaps the shader in place and puts the game's shader back). Two throwaway shaders stand in for Custom/Water and BetterContinents/Water.
// BuildBcWater.Build runs this and logs the answer as "[BCWater] probe ..." lines; it never fails the build. The editor's Material is the
// player's Material for this (both go through Material::SetShader), but the answer in the game itself is the one that counts.
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class MaterialSwapProbe
{
    private const string SourceA = "Shader \"BCProbe/A\" { Properties { _F (\"f\", Float) = 1 _R (\"r\", Range(0, 1)) = 0.5 _C (\"c\", Color) = (1, 1, 1, 1) _T (\"t\", 2D) = \"white\" {} _OnlyA (\"a\", Float) = 7 } SubShader { Pass { } } }";
    private const string SourceB = "Shader \"BCProbe/B\" { Properties { _F (\"f\", Float) = 2 _R (\"r\", Range(0, 1)) = 0.25 _C (\"c\", Color) = (0, 0, 0, 1) _T (\"t\", 2D) = \"black\" {} _OnlyB (\"b\", Float) = 9 } SubShader { Pass { } } }";

    public static string Run()
    {
        var sb = new StringBuilder();
        // ShaderUtil.CreateShaderAsset is not public in every editor version: found by reflection, so a missing one costs this probe and not the build
        var create = typeof(ShaderUtil).GetMethod("CreateShaderAsset", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(bool) }, null);
        if (create == null)
        {
            Debug.Log("[BCWater] probe material keeps values: not run (this editor has no ShaderUtil.CreateShaderAsset(string, bool))");
            return "not run";
        }
        var a = (Shader)create.Invoke(null, new object[] { SourceA, false });
        var b = (Shader)create.Invoke(null, new object[] { SourceB, false });
        var tex = new Texture2D(2, 2) { name = "probe tex" };
        var mat = new Material(a);
        mat.SetFloat("_F", 42f);
        mat.SetFloat("_R", 0.9f);
        mat.SetColor("_C", new Color(0.1f, 0.2f, 0.3f, 0.4f));
        mat.SetTexture("_T", tex);
        mat.SetFloat("_OnlyA", 3f);
        mat.SetFloatArray("_depth", new[] { 0.1f, 0.2f, 0.3f, 0.4f });
        mat.SetFloat("_WaterTime", 12.5f);
        mat.SetFloat("_UseGlobalWind", 1f);
        mat.renderQueue = 2900;
        mat.shader = b;
        sb.AppendLine("after shader A -> B (the properties B also has, then the ones only A has, then undeclared ones):");
        sb.AppendLine($"  _F {mat.GetFloat("_F")} (set 42; B's default 2), _R {mat.GetFloat("_R")} (set 0.9; default 0.25), _C {mat.GetColor("_C")} (set 0.1 0.2 0.3 0.4), _T {(mat.GetTexture("_T") == tex ? "kept" : "reset to " + mat.GetTexture("_T"))}");
        sb.AppendLine($"  _OnlyA declared by B: {mat.HasProperty("_OnlyA")}; _OnlyB {mat.GetFloat("_OnlyB")} (default 9)");
        var depth = mat.GetFloatArray("_depth");
        sb.AppendLine($"  _depth (undeclared) {(depth == null ? "null" : string.Join(",", depth))}, _WaterTime (undeclared) {mat.GetFloat("_WaterTime")}, _UseGlobalWind (undeclared) {mat.GetFloat("_UseGlobalWind")}; renderQueue {mat.renderQueue}");
        mat.shader = a;
        sb.AppendLine("after shader B -> A again:");
        sb.AppendLine($"  _F {mat.GetFloat("_F")}, _R {mat.GetFloat("_R")}, _C {mat.GetColor("_C")}, _T {(mat.GetTexture("_T") == tex ? "kept" : "lost")}, _OnlyA {mat.GetFloat("_OnlyA")} (set 3; default 7), _depth {(mat.GetFloatArray("_depth") == null ? "null" : string.Join(",", mat.GetFloatArray("_depth")))}");
        var result = sb.ToString();
        Debug.Log("[BCWater] probe material keeps values:\n" + result);
        Object.DestroyImmediate(mat);
        Object.DestroyImmediate(tex);
        Object.DestroyImmediate(a);
        Object.DestroyImmediate(b);
        return result;
    }
}
