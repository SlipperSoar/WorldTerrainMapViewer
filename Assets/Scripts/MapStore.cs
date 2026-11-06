using System;
using System.IO;
using UnityEngine;
using WorldTerrain;

/// <summary>
/// <see cref="MapData"/> 的读写实现：把「地图契约」落到 StreamingAssets 下的一组文件。
///
/// 文件布局（与历史版本保持一致，旧地图仍可读）：
///   &lt;名字&gt;.png           彩色地形图
///   &lt;名字&gt;_height.png    灰度高度图
///   &lt;名字&gt;_plates.png    板块分界叠加图
///   &lt;名字&gt;_surface.png   RGBA 地表数据图（3.1 起，步骤 1 数据契约）
///   &lt;名字&gt;_detail.png    RGBA 细节图（3.1 起，步骤 1 数据契约）
///   &lt;名字&gt;_meta.json     地图属性
///   &lt;名字&gt;_human.json    人文 + 气候（湿度场）
///
/// 生成器只调 <see cref="Save"/>，查看器只调 <see cref="Load"/>，
/// 两边都不再自己拼文件名或直接读 JSON。
/// </summary>
public static class MapStore
{
    /// <summary>单个地图名对应的文件路径集合。</summary>
    public struct Paths
    {
        public string terrain;
        public string height;
        public string plates;
        public string surface;
        public string detail;
        public string meta;
        public string human;

        public bool HasTerrain => File.Exists(terrain);
        public bool HasHeight => File.Exists(height);
        public bool HasPlates => File.Exists(plates);
        public bool HasSurface => File.Exists(surface);
        public bool HasDetail => File.Exists(detail);
        public bool HasMeta => File.Exists(meta);
        public bool HasHuman => File.Exists(human);
    }

    /// <summary>取某个地图名对应的全部路径（不判断是否存在）。</summary>
    public static Paths GetPaths(string baseName)
    {
        string root = Application.streamingAssetsPath;
        return new Paths
        {
            terrain = Path.Combine(root, baseName + ".png"),
            height = Path.Combine(root, baseName + "_height.png"),
            plates = Path.Combine(root, baseName + "_plates.png"),
            surface = Path.Combine(root, baseName + "_surface.png"),
            detail = Path.Combine(root, baseName + "_detail.png"),
            meta = Path.Combine(root, baseName + "_meta.json"),
            human = Path.Combine(root, baseName + "_human.json")
        };
    }

    /// <summary>
    /// 读取一张地图。图像缺失时对应字段为 null（<see cref="MapData.Validate"/> 会说明缺什么）。
    /// 人文文件损坏不影响地图本体。
    /// </summary>
    public static MapData Load(string baseName)
    {
        if (string.IsNullOrEmpty(baseName))
            return null;

        Paths paths = GetPaths(baseName);

        // 2.9.0 起生成只写配方（meta），不写 PNG —— 没有地形图但有**有效 meta**时
        // 返回「只有配方」的契约，由查看器按配方重建。
        // V3.2.0 起：MapMetaData.Load 已过滤掉无配方/旧版 meta，因此这里拿到的一定是
        // 带整型配方的有效地图；没有有效 meta 又没有地形图的，一律拒绝（老地图）。
        if (!paths.HasTerrain)
        {
            MapMetaData onlyMeta = MapMetaData.Load(baseName);
            if (onlyMeta == null)
            {
                Debug.LogWarning($"MapStore: 无有效配方、也找不到地形图 {paths.terrain}（V3.2 起不再载入无配方老地图）");
                return null;
            }

            var recipeOnly = new MapData
            {
                baseName = baseName,
                hasRecipe = true,
                recipe = onlyMeta.ToRecipe(),
                seed = onlyMeta.seed,
                axialTilt = onlyMeta.axialTilt,
                waterCoverage = onlyMeta.waterCoverage,
                planetRadiusKm = onlyMeta.planetRadiusKm,
                rotationRetrograde = onlyMeta.rotationRetrograde,
                width = onlyMeta.mapWidth,
                height = onlyMeta.mapHeight,
                generatedAt = onlyMeta.generatedAt,
                human = HumanGeographyData.Load(baseName)
            };
            ApplyRenderParams(recipeOnly, onlyMeta);
            return recipeOnly;
        }

        var data = new MapData
        {
            baseName = baseName,
            terrainPng = ReadBytesOrNull(paths.terrain),
            heightPng = ReadBytesOrNull(paths.height),
            plateOverlayPng = ReadBytesOrNull(paths.plates),
            surfacePng = ReadBytesOrNull(paths.surface),
            detailPng = ReadBytesOrNull(paths.detail)
        };

        MapMetaData meta = MapMetaData.Load(baseName);
        if (meta != null)
        {
            data.axialTilt = meta.axialTilt;
            data.waterCoverage = meta.waterCoverage;
            data.seed = meta.seed;
            data.planetRadiusKm = meta.planetRadiusKm;
            data.rotationRetrograde = meta.rotationRetrograde;
            data.generatedAt = meta.generatedAt;
            data.width = meta.mapWidth;
            data.height = meta.mapHeight;
            ApplyRenderParams(data, meta);
        }

        data.human = HumanGeographyData.Load(baseName);

        // 元数据缺失尺寸时，从 PNG 头里补一个（避免查看器不知道宽高比）
        if (data.width <= 0 || data.height <= 0)
            InferSizeFromPng(data.terrainPng, out data.width, out data.height);

        return data;
    }

    // ── 内部 ──

    /// <summary>把 meta 里的渲染参数投到数据契约上。</summary>
    private static void ApplyRenderParams(MapData data, MapMetaData meta)
    {
        WorldTerrain.SurfaceRenderParams p = meta.GetRenderParams();
        data.seaLevelMeters = p.seaLevelMeters;
        data.snowLineEquatorMeters = p.snowLineEquatorMeters;
        data.snowLineMidMeters = p.snowLineMidMeters;
        data.snowLinePoleMeters = p.snowLinePoleMeters;
        data.tideAmplitudeMeters = p.tideAmplitudeMeters;
        data.tidePeriodDays = p.tidePeriodDays;
    }

    private static byte[] ReadBytesOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"MapStore: 读取失败 {path}：{e.Message}");
            return null;
        }
    }

    /// <summary>从 PNG 的 IHDR 块里读出宽高（PNG 前 24 字节固定布局）。</summary>
    private static void InferSizeFromPng(byte[] png, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (png == null || png.Length < 24)
            return;

        // 8 字节签名 + 4 字节长度 + 4 字节 "IHDR"，随后是大端宽、高
        width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
    }
}
