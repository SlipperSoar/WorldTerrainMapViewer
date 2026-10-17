using System;
using System.Collections;
using System.IO;
using UnityEngine;
using WorldTerrain;

/// <summary>
/// Main controller for world terrain map generation.
/// Runs heavy computation on a background thread, reports progress via coroutine,
/// saves PNGs to StreamingAssets, and applies the texture to the Earth sphere.
/// </summary>
public class WorldTerrainGenerator : MonoBehaviour
{
    #region Properties

    public static WorldTerrainGenerator Instance { get; private set; }

    [SerializeField] private int defaultHeight = 1024;

    [Range(0.1f, 0.9f)]
    [SerializeField, Tooltip("水面占比（0.1=10%海洋 ~ 0.9=90%海洋）")]
    private float waterCoverage = 0.5f;

    [Range(0f, 90f)]
    [SerializeField, Tooltip("地轴倾角（0°-90°），地图属性：决定季节光照与冰盖分布")]
    private float axialTilt = 23.5f;

    public Action<float> onProgress;
    public Action<string> onComplete;
    public Action<string> onError;

    private volatile float generationProgress;
    private volatile string generationError;
    private volatile WorldGenResult generationResult;
    private volatile bool isGenerating;

    #endregion

    #region Unity

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Start generating a new world terrain map.
    /// Call from UI or other scripts.
    /// </summary>
    /// <param name="seed">Random seed (0 = system time)</param>
    /// <param name="height">Map height in pixels (0 = use defaultHeight)</param>
    /// <param name="waterCoverage">Fraction of surface that should be ocean (0-1, default 0.5)</param>
    /// <param name="axialTilt">地轴倾角（0-90 度），地图属性；&lt;0 时使用当前默认值</param>
    public void GenerateWorld(int seed, int height = 0, float waterCoverage = -1f, float axialTilt = -1f)
    {
        if (isGenerating)
        {
            Debug.LogWarning("World generation already in progress.");
            return;
        }

        if (height <= 0)
            height = defaultHeight;

        if (waterCoverage < 0f)
            waterCoverage = this.waterCoverage;

        if (axialTilt < 0f)
            axialTilt = this.axialTilt;
        axialTilt = MapMetaData.ClampAxialTilt(axialTilt);

        StartCoroutine(GenerateWorldCoroutine(seed, height, waterCoverage, axialTilt));
    }

    public bool IsGenerating => isGenerating;

    #endregion

    #region Private

    private IEnumerator GenerateWorldCoroutine(int seed, int mapHeight, float waterCoverage, float axialTilt)
    {
        int mapWidth = mapHeight * 2;
        var config = new WorldGenConfig
        {
            seed = seed,
            width = mapWidth,
            height = mapHeight,
            minPlates = 4,
            maxPlates = 8,
            maxElevation = 10000f,
            minElevation = -10000f,
            waterCoverage = waterCoverage,
            axialTilt = axialTilt
        };

        generationProgress = 0f;
        generationError = null;
        generationResult = null;
        isGenerating = true;

        // ── Launch background thread for heavy computation ──
        System.Threading.Thread thread = new System.Threading.Thread(
            () => GenerateInBackground(config));
        thread.IsBackground = true;
        thread.Start();

        // ── Main thread: poll progress while thread runs ──
        while (thread.IsAlive)
        {
            onProgress?.Invoke(generationProgress);
            yield return null;
        }

        // Thread.Join provides a memory barrier — all writes by the
        // background thread are now visible
        thread.Join();
        isGenerating = false;

        if (generationError != null)
        {
            Debug.LogError($"World generation failed: {generationError}");
            onError?.Invoke(generationError);
            yield break;
        }

        var result = generationResult;
        if (result == null)
        {
            Debug.LogError("World generation produced no result.");
            onError?.Invoke("No result");
            yield break;
        }

        // ── 主线程：把结果整理成 MapData 契约，落盘 ──
        // 落盘/读取都走 MapStore，生成器与查看器不再各自拼文件名。
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string seedStr = seed > 0 ? seed.ToString() : "rnd";
        string baseName = $"gen_{seedStr}_{timestamp}";

        MapData map = MapStore.FromResult(result, baseName);
        map.seed = seed;
        map.axialTilt = config.axialTilt;
        map.waterCoverage = config.waterCoverage;

        if (!MapStore.Save(map))
        {
            string error = $"保存地图失败（{baseName}）";
            Debug.LogError(error);
            onError?.Invoke(error);
            yield break;
        }

        // ── 主线程：为显示创建贴图（与落盘无关，落盘用的是契约里的 PNG 字节）──
        Texture2D heightTex = CreateTexture(result.heightColors, result.width, result.height, true);
        Texture2D terrainTex = CreateTexture(result.terrainColors, result.width, result.height, false);
        Texture2D overlayTex = CreateTexture(result.plateOverlayColors, result.width, result.height, false);

        // ── 应用到星球 ──
        if (EarthManager.Instance != null)
        {
            EarthManager.Instance.SetEarthSurface(terrainTex);
            EarthManager.Instance.SetEarthHeightMap(heightTex);
            EarthManager.Instance.SetPlateOverlay(overlayTex);

            // 生成完立刻把人文图层叠加上去
            EarthManager.Instance.SetHumanGeography(result.human, baseName);
        }
        else
        {
            Debug.LogWarning("EarthManager.Instance is null — texture not applied to sphere.");
        }

        onProgress?.Invoke(1f);
        onComplete?.Invoke(MapStore.GetPaths(baseName).terrain);

        yield return null;
    }

    /// <summary>把颜色数组做成显示用贴图（等距圆柱图：横向重复、双线性过滤）。</summary>
    private static Texture2D CreateTexture(Color[] pixels, int width, int height, bool linear)
    {
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, linear);
        tex.SetPixels(pixels);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.Apply();
        return tex;
    }

    /// <summary>
    /// 后台线程入口 —— 所有计算都在这里发生，不调用任何 UnityEngine API。
    ///
    /// 具体步骤不写在这里：管线与各步骤见 <c>WorldTerrain/Generation/</c>，
    /// 每个步骤都是独立的类、输入输出都在 <see cref="TerrainContext"/> 里，
    /// 可以单独构造上下文来测试或整体替换。
    /// </summary>
    private void GenerateInBackground(WorldGenConfig config)
    {
        try
        {
            WorldGenResult result = TerrainGenerator.Generate(
                config, p => generationProgress = p);

            generationResult = result;
            generationProgress = 1.0f;
        }
        catch (Exception e)
        {
            generationError = e.ToString();
        }
    }

    #endregion
}
