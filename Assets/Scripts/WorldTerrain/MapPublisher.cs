using System;
using System.IO;
using UnityEngine;

namespace WorldTerrain
{
    /// <summary>
    /// 地图的**落盘策略**：分「保存配方」与「导出整包」两件事。
    ///
    /// - <see cref="SaveRecipe"/>：生成完只写 <c>_meta.json</c>（含整型配方）。
    ///   地图列表靠它列出条目；首次选中时按配方重建（生成是确定性的）。
    /// - <see cref="ExportFull"/>：用户点「导出」时才写全套 ——
    ///   地形图 / 高度图 / 板块图 / 人文 JSON / meta。
    ///
    /// 拆开的理由：生成一张 8192 地图要写三张几十 MB 的 PNG，
    /// 而多数时候用户只是要看一眼；配方 JSON 只有几百字节。
    /// </summary>
    public static class MapPublisher
    {
        /// <summary>只写配方（meta）。生成流程调用。</summary>
        public static bool SaveRecipe(TerrainContext ctx, MapRecipe recipe, string baseName,
            string generatedAt = null)
        {
            if (ctx == null || string.IsNullOrEmpty(baseName))
                return false;

            try
            {
                MapMetaData.Save(baseName, BuildMeta(ctx, recipe, generatedAt));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"MapPublisher: 写配方失败 {baseName}：{e.Message}");
                return false;
            }
        }

        /// <summary>导出整包（三张 PNG + 人文 + meta）。用户显式触发。</summary>
        public static bool ExportFull(TerrainContext ctx, MapRecipe recipe, string baseName,
            MapExportOptions options, string generatedAt = null)
        {
            if (ctx == null || string.IsNullOrEmpty(baseName))
                return false;

            MapStore.Paths paths = MapStore.GetPaths(baseName);

            try
            {
                Texture2D terrain = MapExporter.ExportLayer(ctx, MapLayer.Terrain, paths.terrain);
                if (terrain == null)
                {
                    Debug.LogError($"MapPublisher: 地形图导出失败 {baseName}");
                    return false;
                }
                UnityEngine.Object.Destroy(terrain);

                if (options.height)
                {
                    Texture2D t = MapExporter.ExportLayer(ctx, MapLayer.Height, paths.height);
                    if (t != null) UnityEngine.Object.Destroy(t);
                }

                if (options.plates)
                {
                    Texture2D t = MapExporter.ExportLayer(ctx, MapLayer.Plates, paths.plates);
                    if (t != null) UnityEngine.Object.Destroy(t);
                }

                if (options.human && ctx.Human != null)
                    HumanGeographyData.Save(baseName, ctx.Human);

                MapMetaData.Save(baseName, BuildMeta(ctx, recipe, generatedAt));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"MapPublisher: 导出失败 {baseName}：{e.Message}");
                return false;
            }
        }

        /// <summary>组装 meta（配方 + 展示属性）。</summary>
        private static MapMetaData BuildMeta(TerrainContext ctx, MapRecipe recipe, string generatedAt)
        {
            return new MapMetaData
            {
                version = 2,
                axialTilt = recipe.AxialTilt,
                waterCoverage = recipe.WaterCoverage,
                seed = recipe.seed,
                planetRadiusKm = recipe.PlanetRadiusKm,
                rotationRetrograde = recipe.retrograde,
                pngNorthUp = true,
                recipeVersion = recipe.version,
                waterPermille = recipe.waterPermille,
                tiltMilliDeg = recipe.tiltMilliDeg,
                radiusMeters = recipe.radiusMeters,
                mapWidth = ctx.Width,
                mapHeight = ctx.Height,
                generatedAt = string.IsNullOrEmpty(generatedAt)
                    ? DateTime.Now.ToString("o")
                    : generatedAt,
                human = HumanMapSummary.From(ctx.Human)
            };
        }
    }
}
