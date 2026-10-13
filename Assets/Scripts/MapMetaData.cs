using System;
using System.IO;
using UnityEngine;

/// <summary>
/// 地图元数据：与某张地形图绑定的属性（地轴倾角、水面占比等）。
/// 生成地图时随地形图一起写入 StreamingAssets 下的 "&lt;地图名&gt;_meta.json"，
/// 重新载入该地图时自动读取并应用，使地轴倾角成为地图自身的属性。
/// </summary>
[Serializable]
public class MapMetaData
{
    public const float DefaultAxialTilt = 23.5f;
    public const float MinAxialTilt = 0f;
    public const float MaxAxialTilt = 90f;

    /// <summary>元数据格式版本。</summary>
    public int version = 1;

    /// <summary>地轴倾角（黄赤交角），单位：度，范围 0-90。</summary>
    public float axialTilt = DefaultAxialTilt;

    /// <summary>生成时的水面（海洋）占比，0-1。</summary>
    public float waterCoverage = 0.5f;

    /// <summary>生成时使用的随机种子。</summary>
    public int seed;

    /// <summary>地图宽度（像素）。</summary>
    public int mapWidth;

    /// <summary>地图高度（像素）。</summary>
    public int mapHeight;

    /// <summary>生成时间（ISO 8601）。</summary>
    public string generatedAt = "";

    public static float ClampAxialTilt(float tilt)
    {
        if (float.IsNaN(tilt)) return DefaultAxialTilt;
        return Mathf.Clamp(tilt, MinAxialTilt, MaxAxialTilt);
    }

    /// <summary>地图元数据文件路径（不判断是否存在）。</summary>
    public static string GetPath(string mapBaseName)
    {
        return Path.Combine(Application.streamingAssetsPath, mapBaseName + "_meta.json");
    }

    /// <summary>把地图属性写入 "<地图名>_meta.json"。</summary>
    public static void Save(string mapBaseName, MapMetaData meta)
    {
        if (string.IsNullOrEmpty(mapBaseName) || meta == null)
            return;

        meta.axialTilt = ClampAxialTilt(meta.axialTilt);
        string path = GetPath(mapBaseName);
        try
        {
            File.WriteAllText(path, JsonUtility.ToJson(meta, true));
            Debug.Log($"Saved map meta: {path} (axialTilt={meta.axialTilt:F1})");
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to save map meta '{path}': {e.Message}");
        }
    }

    /// <summary>
    /// 读取地图属性；文件不存在或损坏时返回 null（调用方使用默认值）。
    /// </summary>
    public static MapMetaData Load(string mapBaseName)
    {
        if (string.IsNullOrEmpty(mapBaseName))
            return null;

        string path = GetPath(mapBaseName);
        if (!File.Exists(path))
            return null;

        try
        {
            var meta = JsonUtility.FromJson<MapMetaData>(File.ReadAllText(path));
            if (meta == null)
                return null;

            meta.axialTilt = ClampAxialTilt(meta.axialTilt);
            meta.waterCoverage = Mathf.Clamp01(meta.waterCoverage);
            return meta;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Failed to read map meta '{path}': {e.Message}");
            return null;
        }
    }

    /// <summary>读取地图的地轴倾角；没有元数据时返回默认值。</summary>
    public static float LoadAxialTilt(string mapBaseName)
    {
        var meta = Load(mapBaseName);
        return meta != null ? meta.axialTilt : DefaultAxialTilt;
    }
}
