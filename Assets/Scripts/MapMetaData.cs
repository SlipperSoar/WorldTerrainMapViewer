using System;
using System.IO;
using UnityEngine;
using WorldTerrain;

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

    /// <summary>
    /// 元数据格式版本。
    /// 3.1（V3.1.0）起加入「渲染参数」字段（海平面 / 雪线 / 潮汐），
    /// 版本升到 3 以便查看器区分「有渲染参数」与老地图；字段缺失时按缺省值处理。
    /// </summary>
    public int version = 1;

    /// <summary>地轴倾角（黄赤交角），单位：度，范围 0-90。</summary>
    public float axialTilt = DefaultAxialTilt;

    /// <summary>生成时的水面（海洋）占比，0-1。</summary>
    public float waterCoverage = 0.5f;

    /// <summary>生成时使用的随机种子。</summary>
    public int seed;

    /// <summary>星球半径（km）。缺省 = 地球半径；老地图读出来就是地球大小。</summary>
    public float planetRadiusKm = PlanetProfile.ReferenceRadiusKm;

    /// <summary>是否逆向自转（时间流转时太阳反向绕行）。缺省 = 顺向。</summary>
    public bool rotationRetrograde;

    /// <summary>
    /// 该地图的 PNG 是否**上北下南**（2.8.0 起为 true）。
    /// 老地图（缺省 false）是 Unity 的贴图行序，即南北颠倒；载入时需要区别对待。
    /// </summary>
    public bool pngNorthUp;

    // ── 整型配方（2.9.0 起）：重建地图的唯一依据，避免浮点文本往返误差 ──
    //
    // 老地图只有上面那些 float 字段；读到配方字段为默认值（version=0）时，
    // 视为「没有配方」，仍按旧行为直接载入 PNG。

    /// <summary>配方版本；0 表示这张地图没有配方（老地图）。</summary>
    public int recipeVersion;

    /// <summary>水面占比，千分比。</summary>
    public int waterPermille;

    /// <summary>地轴倾角，毫度。</summary>
    public int tiltMilliDeg;

    /// <summary>星球半径，米。</summary>
    public int radiusMeters;

    /// <summary>地图宽度（像素）。</summary>
    public int mapWidth;

    /// <summary>地图高度（像素）。</summary>
    public int mapHeight;

    /// <summary>生成时间（ISO 8601）。</summary>
    public string generatedAt = "";

    /// <summary>人文信息摘要（聚落/国家/交通线数量，完整数据在同名 _human.json 中）。</summary>
    public HumanMapSummary human = new HumanMapSummary();

    // ── 渲染参数（3.1 起，步骤 1 数据契约）──────────────────────────────
    //
    // 这些是**对生成结果的描述**（不是生成器输入）：着色器据此换算海面高度、
    // 雪线与潮位。老地图没有这些字段，读出来是 0 / 缺省值，由查看器兜底。

    /// <summary>海平面（米）。海平面标定后高度场以 0 为海平面，故恒为 0。</summary>
    public float seaLevelMeters;

    /// <summary>赤道雪线（米，按地图起伏标定）。</summary>
    public float snowLineEquatorMeters;

    /// <summary>中纬（45°）雪线（米）。</summary>
    public float snowLineMidMeters;

    /// <summary>极地雪线（米，通常为 0）。</summary>
    public float snowLinePoleMeters;

    /// <summary>潮汐振幅（米）。</summary>
    public float tideAmplitudeMeters;

    /// <summary>潮汐周期（地球日）。</summary>
    public float tidePeriodDays;

    /// <summary>把渲染参数填进 meta（3.1 起）。</summary>
    public void SetRenderParams(WorldTerrain.SurfaceRenderParams p)
    {
        seaLevelMeters = p.seaLevelMeters;
        snowLineEquatorMeters = p.snowLineEquatorMeters;
        snowLineMidMeters = p.snowLineMidMeters;
        snowLinePoleMeters = p.snowLinePoleMeters;
        tideAmplitudeMeters = p.tideAmplitudeMeters;
        tidePeriodDays = p.tidePeriodDays;
    }

    /// <summary>从 meta 读出渲染参数。老地图（version &lt; 3）用缺省值兜底。</summary>
    public WorldTerrain.SurfaceRenderParams GetRenderParams()
    {
        if (version < 3)
            return new WorldTerrain.SurfaceRenderParams();

        return new WorldTerrain.SurfaceRenderParams
        {
            seaLevelMeters = seaLevelMeters,
            snowLineEquatorMeters = snowLineEquatorMeters,
            snowLineMidMeters = snowLineMidMeters,
            snowLinePoleMeters = snowLinePoleMeters,
            tideAmplitudeMeters = tideAmplitudeMeters,
            tidePeriodDays = tidePeriodDays
        };
    }

    /// <summary>这张地图有没有配方（老地图没有）。</summary>
    public bool HasRecipe => recipeVersion > 0;

    /// <summary>读出配方。老地图（无配方）时用 float 字段尽力折算。</summary>
    public WorldTerrain.MapRecipe ToRecipe()
    {
        if (HasRecipe)
        {
            return new WorldTerrain.MapRecipe
            {
                version = recipeVersion,
                seed = seed,
                width = mapWidth,
                waterPermille = waterPermille,
                tiltMilliDeg = tiltMilliDeg,
                radiusMeters = radiusMeters,
                retrograde = rotationRetrograde
            };
        }

        return WorldTerrain.MapRecipe.FromParameters(seed, mapWidth, waterCoverage,
            axialTilt, planetRadiusKm, rotationRetrograde);
    }

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
            meta.planetRadiusKm = PlanetProfile.ClampRadiusKm(meta.planetRadiusKm);
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
