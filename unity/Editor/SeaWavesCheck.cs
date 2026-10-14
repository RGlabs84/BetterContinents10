// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).
// The wave check of the water bundle's build (BuildBcWater.Build runs it): the HLSL the water shader runs, evaluated on the GPU by SeaWavesCheck.compute,
// against the C# side. It needs a graphics device with compute shaders and fails the build when the two disagree.
//
//   1. Waves: BCSeaWaveOne (SeaWaves.hlsl) for 4,096 points x 3 values of W x 3 winds x 2 times (x 2 waveFactors, x 2 extents) against the game's CalcWave
//      (WaterVolume.cs:186-230) with BC's weights, computed here in double precision (ReferenceWave). The allowed difference is the larger of 1e-4 m
//      and an error budget worked out per case from float32's own resolution at that position and time: the water shader and the C# side both
//      compute in float32, and a wave's argument is 100 to 50,000 radians, so rounding (or a fused multiply-add on the GPU) alone moves the height by
//      more than 1e-4 m far from the origin or late in the day. The near set (within 256 m, early times) is where the budget is below 1e-4 m.
//   2. The field: BCSeaWaves / BCSeaCalmMetres (SeaField.hlsl) against the bilinear read of SeaField.cs's layout (ReferenceCalm), on a random field,
//      inside the grid and outside it (the border texels apply). Allowed: 2e-4 m on the calm distance (float32 at 255 m), 1e-6 on W.
//   3. If the mod's own SeaWaves type is in this project (BC_SEA_COPY_CS=1 in tools/build-water.sh copies SeaWaves.cs into Assets/Editor/Reference/),
//      SeaWaves.One is called by name and compared with the reference too (1e-5 m).
//
// The reference is a transcription of the game's code and of the sea field's layout (SeaField.hlsl's header), so it checks the GPU against the specification; the C#
// suites (tools/water-tests) check SeaWaves.cs and SeaField.cs against the same transcriptions.
using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class SeaWavesCheck
{
    private const string ComputePath = "Assets/BCWater/SeaWavesCheck.compute";

    private struct WaveCase
    {
        public Vector2 xz;
        public float depth;
        public float time;
        public Vector4 wind;
        public float waveFactor;
        public float w;
    }

    // WaterVolume's ten waves: speed, length, height (before the weight), sharpness, and the unnormalised direction (wave 1 follows the wind)
    private static readonly double[] Speed = { 10, 14.123, 22.312, 31.42, 35.42, 38.1223, 41.1223, 51.5123, 54.2, 56.123 };
    private static readonly double[] Length = { 0.04, 0.08, 0.1, 0.2, 0.4, 1, 1.2, 1.3, 1.3, 1.5 };
    private static readonly double[] Height = { 8, 6, 4, 2, 1, 0.8, 0.6, 0.4, 0.3, 0.2 };
    private static readonly double[] Sharp = { 0.5, 0.5, 0.5, 0.5, 0.5, 0.7, 0.8, 0.9, 0.9, 0.9 };
    private static readonly double[,] Dir =
    {
        { 1.0312, 0.312 }, { 1.0312, 0.312 }, { -0.123, 1.12 }, { 0.423, 0.124 }, { 0.123, -0.64 },
        { -0.523, -0.64 }, { 0.223, 0.74 }, { 0.923, -0.24 }, { -0.323, 0.44 }, { 0.5312, -0.812 },
    };

    // the greatest difference found, for the build log
    public static string Summary = "";

    public static void Run()
    {
        if (!SystemInfo.supportsComputeShaders)
            throw new Exception("[BCWater] the wave check needs a graphics device with compute shaders");
        var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
        if (cs == null)
            throw new Exception("[BCWater] no compute shader at " + ComputePath);
        var log = new System.Text.StringBuilder();
        CheckWaves(cs, log);
        CheckField(cs, log);
        Summary = log.ToString();
        Debug.Log("[BCWater] wave check passed:\n" + Summary);
    }

    // ---- 1. the waves ----

    private static void CheckWaves(ComputeShader cs, System.Text.StringBuilder log)
    {
        var rng = new Rng(20261013u);
        var cases = new System.Collections.Generic.List<WaveCase>();
        float[] ws = { 0f, 0.37f, 1f };
        // the winds as the game keeps them: direction (x, z), intensity w (the lowest 0.05, a storm 1)
        Vector4[] winds = { new Vector4(1f, 0f, 0f, 0.05f), new Vector4(0.7071f, 0f, 0.7071f, 0.6f), new Vector4(-0.2f, 0f, 0.98f, 1f) };
        float[] times = { 12.5f, 77.7f };
        foreach (float extent in new[] { 256f, 32764f })
        {
            for (int i = 0; i < 4096; i++)
            {
                var xz = new Vector2((rng.Next01() * 2f - 1f) * extent, (rng.Next01() * 2f - 1f) * extent);
                float depth = rng.Next01();
                foreach (float w in ws)
                    foreach (var wind in winds)
                        foreach (float time in times)
                            cases.Add(new WaveCase { xz = xz, depth = depth, time = time, wind = wind, waveFactor = (i & 1) == 0 ? 1f : 0.5f, w = w });
            }
        }
        float[] gpu = Dispatch(cs, "WaveCases", cases.ToArray(), 40);
        var real = RealOne();
        double worstNear = 0, worstFar = 0, worstRatio = 0, worstReal = 0;
        for (int i = 0; i < cases.Count; i++)
        {
            var c = cases[i];
            double reference = ReferenceWave(c, out double budget);
            double diff = Math.Abs(gpu[i] - reference);
            double allowed = Math.Max(1e-4, budget);
            bool near = Math.Max(Math.Abs(c.xz.x), Math.Abs(c.xz.y)) <= 256.0;
            if (near) worstNear = Math.Max(worstNear, diff); else worstFar = Math.Max(worstFar, diff);
            worstRatio = Math.Max(worstRatio, diff / allowed);
            if (diff > allowed || double.IsNaN(gpu[i]))
                throw new Exception($"[BCWater] wave check failed: case {i} at ({c.xz.x}, {c.xz.y}) depth {c.depth} time {c.time} wind {c.wind} W {c.w} waveFactor {c.waveFactor}: GPU {gpu[i]:R}, reference {reference:R}, difference {diff:E3} > allowed {allowed:E3}");
            if (real != null)
            {
                double cpu = real(c);
                worstReal = Math.Max(worstReal, Math.Abs(cpu - reference));
                if (Math.Abs(cpu - reference) > 1e-5 + budget * 0.1)
                    throw new Exception($"[BCWater] wave check failed: SeaWaves.One differs from the reference by {Math.Abs(cpu - reference):E3} at case {i}");
            }
        }
        log.AppendLine($"waves: {cases.Count} cases; greatest GPU-reference difference {worstNear:E2} m within 256 m (target 1e-4), {worstFar:E2} m out to 32,764 m (within the float32 budget; worst use of the budget {worstRatio:P0})");
        log.AppendLine(real == null ? "waves: the mod's SeaWaves.cs is not in the project: the GPU was checked against the transcription of CalcWave only" : $"waves: SeaWaves.One against the reference: greatest difference {worstReal:E2} m");
    }

    // WaterVolume.CalcWave for one wind, in double, with BC's weights (decision 11): waves 1-5 x W, wave 6 x max(W, 0.1), waves 7-10 x max(W, 0.1) x waveFactor.
    // budget: the allowed difference for float32 evaluation (rounding of the positions and arguments, two sine/cosine evaluations per wave).
    private static double ReferenceWave(WaveCase c, out double budget)
    {
        double dx0 = c.wind.x, dz0 = c.wind.z;
        double len0 = Math.Sqrt(dx0 * dx0 + dz0 * dz0);
        if (len0 > 1e-5) { dx0 /= len0; dz0 /= len0; } else { dx0 = 0; dz0 = 0; }
        double num = (double)c.wind.w * Math.Min(Math.Max((double)c.depth, 0.0), 1.0);
        double t = c.time / 20.0;
        double low = Math.Max(c.w, 0.1);
        double sum = 0, err = 0;
        double posUlp = Ulp(Math.Max(Math.Abs(c.xz.x), Math.Abs(c.xz.y)));
        for (int i = 0; i < 10; i++)
        {
            double dx, dz;
            if (i == 0) { dx = dx0; dz = dz0; }
            else
            {
                double m = Math.Sqrt(Dir[i, 0] * Dir[i, 0] + Dir[i, 1] * Dir[i, 1]);
                dx = Dir[i, 0] / m; dz = Dir[i, 1] / m;
            }
            double tx = -dz, tz = dx;
            double weight = i < 5 ? c.w : i == 5 ? low : low * c.waveFactor;
            double height = Height[i] * weight;
            double vx = -(c.xz.y * dx + c.xz.x * tx), vy = -(c.xz.y * dz + c.xz.x * tz);
            double n = t * Speed[i];
            double a1 = n + vy * Length[i];
            double a2 = n * 0.123 + vx * 0.13123 * Length[i];
            sum += (Troch(a1, Sharp[i]) * Troch(a2, Sharp[i]) - 0.2) * height;
            // float32 evaluation error of the two arguments: the position terms (4 roundings of a product or sum of x and z), the products with the
            // wavelength and the time, the sum itself; plus the sine and cosine of the GPU (about 4e-6 each, taken as 2e-5 in all)
            double e1 = 4.0 * Ulp(Math.Abs(a1)) + 4.0 * posUlp * Length[i] + 4.0 * Ulp(Math.Abs(n));
            double e2 = 4.0 * Ulp(Math.Abs(a2)) + 4.0 * posUlp * 0.13123 * Length[i] + 4.0 * Ulp(Math.Abs(n * 0.123));
            err += Math.Abs(height) * (e1 + e2 + 2e-5);
        }
        budget = Math.Abs(num) * err + 1e-6;
        return sum * num;
    }

    private static double Troch(double x, double k) => Math.Sin(x - Math.Cos(x) * k) * 0.5 + 0.5;

    // the spacing of float32 numbers around x
    private static double Ulp(double x)
    {
        if (x == 0) return Math.Pow(2, -149);
        return Math.Pow(2, Math.Floor(Math.Log(x, 2)) - 23);
    }

    // ---- 2. the field ----

    private static void CheckField(ComputeShader cs, System.Text.StringBuilder log)
    {
        const int n = 64;
        const float spacing = 16f, originX = -512f, originZ = -480f;
        var rng = new Rng(7u);
        var data = new byte[n * n * 2];
        var calm = new byte[n * n];
        for (int i = 0; i < n * n; i++)
        {
            // R = hot + 128, G = calm: blocks of calm sea with ramps, so every blend weight and some borders occur
            data[2 * i] = (byte)(1 + (int)(rng.Next01() * 254f));
            calm[i] = (byte)(rng.Next01() < 0.3f ? 0 : (int)(rng.Next01() * 255.99f));
            data[2 * i + 1] = calm[i];
        }
        var tex = new Texture2D(n, n, TextureFormat.RG16, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        tex.SetPixelData(data, 0);
        tex.Apply(false, false);

        int count = 4096;
        var points = new Vector2[count];
        for (int i = 0; i < count; i++)
            points[i] = new Vector2(originX - 100f + rng.Next01() * (n * spacing + 200f), originZ - 100f + rng.Next01() * (n * spacing + 200f));

        int kernel = cs.FindKernel("FieldCases");
        var pBuf = new ComputeBuffer(count, 8);
        var calmBuf = new ComputeBuffer(count, 4);
        var wBuf = new ComputeBuffer(count, 4);
        try
        {
            pBuf.SetData(points);
            cs.SetInt("_Count", count);
            cs.SetTexture(kernel, "_BCSeaField", tex);
            cs.SetVector("_BCSeaRect", new Vector4(originX, originZ, 1f / spacing, n));
            cs.SetBuffer(kernel, "_Points", pBuf);
            cs.SetBuffer(kernel, "_OutCalm", calmBuf);
            cs.SetBuffer(kernel, "_OutW", wBuf);
            cs.Dispatch(kernel, (count + 63) / 64, 1, 1);
            var gpuCalm = new float[count];
            var gpuW = new float[count];
            calmBuf.GetData(gpuCalm);
            wBuf.GetData(gpuW);
            double worstCalm = 0, worstW = 0;
            for (int i = 0; i < count; i++)
            {
                double refCalm = ReferenceCalm(calm, n, spacing, originX, originZ, points[i].x, points[i].y);
                double refW = 1.0 - Math.Min(Math.Max(refCalm * 0.005, 0.0), 1.0);
                worstCalm = Math.Max(worstCalm, Math.Abs(gpuCalm[i] - refCalm));
                worstW = Math.Max(worstW, Math.Abs(gpuW[i] - refW));
                // The reference is exact (double); the GPU rounds the texel coordinate t = (x - origin) * invSpacing - 0.5 in float32, so its error is a couple
                // of ulps of t times the steepest slope (this test field is random bytes: up to 255 per texel). That is the budget, plus 1e-5 m for the rest.
                double tBig = Math.Max(Math.Abs((points[i].x - originX) / spacing), Math.Abs((points[i].y - originZ) / spacing)) + 1.0;
                double calmBudget = 255.0 * tBig * 2.4e-7 + 1e-5;
                double wBudget = calmBudget * 0.005 + 1e-7;
                if (Math.Abs(gpuCalm[i] - refCalm) > calmBudget || Math.Abs(gpuW[i] - refW) > wBudget || double.IsNaN(gpuW[i]))
                    throw new Exception($"[BCWater] field check failed at ({points[i].x}, {points[i].y}): GPU calm {gpuCalm[i]:R} W {gpuW[i]:R}, reference calm {refCalm:R} W {refW:R}");
            }
            log.AppendLine($"field: {count} points on a {n} x {n} field (inside and beyond its edge); greatest difference {worstCalm:E2} m of calm distance, {worstW:E2} in W (allowed: a per-case float32 budget, about 255 x t x 2.4e-7 m)");
            log.AppendLine("field: the GPU was checked against the transcription of SeaField.cs's layout (the C# suites check SeaField.Waves against the same transcription)");
        }
        finally
        {
            pBuf.Release(); calmBuf.Release(); wBuf.Release();
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }

    // the calm distance at (x, z) by SeaField.CalmAt's contract: bilinear between the four nearest texel centres, x first and then z, the border texels beyond the grid
    private static double ReferenceCalm(byte[] calm, int n, float spacing, float originX, float originZ, float x, float z)
    {
        double tx = Math.Min(Math.Max((x - originX) * (1.0 / spacing) - 0.5, -1.0), n);
        double tz = Math.Min(Math.Max((z - originZ) * (1.0 / spacing) - 0.5, -1.0), n);
        double fx = Math.Floor(tx), fz = Math.Floor(tz);
        double ax = tx - fx, az = tz - fz;
        int x0 = Math.Min(Math.Max((int)fx, 0), n - 1), x1 = Math.Min(Math.Max((int)fx + 1, 0), n - 1);
        int z0 = Math.Min(Math.Max((int)fz, 0), n - 1), z1 = Math.Min(Math.Max((int)fz + 1, 0), n - 1);
        double c00 = calm[z0 * n + x0], c10 = calm[z0 * n + x1], c01 = calm[z1 * n + x0], c11 = calm[z1 * n + x1];
        double a = c00 + (c10 - c00) * ax, b = c01 + (c11 - c01) * ax;
        return a + (b - a) * az;
    }

    // ---- 3. the mod's own types, when they are in the project ----

    private static Func<WaveCase, double> RealOne()
    {
        var type = FindType("SeaWaves");
        var m = type?.GetMethod("One", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (m == null) return null;
        return c => Convert.ToDouble(m.Invoke(null, new object[] { c.xz.x, c.xz.y, c.depth, c.wind, c.time, c.waveFactor, c.w }));
    }

    private static Type FindType(string name)
    {
        return AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } }).FirstOrDefault(t => t.Name == name && t.Namespace != "UnityEngine");
    }

    // ---- GPU plumbing ----

    private static float[] Dispatch<T>(ComputeShader cs, string kernelName, T[] items, int stride) where T : struct
    {
        int kernel = cs.FindKernel(kernelName);
        var input = new ComputeBuffer(items.Length, stride);
        var output = new ComputeBuffer(items.Length, 4);
        try
        {
            input.SetData(items);
            cs.SetInt("_Count", items.Length);
            cs.SetBuffer(kernel, "_Cases", input);
            cs.SetBuffer(kernel, "_Out", output);
            cs.Dispatch(kernel, (items.Length + 63) / 64, 1, 1);
            var result = new float[items.Length];
            output.GetData(result);
            return result;
        }
        finally
        {
            input.Release();
            output.Release();
        }
    }

    // a small deterministic generator (the same cases on every machine)
    private sealed class Rng
    {
        private uint _s;
        public Rng(uint seed) { _s = seed == 0 ? 1u : seed; }
        public float Next01()
        {
            _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5;
            return (_s >> 8) / 16777216f;
        }
    }
}
