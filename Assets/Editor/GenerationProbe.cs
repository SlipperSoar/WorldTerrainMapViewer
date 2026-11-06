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

            foreach (MapLayer layer in new[] { MapLayer.Terrain, MapLayer.Height, MapLayer.Plates,
                                              MapLayer.Surface, MapLayer.Detail })
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
            VerifyPublishFlow();
            VerifyRecipeDeterminism();

            Debug.Log("PROBE_OK");
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError("[probe] 失败：" + e);
            EditorApplication.Exit(1);
        }


        /// <summary>
        /// 验证「只存配方 / 导出整包」这条链路：
        ///   1. SaveRecipe 后应当只有 _meta.json，没有 PNG
        ///   2. ExportFull 后七件套齐全（地形/高度/板块/地表/细节/人文/meta）
        ///   3. meta 里的渲染参数与按高度场重算的一致（同一配方两次生成逐位一致）
        /// </summary>
        static void VerifyPublishFlow()
        {
            var recipe = MapRecipe.FromParameters(20260801, 1024, 0.6f, 23.44f, 6371f, false);
            TerrainContext ctx = TerrainGenerator.GenerateContext(recipe.ToConfig());

            string name = "probe_publish";
            MapStore.Paths paths = MapStore.GetPaths(name);

            string[] allFiles = { paths.terrain, paths.height, paths.plates,
                                  paths.surface, paths.detail, paths.human, paths.meta };
            foreach (string f in allFiles)
                if (File.Exists(f)) File.Delete(f);

            bool saved = MapPublisher.SaveRecipe(ctx, recipe, name);
            bool metaOnly =
                File.Exists(paths.meta) &&
                !File.Exists(paths.terrain) && !File.Exists(paths.height) && !File.Exists(paths.plates) &&
                !File.Exists(paths.surface) && !File.Exists(paths.detail);
            Debug.Log($"[probe] SaveRecipe={saved} 只有 meta={metaOnly}");

            bool exported = MapPublisher.ExportFull(ctx, recipe, name, MapExportOptions.All);
            bool full = true;
            foreach (string f in allFiles)
                if (!File.Exists(f)) full = false;
            long size = File.Exists(paths.terrain) ? new FileInfo(paths.terrain).Length : 0;
            Debug.Log($"[probe] ExportFull={exported} 七件套齐全={full} 地形图={size / 1024} KB");

            // 渲染参数自检：meta 写出的值 == 由高度场 + 倾角重算的值
            var meta = MapMetaData.Load(name);
            var expected = SurfaceDataLayers.ComputeRenderParams(ctx);
            bool paramsOk = meta != null && meta.version >= 3 &&
                Math.Abs(meta.seaLevelMeters - expected.seaLevelMeters) < 1e-3f &&
                Math.Abs(meta.snowLineEquatorMeters - expected.snowLineEquatorMeters) < 1e-2f &&
                Math.Abs(meta.tideAmplitudeMeters - expected.tideAmplitudeMeters) < 1e-3f;
            Debug.Log($"[probe] 渲染参数 meta v{(meta != null ? meta.version : -1)} " +
                      $"雪线(赤道)={(meta != null ? meta.snowLineEquatorMeters : -1f):F0}m " +
                      $"潮汐振幅={(meta != null ? meta.tideAmplitudeMeters : -1f):F1}m 一致={paramsOk}");

            // G 通道水体分类自检：从导出的 surface PNG 解码，确认全是合法分类值
            // （间隔编码：(int)WaterClass*16），且至少出现陆地与一种非陆地类。
            VerifyWaterClasses(ctx, paths.surface);

            // 砍老地图自检：无配方 meta 必须被 MapMetaData.Load 拒绝
            VerifyOldMetaRejected(name);

            // 清理，避免污染 StreamingAssets
            foreach (string f in allFiles)
                if (File.Exists(f)) File.Delete(f);
        }

        /// <summary>验证 _surface.png 的 G 通道是合法的水体分类值（间隔编码）。</summary>
        static void VerifyWaterClasses(TerrainContext ctx, string surfacePath)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (!tex.LoadImage(File.ReadAllBytes(surfacePath)))
            {
                UnityEngine.Object.DestroyImmediate(tex);
                Debug.LogError("[probe] surface PNG 解码失败");
                return;
            }

            Color32[] px = tex.GetPixels32();
            int stride = SurfaceContract.WaterClassStride;
            bool allValid = true, hasLand = false, hasWater = false;
            foreach (Color32 c in px)
            {
                int g = c.g;
                if (g % stride != 0 || g > (int)WaterClass.SnowPeak * stride)
                {
                    allValid = false;
                    break;
                }
                if (g == 0) hasLand = true; else hasWater = true;
            }
            UnityEngine.Object.DestroyImmediate(tex);
            Debug.Log($"[probe] G 通道水体分类：合法={allValid} 含陆地={hasLand} 含水/冰={hasWater} " +
                      $"(stride={stride}, 类别数={(int)WaterClass.SnowPeak + 1})");
        }

        /// <summary>验证 V3.2 起无配方 / 旧版 meta 不再被当作地图载入。</summary>
        static void VerifyOldMetaRejected(string baseName)
        {
            string metaPath = MapMetaData.GetPath(baseName);
            string backup = File.Exists(metaPath) ? File.ReadAllText(metaPath) : null;
            try
            {
                // 写一份「没有配方」的假 meta，Load 必须返回 null
                File.WriteAllText(metaPath, "{\"version\":3,\"recipeVersion\":0,\"seed\":1}");
                bool rejected = MapMetaData.Load(baseName) == null;
                Debug.Log($"[probe] 无配方 meta 被拒绝={rejected}");
            }
            finally
            {
                if (backup != null) File.WriteAllText(metaPath, backup);
            }
        }

        /// <summary>
        /// 同一份整型配方生成两次，高度场必须逐位相同 —— 这是「重建 = 首次生成」的依据。
        /// </summary>
        static void VerifyRecipeDeterminism()
        {
            var recipe = MapRecipe.FromParameters(20260801, 1024, 0.6f, 23.44f, 6371f, false);

            float[] a = TerrainGenerator.GenerateContext(recipe.ToConfig()).HeightField;
            float[] b = TerrainGenerator.GenerateContext(recipe.ToConfig()).HeightField;

            bool same = a != null && b != null && a.Length == b.Length;
            long firstDiff = -1;
            if (same)
            {
                for (int i = 0; i < a.Length; i++)
                {
                    if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i]))
                    {
                        same = false;
                        firstDiff = i;
                        break;
                    }
                }
            }

            Debug.Log($"[probe] 配方重建逐位一致={same}（首个差异格 {(firstDiff < 0 ? "无" : firstDiff.ToString())}）");
        }

        static string MB(long bytes) => $"{bytes / 1024.0 / 1024.0:F0} MB";
    }
}
