using System;
using System.Collections;
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

    /// <summary>
    /// 默认生成宽度（像素）。对外只说宽度，高度恒为一半：2048 → 2048×1024。
    /// 可选档位见 <see cref="MapResolution"/>。
    /// </summary>
    [SerializeField, Tooltip("默认生成宽度（像素）；高度恒为一半。可选 1024/2048/4096/8192")]
    private int defaultWidth = MapResolution.DefaultWidth;

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
    /// 开始生成一张新地图。
    /// </summary>
    /// <param name="seed">随机种子（0 = 用系统时间）</param>
    /// <param name="width">
    /// 地图**宽度**（像素），高度恒为一半。0 = 用默认宽度。
    /// 会吸附到 <see cref="MapResolution.Widths"/> 的最近档位（1024/2048/4096/8192）。
    /// </param>
    /// <param name="waterCoverage">水面占比（0-1，&lt;0 用当前默认值）</param>
    /// <param name="axialTilt">地轴倾角（0-90 度，&lt;0 用当前默认值）</param>
    public void GenerateWorld(int seed, int width = 0, float waterCoverage = -1f, float axialTilt = -1f)
    {
        if (isGenerating)
        {
            Debug.LogWarning("World generation already in progress.");
            return;
        }

        if (width <= 0)
            width = defaultWidth;

        // 只允许离散档位：任意值都吸附到最近的 1024/2048/4096/8192
        int snapped = MapResolution.Snap(width);
        if (snapped != width)
            Debug.Log($"生成宽度 {width} 不在可选档位内，已吸附到 {snapped}");

        if (waterCoverage < 0f)
            waterCoverage = this.waterCoverage;

        if (axialTilt < 0f)
            axialTilt = this.axialTilt;
        axialTilt = MapMetaData.ClampAxialTilt(axialTilt);

        StartCoroutine(GenerateWorldCoroutine(seed, snapped, waterCoverage, axialTilt));
    }

    /// <summary>当前默认生成宽度（像素）。</summary>
    public int DefaultWidth => MapResolution.Snap(defaultWidth);

    public bool IsGenerating => isGenerating;

    #endregion

    #region Private

    private IEnumerator GenerateWorldCoroutine(int seed, int mapWidth, float waterCoverage, float axialTilt)
    {
        int mapHeight = MapResolution.HeightFor(mapWidth);
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
            ReleaseGenerationResult();          // 失败也别把大数组留着
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
            ReleaseGenerationResult();
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

        // ── 释放后台线程交出来的中间结果 ──
        //
        // generationResult 里的数组很大：8192×4096 时 heightField / plateIds / terrainTypes
        // 各 128 MB，三张 Color32 各 128 MB，合计约 770 MB。彩色数据已经拷进贴图、
        // 也编码进 PNG 字节写盘了，这里必须清掉引用，否则整份结果会一直挂在这个字段上。
        ReleaseGenerationResult();

        // ── 让 Unity 回收「已销毁但尚未真正释放」的原生资源 ──
        //
        // Destroy(Texture2D) 只是打销毁标记，原生内存与显存要等一次资源回收才真正归零。
        // 每轮生成会换掉三张大贴图（8192 档每张 128 MB），不主动回收就会「每轮涨约 300 MB」——
        // 这正是实测到的现象（详见 VERIFICATION.md 第 14.9 节）。
        AsyncOperation unload = Resources.UnloadUnusedAssets();
        yield return unload;

        // 原生侧回收完，再压一次托管堆：主线程这一段（建贴图、编码 PNG）也分配了不少大数组
        GenerationPipeline.CollectNow();

        yield return null;
    }

    /// <summary>
    /// 清掉 <see cref="generationResult"/> 对中间结果的引用，让那批大数组可以被回收。
    /// 只在主线程把结果用完之后调用。
    /// </summary>
    private void ReleaseGenerationResult()
    {
        generationResult = null;
    }

    /// <summary>把颜色数组做成显示用贴图（等距圆柱图：横向重复、双线性过滤）。</summary>
    private static Texture2D CreateTexture(Color32[] pixels, int width, int height, bool linear)
    {
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, linear);
        tex.SetPixels32(pixels);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Repeat;

        // makeNoLongerReadable: true —— 释放 CPU 侧像素副本。
        // 显示用贴图只需要 GPU 那份，8192 档每张省 134 MB（三层共省约 400 MB）。
        tex.Apply(false, true);
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
