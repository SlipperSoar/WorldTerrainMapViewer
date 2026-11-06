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
    private volatile TerrainContext generationContext;

    /// <summary>本次生成使用的整型配方（写进 _meta.json，用于日后重建）。</summary>
    private MapRecipe pendingRecipe;

    /// <summary>最近一次生成的上下文/配方/名字：用于「导出」与前后切换的缓存。</summary>
    private TerrainContext lastContext;
    private MapRecipe lastRecipe;
    private string lastBaseName;

    /// <summary>按配方重建时沿用原来的地图名（null = 新生成，用时间戳命名）。</summary>
    private string pendingBaseName;

    /// <summary>导出开关（是否写高度图 / 板块图 / 人文 JSON）。地形图始终写。</summary>
    public MapExportOptions ExportOptions { get; set; } = MapExportOptions.All;

    /// <summary>
    /// 生成后是否显示断层带。由 UI 在生成前同步过来 ——
    /// 生成流程会重建叠加层，不同步的话「界面关着、星球上却显示」。
    /// </summary>
    public bool ShowPlateOverlay { get; set; }

    /// <summary>生成后是否显示人文图层（同上）。</summary>
    public bool ShowHumanOverlay { get; set; }
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
    /// <param name="planetRadiusKm">星球半径（km，&lt;0 用默认值 = 地球）</param>
    /// <param name="rotationRetrograde">是否逆向自转</param>
    public void GenerateWorld(int seed, int width = 0, float waterCoverage = -1f, float axialTilt = -1f,
        float planetRadiusKm = -1f, bool rotationRetrograde = false)
    {
        if (isGenerating)
        {
            Debug.LogWarning("World generation already in progress.");
            return;
        }

        if (width <= 0)
            width = defaultWidth;

        // 内存不足时不允许生成过高的分辨率（UI 已把滑条卡住，这里是第二道防线，
        // 也拦住脚本/其它调用方绕过界面直接调用的情形）
        if (!GuardMemory(width, out int snapped, out string reason))
        {
            Debug.LogError(reason);
            onError?.Invoke(reason);
            return;
        }

        if (snapped != width)
            Debug.Log($"生成宽度 {width} 不在可选档位内，已吸附到 {snapped}");

        if (waterCoverage < 0f)
            waterCoverage = this.waterCoverage;

        if (axialTilt < 0f)
            axialTilt = this.axialTilt;
        axialTilt = MapMetaData.ClampAxialTilt(axialTilt);

        if (planetRadiusKm < 0f)
            planetRadiusKm = PlanetProfile.DefaultRadiusKm;
        planetRadiusKm = PlanetProfile.ClampRadiusKm(planetRadiusKm);

        // 浮点参数在这里**一次性量化**成整型配方；之后一切都从配方展开，
        // 保证「同一配方重建」逐位一致（JSON 文本往返不再有精度歧义）。
        MapRecipe recipe = MapRecipe.FromParameters(
            seed, snapped, waterCoverage, axialTilt, planetRadiusKm, rotationRetrograde);
        pendingRecipe = recipe;
        pendingBaseName = null;      // 新生成：用时间戳命名

        StartCoroutine(GenerateWorldCoroutine(recipe));
    }

    /// <summary>
    /// 把宽度吸附到可选档位，并按物理内存判断该档位是否允许生成。
    /// GenerateWorld 与 GenerateFromRecipe 都必须过这里，避免两条入口出现不同的护栏。
    /// </summary>
    private static bool GuardMemory(int width, out int snapped, out string error)
    {
        snapped = MapResolution.Snap(width);
        int systemMemoryMb = SystemInfo.systemMemorySize;

        if (MapResolution.IsAllowed(snapped, systemMemoryMb))
        {
            error = null;
            return true;
        }

        error = $"内存不足，已拒绝生成 {MapResolution.Label(snapped)}：" +
                $"需要 {MapResolution.RequiredLabel(snapped)}，本机物理内存 {systemMemoryMb} MB，" +
                $"最高允许 {MapResolution.Label(MapResolution.MaxAllowedWidth(systemMemoryMb))}";
        return false;
    }

    /// <summary>
    /// 命中缓存时直接复用上次的上下文：重新渲染显示贴图（约 1~3 秒），
    /// 不必重跑整条生成管线（8192 档约 32 秒）。
    /// </summary>
    private void ApplyCachedToEarth(string baseName)
    {
        TerrainContext ctx = lastContext;
        if (ctx == null || EarthManager.Instance == null)
            return;

        Texture2D terrainTex = ExportLayer(ctx, MapLayer.Terrain, null);
        Texture2D heightTex = ExportOptions.height ? ExportLayer(ctx, MapLayer.Height, null) : null;
        Texture2D overlayTex = ExportOptions.plates ? ExportLayer(ctx, MapLayer.Plates, null) : null;

        EarthManager.Instance.SetEarthSurface(terrainTex);

        if (heightTex != null) EarthManager.Instance.SetEarthHeightMap(heightTex);
        else EarthManager.Instance.ClearEarthHeightMap();

        if (overlayTex != null) EarthManager.Instance.SetPlateOverlay(overlayTex);
        else EarthManager.Instance.SetPlateOverlayVisible(false);

        EarthManager.Instance.SetHumanGeography(ctx.Human, baseName);

        // 步骤 2.0：缓存命中也要把两张数据图挂回去，否则切回同一配方会丢 Fresnel
        EarthManager.Instance.SetSurfaceData(
            ExportDataLayer(ctx, MapLayer.Surface),
            ExportDataLayer(ctx, MapLayer.Detail),
            SurfaceDataLayers.ComputeRenderParams(ctx));

        EarthManager.Instance.SetPlateOverlayVisible(overlayTex != null && ShowPlateOverlay);
        EarthManager.Instance.SetHumanOverlayVisible(ShowHumanOverlay);

        lastBaseName = baseName;
        onProgress?.Invoke(1f);
        // 与整条生成管线一致：传地图名，不传尚不存在的 PNG 路径。
        onComplete?.Invoke(baseName);
    }

    /// <summary>
    /// 按配方重建一张地图（首次选中「只有配方」的地图时调用）。
    /// 沿用原地图名，因此重建后仍写回同一个 <c>_meta.json</c>。
    /// </summary>
    public bool GenerateFromRecipe(MapRecipe recipe, string baseName)
    {
        if (isGenerating)
        {
            Debug.LogWarning("WorldTerrainGenerator: 正在生成中，忽略重建请求");
            return false;
        }

        // 按配方重建与「生成新地图」走同一道内存防线：列表里点选一张 8192 档
        // 配方图，同样要先确认这台机器放得下，不能绕过 UI 直接跑全管线。
        if (!GuardMemory(recipe.width, out _, out string reason))
        {
            Debug.LogError(reason);
            onError?.Invoke(reason);
            return false;
        }

        // 缓存命中：同一份配方且上下文还在，直接复用，不必重算
        if (lastContext != null && lastRecipe.SameAs(recipe))
        {
            ApplyCachedToEarth(baseName);
            return true;
        }

        pendingRecipe = recipe;
        pendingBaseName = baseName;
        StartCoroutine(GenerateWorldCoroutine(recipe));
        return true;
    }

    /// <summary>
    /// 把**当前地图**导出成整包（地形/高度/板块 PNG + 人文 JSON + meta）。
    /// 只有用户显式点「导出」时才调用 —— 生成本身不写这些大文件。
    /// </summary>
    public bool ExportCurrentMap(MapExportOptions? options = null)
    {
        if (lastContext == null || string.IsNullOrEmpty(lastBaseName))
        {
            Debug.LogWarning("ExportCurrentMap: 当前地图没有可导出的生成结果（请先生成或按配方重建）");
            return false;
        }

        MapExportOptions opts = options ?? MapExportOptions.All;
        bool ok = MapPublisher.ExportFull(lastContext, lastRecipe, lastBaseName, opts);
        Debug.Log(ok
            ? $"已导出地图 {lastBaseName}"
            : $"导出失败 {lastBaseName}");
        return ok;
    }

    /// <summary>
    /// 查看器载入了一张**已有的 PNG 快照**时调用：让「当前地图」与导出状态保持一致。
    /// 这类地图的 PNG 本来就在磁盘上，没有生成上下文可导出，因此清掉 lastContext，
    /// 使 ExportCurrentMap 明确拒绝（而不是悄悄导出上一次生成的那张图）。
    /// </summary>
    public void NotifyExternalMapSelected(string baseName)
    {
        if (isGenerating)
            return;

        lastContext = null;
        lastBaseName = baseName;
        lastRecipe = default;
    }

    /// <summary>
    /// 主动释放常驻的生成上下文（8192 档的逐格数组有数百 MB）。
    /// 释放后缓存命中失效，再选中同一配方会重新跑管线 —— 结果不变，只是慢一些。
    /// 保留 lastBaseName / lastRecipe，导出与界面显示仍以当前地图为准。
    /// </summary>
    public void ReleaseCachedContext()
    {
        lastContext = null;
        generationContext = null;
    }

    private void OnDestroy()
    {
        ReleaseCachedContext();
    }

    /// <summary>最近一次生成/载入的地图名（用于 UI 显示与切换缓存）。</summary>
    public string CurrentMapName => lastBaseName;

    /// <summary>当前默认生成宽度（像素）。</summary>
    public int DefaultWidth => MapResolution.Snap(defaultWidth);

    public bool IsGenerating => isGenerating;

    #endregion

    #region Private

    private IEnumerator GenerateWorldCoroutine(MapRecipe recipe)
    {
        int seed = recipe.seed;
        var config = recipe.ToConfig();

        generationProgress = 0f;
        generationError = null;
        generationContext = null;
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

        TerrainContext ctx = generationContext;
        if (ctx == null)
        {
            Debug.LogError("World generation produced no result.");
            onError?.Invoke("No result");
            yield break;
        }

        // ── 主线程：流式导出（按行带渲染 → 直接编码写盘）──
        //
        // 不再生成整幅 Color32[]：8192 档三张合计 384 MB，而且还要再拷进贴图、
        // 再编码成 PNG，同一份像素会同时存在三份。现在一条带只有 8 MB。
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string seedStr = seed > 0 ? seed.ToString() : "rnd";
        // 按配方重建时沿用原名，否则新生成用时间戳
        string baseName = pendingBaseName ?? $"gen_{seedStr}_{timestamp}";
        pendingBaseName = null;

        // 生成**不落 PNG**：只建显示用的贴图，配方写进 meta。
        // 真正导出由用户点「导出」时触发（ExportCurrentMap），见 MapPublisher。
        // 注意：这里保留 lastContext 是为了「随时可导出」与配方缓存命中，
        // 代价是逐格大数组常驻到下次生成/手动释放为止（见 ReleaseCachedContext）。
        Texture2D terrainTex = ExportLayer(ctx, MapLayer.Terrain, null);
        Texture2D heightTex = ExportOptions.height
            ? ExportLayer(ctx, MapLayer.Height, null)
            : null;
        Texture2D overlayTex = ExportOptions.plates
            ? ExportLayer(ctx, MapLayer.Plates, null)
            : null;

        // 步骤 2.0：生成后直接把两张数据图也建出来挂到地表材质，
        // 让 Fresnel 的湿度调制立刻生效（不必先「导出 → 载入」）。
        // 与导出整包走同一套流式渲染，不改变生成语义。
        Texture2D surfaceTex = ExportDataLayer(ctx, MapLayer.Surface);
        Texture2D detailTex = ExportDataLayer(ctx, MapLayer.Detail);

        if (terrainTex == null)
        {
            string error = $"导出地形图失败（{baseName}）";
            Debug.LogError(error);
            onError?.Invoke(error);
            ReleaseGenerationResult();
            yield break;
        }

        // 只存配方（几百字节），不写 PNG / human。写失败要如实报错：
        // 否则地图列表里不会出现这张图，界面却已经显示「生成完成」。
        lastContext = ctx;
        lastRecipe = recipe;
        lastBaseName = baseName;
        if (!MapPublisher.SaveRecipe(ctx, recipe, baseName))
        {
            string error = $"写入地图配方失败（{baseName}），该地图无法在列表中重建";
            Debug.LogError(error);
            onError?.Invoke(error);
        }

        // ── 应用到星球 ──
        if (EarthManager.Instance != null)
        {
            EarthManager.Instance.SetEarthSurface(terrainTex);

            if (heightTex != null)
                EarthManager.Instance.SetEarthHeightMap(heightTex);
            else
                EarthManager.Instance.ClearEarthHeightMap();

            if (overlayTex != null)
                EarthManager.Instance.SetPlateOverlay(overlayTex);
            else
                EarthManager.Instance.SetPlateOverlayVisible(false);

            EarthManager.Instance.SetHumanGeography(ctx.Human, baseName);

            // 两张数据图 + 渲染参数：由当前上下文算出，与按配方重建结果一致
            EarthManager.Instance.SetSurfaceData(surfaceTex, detailTex,
                SurfaceDataLayers.ComputeRenderParams(ctx));

            // 与界面开关同步：叠加层刚被重建，必须按开关状态决定显示与否
            EarthManager.Instance.SetPlateOverlayVisible(overlayTex != null && ShowPlateOverlay);
            EarthManager.Instance.SetHumanOverlayVisible(ShowHumanOverlay);
        }
        else
        {
            Debug.LogWarning("EarthManager.Instance is null — texture not applied to sphere.");
        }

        onProgress?.Invoke(1f);
        // 回调给的是地图名而非 PNG 路径：生成只写 meta，PNG 要等用户点「导出」才有。
        onComplete?.Invoke(baseName);

        // ── 释放后台线程交出来的中间结果引用 ──
        //
        // 注意：lastContext 仍持有整份上下文（为了随时可导出 / 缓存命中），
        // 这里清掉的只是 generationContext 这个「线程→主线程」的交接引用。
        // 真正释放大数组要调用 ReleaseCachedContext() 或等下一次生成覆盖。
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
        generationContext = null;
    }

    /// <summary>
    /// 导出一层：按行带渲染成贴图 → 编码写盘 → 释放 CPU 侧像素，返回可显示贴图。
    /// 写盘失败不阻断生成（贴图仍然可用于显示），只在日志里报错。
    /// </summary>
    private static Texture2D ExportLayer(TerrainContext ctx, MapLayer layer, string path)
    {
        // 一次遍历同时产出贴图（北极在第 0 行，给着色器）与 PNG（上北下南，给人看）
        Texture2D tex = MapExporter.ExportLayer(ctx, layer, path);
        if (tex == null)
            return null;

        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Repeat;
        return tex;
    }

    /// <summary>
    /// 建一张**数据图贴图**（不写盘）供地表材质采样。
    /// linear 由 MapExporter 保证；surface 的 G 是分类值，必须用 Point 过滤，
    /// 否则 Bilinear 插值会在类别交界处产生中间值，把「河」判成「海」。
    /// </summary>
    private static Texture2D ExportDataLayer(TerrainContext ctx, MapLayer layer)
    {
        Texture2D tex = MapExporter.ExportLayer(ctx, layer, null);
        if (tex == null)
            return null;

        tex.filterMode = layer == MapLayer.Surface ? FilterMode.Point : FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Repeat;
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
            TerrainContext ctx = TerrainGenerator.GenerateContext(
                config, p => generationProgress = p);

            generationContext = ctx;
            generationProgress = 1.0f;
        }
        catch (Exception e)
        {
            generationError = e.ToString();
        }
    }

    #endregion
}
