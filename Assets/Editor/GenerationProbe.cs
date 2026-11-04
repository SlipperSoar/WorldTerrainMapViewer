using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using WorldTerrain;

/// <summary>
/// 端到端探针：在编辑器里真实跑一次生成 + 流式导出，测量内存并确认 PNG 落盘。
/// 仅供命令行验证使用（-executeMethod GenerationProbe.Run），不进运行时。
/// </summary>
public static class GenerationProbe
{
    private static int Width
    {
        get
        {
            string v = Environment.GetEnvironmentVariable("WTMV_PROBE_WIDTH");
            return int.TryParse(v, out int w) && w > 0 ? w : 4096;
        }
    }

    public static void Run()
    {
        try
        {
            string outDir = Path.Combine(Path.GetTempPath(), "wtmv_probe");
            Directory.CreateDirectory(outDir);

            var config = new WorldGenConfig
            {
                seed = 20260801,
                width = Width,
                height = Width / 2,
                minPlates = 4,
                maxPlates = 8,
                maxElevation = 10000f,
                minElevation = -10000f,
                waterCoverage = 0.6f,
                axialTilt = 23.44f,
                planetRadiusKm = PlanetProfile.ReferenceRadiusKm
            };

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long baseline = GC.GetTotalMemory(true);

            long peak = baseline;
            TerrainContext ctx = TerrainGenerator.GenerateContext(config,
                p => { long now = GC.GetTotalMemory(false); if (now > peak) peak = now; });

            long afterGen = GC.GetTotalMemory(false);
            Debug.Log($"[probe] 生成完成：托管 {MB(afterGen)}，峰值 {MB(peak)}");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long exportPeak = GC.GetTotalMemory(false);

            foreach (MapLayer layer in new[] { MapLayer.Terrain, MapLayer.Height, MapLayer.Plates })
            {
                string path = Path.Combine(outDir, layer + ".png");
                bool ok = MapExporter.WritePng(ctx, layer, path);
                long now = GC.GetTotalMemory(false);
                if (now > exportPeak) exportPeak = now;
                long size = File.Exists(path) ? new FileInfo(path).Length : 0;
                Debug.Log($"[probe] 导出 {layer}: ok={ok} {size / 1024} KB → {path}");
            }

            sw.Stop();
            Debug.Log($"[probe] 导出耗时 {sw.Elapsed.TotalSeconds:F1} s，导出期间托管峰值 {MB(exportPeak)}");
            Debug.Log($"[probe] Unity 已分配 {MB(UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong())}，" +
                      $"峰值托管 {MB(Math.Max(peak, exportPeak))}");
            Debug.Log("PROBE_OK");
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError("[probe] 失败：" + e);
            EditorApplication.Exit(1);
        }

        static string MB(long bytes) => $"{bytes / 1024.0 / 1024.0:F0} MB";
    }
}
