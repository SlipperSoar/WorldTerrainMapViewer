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
        public string meta;
        public string human;

        public bool HasTerrain => File.Exists(terrain);
        public bool HasHeight => File.Exists(height);
        public bool HasPlates => File.Exists(plates);
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
        if (!paths.HasTerrain)
        {
            Debug.LogWarning($"MapStore: 找不到地形图 {paths.terrain}");
            return null;
        }

        var data = new MapData
        {
            baseName = baseName,
            terrainPng = ReadBytesOrNull(paths.terrain),
            heightPng = ReadBytesOrNull(paths.height),
            plateOverlayPng = ReadBytesOrNull(paths.plates)
        };

        MapMetaData meta = MapMetaData.Load(baseName);
        if (meta != null)
        {
            data.axialTilt = meta.axialTilt;
            data.waterCoverage = meta.waterCoverage;
            data.seed = meta.seed;
            data.generatedAt = meta.generatedAt;
            data.width = meta.mapWidth;
            data.height = meta.mapHeight;
        }

        data.human = HumanGeographyData.Load(baseName);

        // 元数据缺失尺寸时，从 PNG 头里补一个（避免查看器不知道宽高比）
        if (data.width <= 0 || data.height <= 0)
            InferSizeFromPng(data.terrainPng, out data.width, out data.height);

        return data;
    }

    /// <summary>
    /// 把地图写盘。缺少地形图时直接拒绝（契约要求）。
    /// </summary>
    public static bool Save(MapData data)
    {
        if (data == null)
        {
            Debug.LogError("MapStore.Save 被拒绝：data 为 null");
            return false;
        }

        if (!data.Validate(out string reason))
        {
            Debug.LogError($"MapStore.Save 被拒绝：{reason}");
            return false;
        }

        Paths paths = GetPaths(data.baseName);

        try
        {
            File.WriteAllBytes(paths.terrain, data.terrainPng);
            Debug.Log($"Saved terrain map: {paths.terrain}");

            if (data.HasHeight)
                File.WriteAllBytes(paths.height, data.heightPng);

            if (data.HasPlateOverlay)
                File.WriteAllBytes(paths.plates, data.plateOverlayPng);
        }
        catch (Exception e)
        {
            Debug.LogError($"MapStore.Save 写图像失败：{e.Message}");
            return false;
        }

        MapMetaData.Save(data.baseName, new MapMetaData
        {
            axialTilt = data.axialTilt,
            waterCoverage = data.waterCoverage,
            seed = data.seed,
            mapWidth = data.width,
            mapHeight = data.height,
            generatedAt = string.IsNullOrEmpty(data.generatedAt)
                ? DateTime.Now.ToString("o")
                : data.generatedAt,
            human = HumanMapSummary.From(data.human)
        });

        if (data.human != null)
            HumanGeographyData.Save(data.baseName, data.human);

        return true;
    }

    /// <summary>
    /// 把一次生成的结果转成契约再落盘。
    /// PNG 编码需要 Unity 主线程，所以这一步只能在主线程做。
    /// </summary>
    public static MapData FromResult(WorldGenResult result, string baseName)
    {
        if (result == null)
            return null;

        var data = new MapData
        {
            baseName = baseName,
            axialTilt = result.axialTilt,
            waterCoverage = result.waterCoverage,
            seed = 0,                     // 由调用方按需覆盖（WorldGenResult 不带 seed）
            generatedAt = DateTime.Now.ToString("o"),
            width = result.width,
            height = result.height,
            human = result.human,
            terrainPng = EncodePng(result.terrainColors, result.width, result.height, false),
            heightPng = EncodePng(result.heightColors, result.width, result.height, true),
            plateOverlayPng = EncodePng(result.plateOverlayColors, result.width, result.height, false)
        };

        return data;
    }

    // ── 内部 ──

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

    private static byte[] EncodePng(Color32[] pixels, int width, int height, bool linear)
    {
        if (pixels == null || width <= 0 || height <= 0)
            return null;

        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, linear);
        try
        {
            // SetPixels32 直接收 8 位数据，不需要 Unity 再做一次浮点→字节转换
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex.EncodeToPNG();
        }
        finally
        {
            UnityEngine.Object.Destroy(tex);
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
