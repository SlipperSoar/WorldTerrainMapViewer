using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System.IO;

public class UIController : MonoBehaviour
{
    #region Properties

    [SerializeField] private Button exitButton;

    [Header("加载地图面板")] [SerializeField] private Text mapFileName;
    [SerializeField] private Transform imageListContainer;
    [SerializeField] private GameObject imageListItemPrefab;
    [SerializeField] private RayCastArea inOutArea;
    [SerializeField] private RectTransform uiPanel;
    [SerializeField] private float slideDuration = 0.3f;
    [SerializeField] private float slideDistance = 300f;

    [Header("一键定位特殊角度")] [SerializeField, Tooltip("一键北极")]
    private Button northButton;

    [SerializeField, Tooltip("一键赤道")] private Button equatorButton;
    [SerializeField, Tooltip("一键南极")] private Button southButton;
    [SerializeField, Tooltip("一键俯视")] private Button topButton;

    [Header("状态显示")] [SerializeField] private Text earthRotate;
    [SerializeField] private CameraController cameraController;
    [SerializeField] private Text dayTime;
    [SerializeField] private Text seasonTime;
    [SerializeField] private SunLightController sunLightController;

    [Header("时间")] [SerializeField, Tooltip("时间滑动条（0-24小时）")]
    private Slider timeSlider;

    [SerializeField] private Text timeSliderValue;

    [SerializeField, Tooltip("季节滑动条（0-40，4季×10细分）")]
    private Slider seasonSlider;

    [SerializeField] private Text seasonSliderValue;

    [SerializeField, Tooltip("时间自动流动开关")] private Toggle autoCycleToggle;

    [SerializeField] private Text autoCycleToggleText;

    [Header("标点")] [SerializeField, Tooltip("标记点列表容器")]
    private Transform markerListContainer;

    [SerializeField] private GameObject markerListItemPrefab;
    [SerializeField] private Text currentColorIndicator;
    [SerializeField] private Image currentColorIndicatorImage;
    [SerializeField] private Button clearMarkersButton;
    [SerializeField] private Image[] markerColors;

    [Header("世界生成")] [SerializeField, Tooltip("生成新世界按钮")]
    private Button generateButton;

    [SerializeField, Tooltip("种子输入框")] private InputField seedInputField;
    [SerializeField, Tooltip("生成进度文本")] private Text generationProgressText;
    [SerializeField, Tooltip("水面占比滑动条（0.1-0.9）")] private Slider waterCoverageSlider;
    [SerializeField, Tooltip("水面占比数值显示")] private Text waterCoverageValueText;

    private Texture2D currentTexture;
    private List<string> availableImages = new List<string>();
    private List<GameObject> listItemObjects = new List<GameObject>();
    private List<GameObject> markerListItemObjects = new List<GameObject>();
    private bool isUIVisible = false;
    private Coroutine slideCoroutine;
    private bool isUpdatingTimeSlider = false;
    private bool isUpdatingSeasonSlider = false;

    #endregion

    #region Unity

    void Start()
    {
        exitButton.onClick.AddListener(Application.Quit);

        InitializeTween();
        InitializeEarth();
        InitializeSliders();
        InitializeAutoCycleToggle();
        InitializeMarkerSystem();
        InitializeImageList();
        LoadImagesFromStreamingAssets();
        InitializeWorldGeneration();
    }

    private void Update()
    {
        UpdateCameraRotationDisplay();
        UpdateTimeDisplay();
        UpdateSlidersDisplay();
        UpdateAutoCycleToggleDisplay();
        UpdateMarkerListDisplay();
        UpdateCurrentColorDisplay();
    }

    void OnDestroy()
    {
        if (currentTexture != null)
        {
            Destroy(currentTexture);
        }

        ClearListItemObjects();
        ClearMarkerListItemObjects();
    }

    #endregion

    #region Display

    private void UpdateCameraRotationDisplay()
    {
        if (earthRotate == null || cameraController == null)
            return;

        float horizontal = cameraController.GetHorizontalRotation();
        float vertical = cameraController.GetVerticalRotation();

        earthRotate.text = $"H: {horizontal:F1}° V: {vertical:F1}°";
    }

    private void UpdateTimeDisplay()
    {
        if (dayTime == null || seasonTime == null || sunLightController == null)
            return;

        dayTime.text = $"{sunLightController.GetTimeOfDayName()}";
        seasonTime.text = $"{sunLightController.GetCurrentSeasonName()}";
    }

    #endregion

    #region Marker System

    private void InitializeMarkerSystem()
    {
        UpdateCurrentColorDisplay();

        if (clearMarkersButton != null)
        {
            clearMarkersButton.onClick.AddListener(ClearAllMarkers);
        }

        if (markerColors != null)
        {
            for (int i = 0; i < markerColors.Length; i++)
            {
                markerColors[i].color = EarthManager.Instance.markerColors[i];
            }
        }
    }

    private void UpdateCurrentColorDisplay()
    {
        if (currentColorIndicator == null || currentColorIndicatorImage == null || EarthManager.Instance == null)
            return;

        int colorIndex = EarthManager.Instance.GetCurrentColorIndex();
        Color color = EarthManager.Instance.GetCurrentColor();

        currentColorIndicator.text = $"当前颜色: {colorIndex}";
        currentColorIndicatorImage.color = color;
    }

    private void UpdateMarkerListDisplay()
    {
        if (markerListContainer == null || EarthManager.Instance == null)
            return;

        var allMarkers = EarthManager.Instance.GetAllMarkers();

        if (allMarkers.Count != markerListItemObjects.Count)
        {
            RefreshMarkerList(allMarkers);
        }
    }

    private void RefreshMarkerList(List<MarkerData> markers)
    {
        ClearMarkerListItemObjects();

        if (markerListContainer == null)
            return;

        for (int i = 0; i < markers.Count; i++)
        {
            GameObject item;

            if (markerListItemPrefab != null)
            {
                item = Instantiate(markerListItemPrefab, markerListContainer);
            }
            else
            {
                item = CreateDefaultMarkerListItem();
                item.transform.SetParent(markerListContainer);
            }

            SetupMarkerListItem(item, markers[i], i);
            markerListItemObjects.Add(item);
        }
    }

    private void SetupMarkerListItem(GameObject item, MarkerData markerData, int index)
    {
        Text markerText = item.GetComponentInChildren<Text>();
        if (markerText != null)
        {
            markerText.text = $"#{index + 1} Pos: {markerData.position.ToString("F2")}";
        }

        Button deleteButton = item.GetComponent<Button>();
        if (deleteButton != null)
        {
            GameObject markerObj = markerData.gameObject;
            deleteButton.onClick.AddListener(() => DeleteMarker(markerObj));
        }

        Image colorImage = item.GetComponent<Image>();
        if (colorImage != null && EarthManager.Instance != null)
        {
            Color markerColor = EarthManager.Instance.markerColors[markerData.colorIndex];
            colorImage.color = new Color(markerColor.r, markerColor.g, markerColor.b, 0.3f);
        }
    }

    private GameObject CreateDefaultMarkerListItem()
    {
        GameObject item = new GameObject("MarkerListItem");

        RectTransform rectTransform = item.AddComponent<RectTransform>();
        rectTransform.sizeDelta = new Vector2(0, 30);

        Button button = item.AddComponent<Button>();

        Text text = item.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        text.fontSize = 12;
        text.color = Color.black;
        text.alignment = TextAnchor.MiddleLeft;

        RectTransform textRect = text.GetComponent<RectTransform>();
        textRect.SetParent(rectTransform);
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(10, 0);
        textRect.offsetMax = new Vector2(-10, 0);

        return item;
    }

    private void DeleteMarker(GameObject marker)
    {
        if (EarthManager.Instance != null)
        {
            EarthManager.Instance.RemoveMarker(marker);
        }
    }

    private void ClearAllMarkers()
    {
        if (EarthManager.Instance != null)
        {
            EarthManager.Instance.ClearAllMarkers();
        }
    }

    private void ClearMarkerListItemObjects()
    {
        foreach (GameObject item in markerListItemObjects)
        {
            Destroy(item);
        }

        markerListItemObjects.Clear();
    }

    #endregion

    #region Auto Cycle Toggle

    private void InitializeAutoCycleToggle()
    {
        if (autoCycleToggle == null || sunLightController == null)
            return;

        autoCycleToggle.isOn = true;

        autoCycleToggle.onValueChanged.AddListener(OnAutoCycleToggleChanged);

        UpdateAutoCycleToggleText(true);
    }

    private void OnAutoCycleToggleChanged(bool isOn)
    {
        if (sunLightController == null)
            return;

        sunLightController.ToggleAutoCycle(isOn);

        UpdateAutoCycleToggleText(isOn);

        Debug.Log($"Time auto-cycle: {(isOn ? "ON" : "OFF")}");
    }

    private void UpdateAutoCycleToggleDisplay()
    {
        if (autoCycleToggle == null || sunLightController == null)
            return;

        bool currentAutoCycle = sunLightController.IsAutoCycleEnabled;

        if (autoCycleToggle.isOn != currentAutoCycle)
        {
            autoCycleToggle.isOn = currentAutoCycle;
            UpdateAutoCycleToggleText(currentAutoCycle);
        }
    }

    private void UpdateAutoCycleToggleText(bool isOn)
    {
        if (autoCycleToggleText != null)
        {
            autoCycleToggleText.text = isOn ? "自动流动: 开" : "自动流动: 关";
        }
    }

    public void SetAutoCycle(bool enabled)
    {
        if (sunLightController != null)
        {
            sunLightController.ToggleAutoCycle(enabled);

            if (autoCycleToggle != null)
            {
                autoCycleToggle.isOn = enabled;
            }

            UpdateAutoCycleToggleText(enabled);
        }
    }

    #endregion

    #region Sliders

    private void InitializeSliders()
    {
        if (timeSlider != null && sunLightController != null)
        {
            timeSlider.minValue = 0f;
            timeSlider.maxValue = 24f;
            timeSlider.wholeNumbers = false;
            timeSlider.value = GetNormalizedTimeToHours(sunLightController.GetNormalizedTimeOfDay());

            timeSlider.onValueChanged.AddListener(OnTimeSliderChanged);

            if (timeSliderValue != null)
            {
                timeSliderValue.text = timeSlider.value.ToString("F1") + "h";
            }
        }

        if (seasonSlider != null && sunLightController != null)
        {
            seasonSlider.minValue = 0f;
            seasonSlider.maxValue = 40f;
            seasonSlider.wholeNumbers = true;
            seasonSlider.value = GetNormalizedSeasonToIndex(sunLightController.GetNormalizedDayOfYear());

            seasonSlider.onValueChanged.AddListener(OnSeasonSliderChanged);

            if (seasonSliderValue != null)
            {
                seasonSliderValue.text = FormatSeasonDisplay((int)seasonSlider.value);
            }
        }
    }

    private void OnTimeSliderChanged(float value)
    {
        if (isUpdatingTimeSlider || sunLightController == null)
            return;

        float normalizedTime = value / 24f;
        sunLightController.SetTimeOfDay(normalizedTime * sunLightController.DayDuration);

        if (timeSliderValue != null)
        {
            timeSliderValue.text = value.ToString("F1") + "h";
        }
    }

    private void OnSeasonSliderChanged(float value)
    {
        if (isUpdatingSeasonSlider || sunLightController == null)
            return;

        int seasonIndex = Mathf.RoundToInt(value);
        seasonIndex = Mathf.Clamp(seasonIndex, 0, 40);

        float normalizedYear = seasonIndex / 40f;
        sunLightController.SetDayOfYear(normalizedYear * sunLightController.YearDuration);

        if (seasonSliderValue != null)
        {
            seasonSliderValue.text = FormatSeasonDisplay(seasonIndex);
        }
    }

    private void UpdateSlidersDisplay()
    {
        if (timeSlider != null && !isUpdatingTimeSlider && sunLightController != null)
        {
            float currentHours = GetNormalizedTimeToHours(sunLightController.GetNormalizedTimeOfDay());

            if (Mathf.Abs(timeSlider.value - currentHours) > 0.01f)
            {
                isUpdatingTimeSlider = true;
                timeSlider.value = currentHours;
                isUpdatingTimeSlider = false;

                if (timeSliderValue != null)
                {
                    timeSliderValue.text = currentHours.ToString("F1") + "h";
                }
            }
        }

        if (seasonSlider != null && !isUpdatingSeasonSlider && sunLightController != null)
        {
            int currentIndex = GetNormalizedSeasonToIndex(sunLightController.GetNormalizedDayOfYear());

            if (Mathf.Abs(seasonSlider.value - currentIndex) > 0.5f)
            {
                isUpdatingSeasonSlider = true;
                seasonSlider.value = currentIndex;
                isUpdatingSeasonSlider = false;

                if (seasonSliderValue != null)
                {
                    seasonSliderValue.text = FormatSeasonDisplay(currentIndex);
                }
            }
        }
    }

    private float GetNormalizedTimeToHours(float normalizedTime)
    {
        return normalizedTime * 24f;
    }

    private int GetNormalizedSeasonToIndex(float normalizedYear)
    {
        return Mathf.RoundToInt(normalizedYear * 40f);
    }

    private string FormatSeasonDisplay(int seasonIndex)
    {
        int season = seasonIndex / 10;
        int subSeason = seasonIndex % 10;

        string[] seasonNames = new string[] { "春", "夏", "秋", "冬" };
        string seasonName = season < 4 ? seasonNames[season] : "春";

        return $"{seasonName}{subSeason}/10";
    }

    #endregion

    #region Tween

    private void InitializeTween()
    {
        inOutArea.AddListener(OnAreaIn, OnAreaOut);

        Vector2 anchoredPos = uiPanel.anchoredPosition;
        anchoredPos.x = -slideDistance;
        uiPanel.anchoredPosition = anchoredPos;
    }

    private void OnAreaIn()
    {
        Debug.Log($"Mouse entered: {gameObject.name}");
        if (isUIVisible) return;

        isUIVisible = true;

        if (slideCoroutine != null)
        {
            StopCoroutine(slideCoroutine);
        }

        slideCoroutine = StartCoroutine(SlideUI(true));
    }

    private void OnAreaOut()
    {
        Debug.Log($"Mouse exited: {gameObject.name}");
        if (!isUIVisible) return;

        isUIVisible = false;

        if (slideCoroutine != null)
        {
            StopCoroutine(slideCoroutine);
        }

        slideCoroutine = StartCoroutine(SlideUI(false));
    }

    private IEnumerator SlideUI(bool slideIn)
    {
        float elapsed = 0f;
        Vector2 startPos = uiPanel.anchoredPosition;
        Vector2 endPos = startPos;

        if (slideIn)
        {
            endPos.x = 0f;
        }
        else
        {
            endPos.x = -slideDistance;
        }

        while (elapsed < slideDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, elapsed / slideDuration);

            Vector2 currentPos = Vector2.Lerp(startPos, endPos, t);
            uiPanel.anchoredPosition = currentPos;

            yield return null;
        }

        uiPanel.anchoredPosition = endPos;
        slideCoroutine = null;
    }

    #endregion

    #region LoadMap

    private void InitializeImageList()
    {
        if (imageListContainer == null)
        {
            Debug.LogWarning("Image List Container not assigned! Creating list items will not work.");
        }
    }

    private void LoadImagesFromStreamingAssets()
    {
        string streamingPath = Application.streamingAssetsPath;

        if (!Directory.Exists(streamingPath))
        {
            Directory.CreateDirectory(streamingPath);
            Debug.Log($"Created StreamingAssets folder at: {streamingPath}");
        }

        availableImages.Clear();

        string[] imageFiles = Directory.GetFiles(streamingPath, "*.*", SearchOption.TopDirectoryOnly);

        foreach (string file in imageFiles)
        {
            string extension = Path.GetExtension(file).ToLower();

            if (extension == ".png" || extension == ".jpg" || extension == ".jpeg" ||
                extension == ".bmp" || extension == ".tga" || extension == ".tif" ||
                extension == ".tiff")
            {
                string fileName = Path.GetFileName(file);

                // Skip height map files — they are loaded automatically with their terrain map
                if (fileName.Contains("_height"))
                    continue;

                availableImages.Add(fileName);
            }
        }

        Debug.Log($"Found {availableImages.Count} images in StreamingAssets");

        UpdateImageListUI();
    }

    private void UpdateImageListUI()
    {
        ClearListItemObjects();

        if (imageListContainer == null || imageListItemPrefab == null)
        {
            Debug.LogWarning("Cannot create list items. Please assign Container and Prefab in Inspector.");
            return;
        }

        foreach (string imageName in availableImages)
        {
            var item = Instantiate(imageListItemPrefab, imageListContainer);
            var mapItem = item.GetComponent<MapItem>();

            mapItem.Initialize(Path.GetFileNameWithoutExtension(imageName), () => OnImageItemSelected(imageName));

            listItemObjects.Add(item);
        }
    }

    private void ClearListItemObjects()
    {
        foreach (GameObject item in listItemObjects)
        {
            Destroy(item);
        }

        listItemObjects.Clear();
    }

    private void OnImageItemSelected(string imageName)
    {
        string fullPath = Path.Combine(Application.streamingAssetsPath, imageName);

        Debug.Log($"Selected image: {imageName}");

        if (mapFileName != null)
        {
            mapFileName.text = Path.GetFileNameWithoutExtension(imageName);
        }

        LoadAndApplyTexture(fullPath);
    }

    private void LoadAndApplyTexture(string filePath)
    {
        StartCoroutine(LoadTextureCoroutine(filePath));
    }

    private IEnumerator LoadTextureCoroutine(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogError($"File not found: {filePath}");
            yield break;
        }

        byte[] fileData = File.ReadAllBytes(filePath);

        Texture2D newTexture = new Texture2D(2, 2);

        if (newTexture.LoadImage(fileData))
        {
            newTexture.filterMode = FilterMode.Bilinear;
            newTexture.wrapMode = TextureWrapMode.Repeat;

            if (currentTexture != null)
            {
                Destroy(currentTexture);
            }

            currentTexture = newTexture;

            EarthManager.Instance.SetEarthSurface(newTexture);

            // Auto-detect and load matching height map
            string baseName = Path.GetFileNameWithoutExtension(filePath);
            string heightPath = Path.Combine(
                Path.GetDirectoryName(filePath),
                baseName + "_height" + Path.GetExtension(filePath));

            if (File.Exists(heightPath))
            {
                byte[] heightData = File.ReadAllBytes(heightPath);
                Texture2D heightTex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                if (heightTex.LoadImage(heightData))
                {
                    heightTex.filterMode = FilterMode.Bilinear;
                    heightTex.wrapMode = TextureWrapMode.Repeat;
                    EarthManager.Instance.SetEarthHeightMap(heightTex);
                    Debug.Log($"Auto-loaded height map: {heightPath}");
                }
                else
                {
                    Destroy(heightTex);
                    EarthManager.Instance.ClearEarthHeightMap();
                }
            }
            else
            {
                EarthManager.Instance.ClearEarthHeightMap();
            }
        }
        else
        {
            Debug.LogError($"Failed to load image: {filePath}");
            Destroy(newTexture);
        }

        yield return null;
    }

    #endregion

    #region SetEarth

    private void InitializeEarth()
    {
        northButton.onClick.AddListener(OnNorthPoleButtonClick);
        equatorButton.onClick.AddListener(OnEquatorButtonClick);
        southButton.onClick.AddListener(OnSouthPoleButtonClick);
        topButton.onClick.AddListener(OnTopButtonClick);
    }

    private void OnEquatorButtonClick()
    {
        cameraController.SetEquatorView();
    }

    private void OnNorthPoleButtonClick()
    {
        cameraController.SetNorthPoleView();
    }

    private void OnSouthPoleButtonClick()
    {
        cameraController.SetSouthPoleView();
    }

    private void OnTopButtonClick()
    {
        cameraController.SetTopDownView();
    }

    #endregion

    #region 世界生成

    private void InitializeWorldGeneration()
    {
        if (generateButton != null)
        {
            generateButton.onClick.AddListener(OnGenerateButtonClicked);
        }

        if (WorldTerrainGenerator.Instance != null)
        {
            WorldTerrainGenerator.Instance.onProgress = OnGenerationProgress;
            WorldTerrainGenerator.Instance.onComplete = OnGenerationComplete;
            WorldTerrainGenerator.Instance.onError = OnGenerationError;
        }

        if (waterCoverageSlider != null)
        {
            waterCoverageSlider.minValue = 0.1f;
            waterCoverageSlider.maxValue = 0.9f;
            waterCoverageSlider.wholeNumbers = false;
            waterCoverageSlider.value = 0.5f;

            waterCoverageSlider.onValueChanged.AddListener(OnWaterCoverageSliderChanged);

            if (waterCoverageValueText != null)
            {
                waterCoverageValueText.text = $"{waterCoverageSlider.value * 100:F0}%";
            }
        }
    }

    private void OnGenerateButtonClicked()
    {
        int seed = 0;

        if (seedInputField != null && !string.IsNullOrEmpty(seedInputField.text))
        {
            if (int.TryParse(seedInputField.text, out int parsed))
                seed = parsed;
        }

        if (seed == 0)
        {
            seed = System.Environment.TickCount;
            seedInputField.SetTextWithoutNotify(seed.ToString());
        }

        float waterCoverage = waterCoverageSlider != null ? waterCoverageSlider.value : 0.5f;

        if (generationProgressText != null)
            generationProgressText.text = "生成中...";

        if (WorldTerrainGenerator.Instance != null)
        {
            WorldTerrainGenerator.Instance.GenerateWorld(seed, 0, waterCoverage);
        }
        else
        {
            Debug.LogError("WorldTerrainGenerator.Instance is null!");
        }
    }

    private void OnWaterCoverageSliderChanged(float value)
    {
        if (waterCoverageValueText != null)
        {
            waterCoverageValueText.text = $"{value * 100:F0}%";
        }
    }

    private void OnGenerationProgress(float progress)
    {
        if (generationProgressText != null)
            generationProgressText.text = $"生成中... {progress * 100:F0}%";
    }

    private void OnGenerationComplete(string path)
    {
        if (generationProgressText != null)
            generationProgressText.text = "生成完成";

        Debug.Log($"World generation complete: {path}");

        // Refresh image list to include the newly generated map
        LoadImagesFromStreamingAssets();
    }

    private void OnGenerationError(string error)
    {
        if (generationProgressText != null)
            generationProgressText.text = "生成失败";

        Debug.LogError($"World generation error: {error}");
    }

    #endregion
}