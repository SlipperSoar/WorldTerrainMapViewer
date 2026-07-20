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
    public void GenerateWorld(int seed, int height = 0, float waterCoverage = -1f)
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

        StartCoroutine(GenerateWorldCoroutine(seed, height, waterCoverage));
    }

    public bool IsGenerating => isGenerating;

    #endregion

    #region Private

    private IEnumerator GenerateWorldCoroutine(int seed, int mapHeight, float waterCoverage)
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
            waterCoverage = waterCoverage
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
        string heightPath = Path.Combine(
            Application.streamingAssetsPath,
            $"gen_{seedStr}_{timestamp}_height.png");
        string terrainPath = Path.Combine(
            Application.streamingAssetsPath,
            $"gen_{seedStr}_{timestamp}.png");

        try
        {
            File.WriteAllBytes(heightPath, heightTex.EncodeToPNG());
            Debug.Log($"Saved heightmap: {heightPath}");

            File.WriteAllBytes(terrainPath, terrainTex.EncodeToPNG());
            Debug.Log($"Saved terrain map: {terrainPath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to save PNG files: {e.Message}");
            onError?.Invoke(e.Message);
            yield break;
        }

        // ── Apply terrain map and height map to Earth sphere ──
        if (EarthManager.Instance != null)
        {
            EarthManager.Instance.SetEarthSurface(terrainTex);
            EarthManager.Instance.SetEarthHeightMap(heightTex);
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
            generationProgress = 0.30f;
            var heightField = PlateTectonicsGenerator.GenerateHeights(
                config, rng, plateIds, plates, noise);

            // Step 4: Rivers and lakes
            generationProgress = 0.55f;
            var hydrology = HydrologyGenerator.Generate(
                config.width, config.height, heightField);

            // Steps 5-6: Terrain classification + glacier rules
            generationProgress = 0.70f;
            var terrainTypes = TerrainClassifier.Classify(
                config.width, config.height, heightField,
                plateIds, hydrology, noise);

            // Step 8: Grayscale heightmap
            generationProgress = 0.82f;
            var heightColors = TerrainMapRenderer.RenderHeightMap(
                config.width, config.height, heightField);

            // Step 7: Color terrain map (hypsometric tinting)
            generationProgress = 0.92f;
            var terrainColors = TerrainMapRenderer.RenderTerrainMap(
                config.width, config.height, heightField,
                terrainTypes, hydrology, noise);

            generationResult = new WorldGenResult
            {
                width = config.width,
                height = config.height,
                heightField = heightField,
                plateIds = plateIds,
                terrainTypes = terrainTypes,
                heightColors = heightColors,
                terrainColors = terrainColors
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
