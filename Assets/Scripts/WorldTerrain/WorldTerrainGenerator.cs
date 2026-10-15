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

        // ── Main thread: create Texture2D from computed colour arrays ──
        Texture2D heightTex = new Texture2D(
            result.width, result.height, TextureFormat.RGBA32, false, true);
        heightTex.SetPixels(result.heightColors);
        heightTex.Apply();

        Texture2D terrainTex = new Texture2D(
            result.width, result.height, TextureFormat.RGBA32, false);
        terrainTex.SetPixels(result.terrainColors);
        terrainTex.filterMode = FilterMode.Bilinear;
        terrainTex.wrapMode = TextureWrapMode.Repeat;
        terrainTex.Apply();

        // ── Save PNGs to StreamingAssets ──
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string seedStr = seed > 0 ? seed.ToString() : "rnd";
        string baseName = $"gen_{seedStr}_{timestamp}";
        string heightPath = Path.Combine(
            Application.streamingAssetsPath, baseName + "_height.png");
        string terrainPath = Path.Combine(
            Application.streamingAssetsPath, baseName + ".png");
        string platePath = Path.Combine(
            Application.streamingAssetsPath, baseName + "_plates.png");

        // Create plate textures — save the transparent version as PNG so that
        // reloading from disk preserves the correct alpha for the overlay sphere
        Texture2D plateTex = new Texture2D(
            result.width, result.height, TextureFormat.RGBA32, false);
        plateTex.SetPixels(result.plateOverlayColors);
        plateTex.Apply();

        Texture2D overlayTex = new Texture2D(
            result.width, result.height, TextureFormat.RGBA32, false);
        overlayTex.SetPixels(result.plateOverlayColors);
        overlayTex.filterMode = FilterMode.Bilinear;
        overlayTex.wrapMode = TextureWrapMode.Repeat;
        overlayTex.Apply();

        try
        {
            File.WriteAllBytes(heightPath, heightTex.EncodeToPNG());
            Debug.Log($"Saved heightmap: {heightPath}");

            File.WriteAllBytes(terrainPath, terrainTex.EncodeToPNG());
            Debug.Log($"Saved terrain map: {terrainPath}");

            File.WriteAllBytes(platePath, plateTex.EncodeToPNG());
            Debug.Log($"Saved plate division map: {platePath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to save PNG files: {e.Message}");
            onError?.Invoke(e.Message);
            yield break;
        }

        // ── Save map properties (地轴倾角等) alongside the map ──
        MapMetaData.Save(baseName, new MapMetaData
        {
            axialTilt = config.axialTilt,
            waterCoverage = config.waterCoverage,
            seed = seed,
            mapWidth = config.width,
            mapHeight = config.height,
            generatedAt = DateTime.Now.ToString("o"),
            human = HumanMapSummary.From(result.human)
        });

        // ── 人文图层随地图一起保存（<地图名>_human.json）──
        HumanGeographyData.Save(baseName, result.human);

        // ── Apply terrain map and height map to Earth sphere ──
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
        onComplete?.Invoke(terrainPath);

        yield return null;
    }

    /// <summary>
    /// Background thread entry point — all computation happens here.
    /// No UnityEngine API calls (only System.Math and our own types).
    /// </summary>
    private void GenerateInBackground(WorldGenConfig config)
    {
        try
        {
            var rng = new System.Random(config.seed);
            var noise = new SeededNoise(config.seed);

            // Step 1: Generate tectonic plates
            generationProgress = 0.05f;
            var plates = PlateTectonicsGenerator.GeneratePlates(config, rng);

            // Voronoi plate assignment
            generationProgress = 0.15f;
            var plateIds = PlateTectonicsGenerator.AssignPlates(
                config.width, config.height, plates, noise);

            // Steps 2-3: Height field with drift-induced boundary effects
            float[] heightField;
            float[] sphereRunoff = null;                    // 球面径流场（只有球面路径才有）
            float sphereRunoffThreshold = 0f;
            if (!config.planarGeneration)
            {
                // 球面生成域：在单位球面的等面积网格上生成大地形，再投影成等距圆柱图。
                // 没有极点奇点、没有经度接缝，板块距离按球面角距计算。
                generationProgress = 0.18f;
                var sphere = SphericalTerrainGenerator.Generate(
                    config, plates, noise, p => generationProgress = 0.18f + 0.12f * p);
                heightField = sphere.Height;
                sphereRunoff = sphere.Runoff;
                sphereRunoffThreshold = sphere.RunoffChannelThreshold;
            }
            else
            {
                generationProgress = 0.30f;
                heightField = PlateTectonicsGenerator.GenerateHeights(
                    config, rng, plateIds, plates, noise);

                // 平面路径没有球面径流场：水文自动退回平面判据，
                // 不会像以前那样误用「上一张图留下的球面汇流」。
            }

            // Step 3b: 气候场（纬向蒸发 + 盛行风带输送 + 迎风坡降水/雨影）
            generationProgress = 0.34f;
            float[] moisture = ClimateModel.ComputeMoistureField(
                config.width, config.height, heightField, noise, config.axialTilt,
                p => generationProgress = 0.34f + 0.03f * p);

            // Step 3c: 侵蚀 —— 水力（雨滴冲刷河谷、下游沉积）+ 热力（休止角），
            //          强度与降水耦合：湿润区冲刷更强。
            //          球面路径已在球面格点上做过一次「大尺度」侵蚀（等面积、无极点接缝），
            //          这里再在投影后的像素网格上做一次「细尺度」雕刻 —— 两级尺度互补：
            //          球面那级决定山谷走向，这一级决定河谷的锐利程度。
            ErosionSimulator.Erode(config.width, config.height, heightField, moisture, config.seed,
                p => generationProgress = 0.37f + 0.10f * p);

            // 侵蚀会整体削低陆地，重新标定海平面以保持海陆比例
            PlateTectonicsGenerator.ApplyWaterCoverage(
                config.width, config.height, heightField, config.waterCoverage);

            // 地形变了，复算一次气候：迎风坡与雨影随之改变（气候 ↔ 地形一次反馈）
            generationProgress = 0.48f;
            moisture = ClimateModel.ComputeMoistureField(
                config.width, config.height, heightField, noise, config.axialTilt,
                p => generationProgress = 0.48f + 0.02f * p);

            // Step 4: 水文 —— 河网、湖泊、三角洲（用侵蚀后的地形与气候）
            // 球面径流场显式传入：河网走向按球面真实汇流判定，平面汇流只决定河宽。
            // 湖泊在像素网格上判定，但面积门槛按真实球面面积（cos lat）折算，纬度不失真。
            generationProgress = 0.50f;
            var hydrology = HydrologyGenerator.Generate(
                config.width, config.height, heightField, noise,
                p => generationProgress = 0.50f + 0.15f * p, moisture,
                sphereRunoff, sphereRunoffThreshold, config.axialTilt);

            // Steps 5-6: Terrain classification + 冰盖/雪线规则（依据地图的地轴倾角）
            generationProgress = 0.70f;
            var terrainTypes = TerrainClassifier.Classify(
                config.width, config.height, heightField,
                plateIds, hydrology, noise, config.axialTilt, moisture);

            // Step 5b: Render plate division map (transparent for overlay + PNG)
            generationProgress = 0.75f;
            var plateOverlayColors = TerrainMapRenderer.RenderPlateMap(
                config.width, config.height, plateIds, true);

            // Step 8: Grayscale heightmap
            generationProgress = 0.82f;
            var heightColors = TerrainMapRenderer.RenderHeightMap(
                config.width, config.height, heightField);

            // Step 7: Color terrain map (hypsometric tinting)
            generationProgress = 0.92f;
            var terrainColors = TerrainMapRenderer.RenderTerrainMap(
                config.width, config.height, heightField,
                terrainTypes, hydrology, noise);

            // Step 9: 人文（聚落 / 国家 / 交通 / 中文地名）
            generationProgress = 0.93f;
            var human = HumanGeographyGenerator.Generate(
                config.width, config.height, heightField, hydrology,
                config.axialTilt, noise, config.seed,
                p => generationProgress = 0.93f + 0.06f * p, moisture);

            generationResult = new WorldGenResult
            {
                width = config.width,
                height = config.height,
                heightField = heightField,
                plateIds = plateIds,
                terrainTypes = terrainTypes,
                heightColors = heightColors,
                terrainColors = terrainColors,
                plateOverlayColors = plateOverlayColors,
                axialTilt = config.axialTilt,
                waterCoverage = config.waterCoverage,
                human = human
            };
            generationProgress = 1.0f;
        }
        catch (Exception e)
        {
            generationError = e.ToString();
        }
    }

    #endregion
}
