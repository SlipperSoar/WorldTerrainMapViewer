using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;
#if UNITY_EDITOR
using UnityEditor;
#endif

[RequireComponent(typeof(UIDocument))]
[ExecuteAlways]
public class UIController : MonoBehaviour
{
    #region Properties

    [Header("References")]
    [SerializeField] private CameraController cameraController;
    [SerializeField] private SunLightController sunLightController;
    [SerializeField] private SunlightDirectionController sunlightDirectionController;

    // UIToolkit elements
    private VisualElement _root;
    private VisualElement _slidePanel;
    private VisualElement _hoverZone;
    private VisualElement _hoverArrow;
    private ScrollView _mapList;
    private Label _mapFileName;
    private TextField _seedInput;
    private Button _seedRandomBtn;
    private Slider _waterSlider;
    private Label _waterValue;
    private Button _generateButton;
    private Label _genProgress;
    private ScrollView _markerList;
    private Slider _timeSlider;
    private Label _timeValue;
    private Toggle _autoCycleToggle;
    private Slider _speedSlider;
    private Label _speedValue;
    private Toggle _arrowToggle;
    private Slider _seasonSlider;
    private Label _seasonValue;
    private Button _exitButton;
    private Button _northButton;
    private Button _equatorButton;
    private Button _southButton;
    private Button _topButton;
    private Label _rotationValue;
    private Label _timeStatus;
    private Label _seasonStatus;
    private VisualElement _colorPalette;
    private Label _currentColorLabel;
    private VisualElement _currentColorSwatch;
    private Button _clearMarkersButton;
    private int _lastColorIndex = -1;

    // State
    private bool _isUpdatingTimeSlider = false;
    private bool _isUpdatingSeasonSlider = false;
    private List<VisualElement> _mapItems = new List<VisualElement>();
    private List<VisualElement> _markerItems = new List<VisualElement>();
    private Texture2D _currentTexture;
    private bool _isPanelVisible = false;
    private bool _initialized = false;

    #endregion

    #region Unity

    private void OnEnable()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            // Edit mode: defer — UIDocument.rootVisualElement may not be ready yet
            EditorApplication.delayCall -= TryInitialize;
            EditorApplication.delayCall += TryInitialize;
        }
        else
        {
            TryInitialize();
        }
#else
        TryInitialize();
#endif
    }

    private void OnDisable()
    {
#if UNITY_EDITOR
        EditorApplication.delayCall -= TryInitialize;
#endif
        _initialized = false;
    }

    private void TryInitialize()
    {
        if (_initialized) return;

        var uiDocument = GetComponent<UIDocument>();
        if (uiDocument == null || uiDocument.rootVisualElement == null)
            return;

        _root = uiDocument.rootVisualElement;
        QueryElements();
        RegisterCallbacks();
        InitializeSliders();
        InitializeSeasonSliderColors();
        InitializeAutoCycleToggle();
        InitializeSpeedSlider();
        InitializeArrowToggle();
        InitializeColorPalette();
        InitializeWorldGeneration();
        InitializeViewButtons();
        InitializeExitButton();
        InitializeHoverZone();
        _initialized = true;
    }

    private void Start()
    {
        if (!_initialized)
            TryInitialize();

        // Reload color palette when EarthManager becomes available
        if (EarthManager.Instance != null && _colorPalette != null)
            InitializeColorPalette();
        if (_mapList != null)
            LoadImagesFromStreamingAssets();
    }

    private void Update()
    {
        if (!_initialized)
        {
            TryInitialize();
            if (!_initialized) return;
        }

        UpdateCameraRotationDisplay();
        UpdateTimeDisplay();
        UpdateSlidersDisplay();
        UpdateAutoCycleToggleDisplay();
        UpdateMarkerListDisplay();
        UpdateCurrentColorDisplay();
        UpdateHoverPanel();
    }

    private void OnDestroy()
    {
        if (_currentTexture != null)
            Destroy(_currentTexture);
    }

    #endregion

    #region Query & Wire

    private void QueryElements()
    {
        _slidePanel = _root.Q<VisualElement>("slide-panel");
        _hoverZone = _root.Q<VisualElement>("hover-zone");
        _hoverArrow = _root.Q<VisualElement>("hover-arrow");
        _mapList = _root.Q<ScrollView>("map-list");
        _mapFileName = _root.Q<Label>("map-file-name");
        _seedInput = _root.Q<TextField>("seed-input");
        _seedRandomBtn = _root.Q<Button>("seed-random-btn");
        _waterSlider = _root.Q<Slider>("water-slider");
        _waterValue = _root.Q<Label>("water-value");
        _generateButton = _root.Q<Button>("generate-button");
        _genProgress = _root.Q<Label>("gen-progress-label");
        _markerList = _root.Q<ScrollView>("marker-list");
        _timeSlider = _root.Q<Slider>("time-slider");
        _timeValue = _root.Q<Label>("time-value");
        _autoCycleToggle = _root.Q<Toggle>("autocycle-toggle");
        _speedSlider = _root.Q<Slider>("speed-slider");
        _speedValue = _root.Q<Label>("speed-value");
        _arrowToggle = _root.Q<Toggle>("arrow-toggle");
        _seasonSlider = _root.Q<Slider>("season-slider");
        _seasonValue = _root.Q<Label>("season-value");
        _exitButton = _root.Q<Button>("exit-button");
        _northButton = _root.Q<Button>("north-button");
        _equatorButton = _root.Q<Button>("equator-button");
        _southButton = _root.Q<Button>("south-button");
        _topButton = _root.Q<Button>("top-button");
        _rotationValue = _root.Q<Label>("rotation-value");
        _timeStatus = _root.Q<Label>("time-status");
        _seasonStatus = _root.Q<Label>("season-status");
        _colorPalette = _root.Q<VisualElement>("color-palette");
        _currentColorLabel = _root.Q<Label>("current-color-label");
        _currentColorSwatch = _root.Q<VisualElement>("current-color-swatch");
        _clearMarkersButton = _root.Q<Button>("clear-markers-button");
    }

    private void RegisterCallbacks()
    {
        _clearMarkersButton.clicked += ClearAllMarkers;
    }

    #endregion

    #region Display

    private void UpdateCameraRotationDisplay()
    {
        if (cameraController == null || _rotationValue == null)
            return;

        float horizontal = cameraController.GetHorizontalRotation();
        float vertical = cameraController.GetVerticalRotation();
        _rotationValue.text = $"H: {horizontal:F1}° V: {vertical:F1}°";
    }

    private void UpdateTimeDisplay()
    {
        if (sunLightController == null)
            return;

        if (_timeStatus != null)
            _timeStatus.text = sunLightController.GetTimeOfDayName();

        if (_seasonStatus != null)
            _seasonStatus.text = sunLightController.GetCurrentSeasonName();
    }

    #endregion

    #region Hover Zone / Slide Panel

    private void InitializeHoverZone()
    {
        // Start hidden by translating off-screen
        _slidePanel.style.translate = new Translate(-340, 0, 0);
    }

    private void UpdateHoverPanel()
    {
        if (_slidePanel == null || _root == null)
            return;

        Vector2 mousePos = Input.mousePosition;
        float triggerWidth = Screen.width * 0.02f;
        bool mouseInTrigger = mousePos.x >= 0 && mousePos.x <= triggerWidth;
        bool mouseOverPanel = _isPanelVisible && MouseOverSlidePanel(mousePos);

        bool shouldShow = mouseInTrigger || mouseOverPanel;

        if (shouldShow != _isPanelVisible)
        {
            _isPanelVisible = shouldShow;
            _slidePanel.style.translate = _isPanelVisible
                ? new Translate(0, 0, 0)
                : new Translate(-340, 0, 0);
            if (_hoverArrow != null)
                _hoverArrow.style.display = _isPanelVisible ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }

    private bool MouseOverSlidePanel(Vector2 mousePos)
    {
        if (_slidePanel == null) return false;

        // UIToolkit uses top-left origin; Input.mousePosition uses bottom-left
        float screenH = Screen.height;
        Vector2 uiPos = new Vector2(mousePos.x, screenH - mousePos.y);

        var layout = _slidePanel.layout;
        // When visible, the panel is at left=0, so layout.x ≈ 0
        // Check if mouse is within the panel bounds
        return uiPos.x >= layout.x && uiPos.x <= layout.x + layout.width &&
               uiPos.y >= layout.y && uiPos.y <= layout.y + layout.height;
    }

    #endregion

    #region Color Palette

    private static readonly Color[] FALLBACK_COLORS = new Color[]
    {
        Color.red,
        Color.blue,
        Color.green,
        Color.yellow,
        Color.magenta,
        Color.cyan,
        Color.white,
        new Color(1f, 0.5f, 0f),
        new Color(0.5f, 0f, 1f),
        new Color(0f, 1f, 0.5f)
    };

    private void InitializeColorPalette()
    {
        Color[] colors = EarthManager.Instance != null
            ? EarthManager.Instance.markerColors
            : FALLBACK_COLORS;

        _colorPalette.Clear();
        for (int i = 0; i < colors.Length; i++)
        {
            Color color = colors[i];
            int index = i;

            var btn = new Button();
            btn.AddToClassList("color-button");
            btn.style.backgroundColor = color;

            // Number label overlay (1-9 then 0)
            int displayNum = (i == 9) ? 0 : i + 1;
            var numLabel = new Label(displayNum.ToString());
            numLabel.AddToClassList("color-number");
            numLabel.style.color = GetReadableTextColor(color);
            btn.Add(numLabel);

            btn.clicked += () =>
            {
                if (EarthManager.Instance != null)
                    EarthManager.Instance.SetCurrentColorIndex(index);
                UpdateColorPaletteSelection();
            };

            _colorPalette.Add(btn);
        }

        UpdateColorPaletteSelection();
    }

    private void UpdateColorPaletteSelection()
    {
        int currentIndex = EarthManager.Instance != null
            ? EarthManager.Instance.GetCurrentColorIndex()
            : 0;

        for (int i = 0; i < _colorPalette.childCount; i++)
        {
            var child = _colorPalette[i];
            if (i == currentIndex)
                child.AddToClassList("color-button--selected");
            else
                child.RemoveFromClassList("color-button--selected");
        }
    }

    private static Color GetReadableTextColor(Color backgroundColor)
    {
        float luminance = backgroundColor.r * 0.299f + backgroundColor.g * 0.587f + backgroundColor.b * 0.114f;
        return luminance > 0.5f ? Color.black : Color.white;
    }

    private void UpdateCurrentColorDisplay()
    {
        int colorIndex = EarthManager.Instance != null
            ? EarthManager.Instance.GetCurrentColorIndex()
            : 0;
        Color color = EarthManager.Instance != null
            ? EarthManager.Instance.GetCurrentColor()
            : FALLBACK_COLORS[colorIndex];

        if (_currentColorLabel != null)
            _currentColorLabel.text = $"颜色: {colorIndex}";

        if (_currentColorSwatch != null)
            _currentColorSwatch.style.backgroundColor = color;

        if (colorIndex != _lastColorIndex)
        {
            _lastColorIndex = colorIndex;
            UpdateColorPaletteSelection();
        }
    }

    #endregion

    #region Marker System

    private void ClearAllMarkers()
    {
        if (EarthManager.Instance != null)
            EarthManager.Instance.ClearAllMarkers();
    }

    private void UpdateMarkerListDisplay()
    {
        if (EarthManager.Instance == null || _markerList == null)
            return;

        var allMarkers = EarthManager.Instance.GetAllMarkers();
        if (allMarkers.Count != _markerItems.Count)
            RefreshMarkerList(allMarkers);
    }

    private void RefreshMarkerList(List<MarkerData> markers)
    {
        _markerList.Clear();
        _markerItems.Clear();

        for (int i = 0; i < markers.Count; i++)
        {
            var item = CreateMarkerItem(markers[i], i);
            _markerList.Add(item);
            _markerItems.Add(item);
        }
    }

    private VisualElement CreateMarkerItem(MarkerData markerData, int index)
    {
        var container = new VisualElement();
        container.AddToClassList("marker-item");

        // Color dot
        var dot = new VisualElement();
        dot.AddToClassList("marker-color-dot");
        if (EarthManager.Instance != null)
            dot.style.backgroundColor = EarthManager.Instance.markerColors[markerData.colorIndex];
        container.Add(dot);

        // Label
        var label = new Label($"#{index + 1}  {markerData.position.ToString("F2")}");
        container.Add(label);

        // Delete button
        var deleteBtn = new Button();
        deleteBtn.AddToClassList("marker-delete-btn");
        deleteBtn.text = "删除";
        GameObject markerObj = markerData.gameObject;
        deleteBtn.clicked += () =>
        {
            if (EarthManager.Instance != null)
                EarthManager.Instance.RemoveMarker(markerObj);
        };
        container.Add(deleteBtn);

        return container;
    }

    #endregion

    #region Auto Cycle Toggle

    private void InitializeAutoCycleToggle()
    {
        if (_autoCycleToggle == null || sunLightController == null)
            return;

        _autoCycleToggle.value = true;
        _autoCycleToggle.RegisterValueChangedCallback(evt =>
        {
            sunLightController.ToggleAutoCycle(evt.newValue);
        });
    }

    private void InitializeSpeedSlider()
    {
        if (_speedSlider == null || sunLightController == null)
            return;

        _speedSlider.value = sunLightController.SpeedMultiplier;
        _speedSlider.RegisterValueChangedCallback(evt =>
        {
            sunLightController.SetSpeedMultiplier(evt.newValue);
            if (_speedValue != null)
                _speedValue.text = evt.newValue.ToString("F1") + "x";
        });
        if (_speedValue != null)
            _speedValue.text = _speedSlider.value.ToString("F1") + "x";
    }

    private void InitializeArrowToggle()
    {
        if (_arrowToggle == null)
            return;

        var arrowLabel = _arrowToggle.Q<Label>();
        if (arrowLabel != null)
        {
            arrowLabel.style.color = new Color(0.91f, 0.91f, 0.91f, 1f);
            arrowLabel.style.fontSize = 12;
            arrowLabel.style.whiteSpace = WhiteSpace.NoWrap;
        }

        _arrowToggle.value = true;
        _arrowToggle.RegisterValueChangedCallback(evt =>
        {
            if (sunlightDirectionController != null)
                sunlightDirectionController.SetArrowVisible(evt.newValue);
        });
    }

    private void UpdateAutoCycleToggleDisplay()
    {
        if (_autoCycleToggle == null || sunLightController == null)
            return;

        bool currentAutoCycle = sunLightController.IsAutoCycleEnabled;
        if (_autoCycleToggle.value != currentAutoCycle)
            _autoCycleToggle.SetValueWithoutNotify(currentAutoCycle);
    }

    public void SetAutoCycle(bool enabled)
    {
        if (sunLightController != null)
        {
            sunLightController.ToggleAutoCycle(enabled);
            _autoCycleToggle?.SetValueWithoutNotify(enabled);
        }
    }

    #endregion

    #region Sliders

    private void InitializeSliders()
    {
        if (_timeSlider != null && sunLightController != null)
        {
            _timeSlider.lowValue = 0f;
            _timeSlider.highValue = 24f;
            _timeSlider.value = GetNormalizedTimeToHours(sunLightController.GetNormalizedTimeOfDay());
            _timeSlider.RegisterValueChangedCallback(evt =>
            {
                if (_isUpdatingTimeSlider || sunLightController == null)
                    return;

                float normalizedTime = evt.newValue / 24f;
                sunLightController.SetTimeOfDay(normalizedTime * sunLightController.DayDuration);
                _timeValue.text = evt.newValue.ToString("F1") + "h";
            });
            _timeValue.text = _timeSlider.value.ToString("F1") + "h";
        }

        if (_seasonSlider != null && sunLightController != null)
        {
            _seasonSlider.lowValue = 0f;
            _seasonSlider.highValue = 40f;
            _seasonSlider.value = GetNormalizedSeasonToIndex(sunLightController.GetNormalizedDayOfYear());
            _seasonSlider.RegisterValueChangedCallback(evt =>
            {
                if (_isUpdatingSeasonSlider || sunLightController == null)
                    return;

                int seasonIndex = Mathf.RoundToInt(evt.newValue);
                seasonIndex = Mathf.Clamp(seasonIndex, 0, 40);
                float normalizedYear = seasonIndex / 40f;
                sunLightController.SetDayOfYear(normalizedYear * sunLightController.YearDuration);
                _seasonValue.text = FormatSeasonDisplay(seasonIndex);
            });
            _seasonValue.text = FormatSeasonDisplay((int)_seasonSlider.value);
        }
    }

    private void InitializeSeasonSliderColors()
    {
        if (_seasonSlider == null) return;

        var track = _seasonSlider.Q<VisualElement>(className: "unity-base-slider__tracker");
        if (track == null) return;

        var bg = new VisualElement();
        bg.style.flexDirection = FlexDirection.Row;
        bg.style.position = Position.Absolute;
        bg.style.left = 0;
        bg.style.right = 0;
        bg.style.top = 0;
        bg.style.bottom = 0;
        bg.style.overflow = Overflow.Hidden;

        Color[] seasonColors = { new Color(0.3f, 0.7f, 0.2f, 0.6f), new Color(0.8f, 0.3f, 0.2f, 0.6f), new Color(0.8f, 0.6f, 0.1f, 0.6f), new Color(0.3f, 0.5f, 0.8f, 0.6f) };
        for (int i = 0; i < 4; i++)
        {
            var section = new VisualElement();
            section.style.flexGrow = 1;
            section.style.backgroundColor = seasonColors[i];
            bg.Add(section);
        }

        track.Add(bg);
        bg.SendToBack();
    }

    private void UpdateSlidersDisplay()
    {
        if (_timeSlider != null && !_isUpdatingTimeSlider && sunLightController != null)
        {
            float currentHours = GetNormalizedTimeToHours(sunLightController.GetNormalizedTimeOfDay());
            if (Mathf.Abs(_timeSlider.value - currentHours) > 0.01f)
            {
                _isUpdatingTimeSlider = true;
                _timeSlider.SetValueWithoutNotify(currentHours);
                _isUpdatingTimeSlider = false;
                _timeValue.text = currentHours.ToString("F1") + "h";
            }
        }

        if (_seasonSlider != null && !_isUpdatingSeasonSlider && sunLightController != null)
        {
            int currentIndex = GetNormalizedSeasonToIndex(sunLightController.GetNormalizedDayOfYear());
            if (Mathf.Abs(_seasonSlider.value - currentIndex) > 0.5f)
            {
                _isUpdatingSeasonSlider = true;
                _seasonSlider.SetValueWithoutNotify(currentIndex);
                _isUpdatingSeasonSlider = false;
                _seasonValue.text = FormatSeasonDisplay(currentIndex);
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

    #region View Buttons

    private void InitializeViewButtons()
    {
        _northButton.clicked += OnNorthPoleButtonClick;
        _equatorButton.clicked += OnEquatorButtonClick;
        _southButton.clicked += OnSouthPoleButtonClick;
        _topButton.clicked += OnTopButtonClick;
    }

    private void OnEquatorButtonClick() => cameraController.SetEquatorView();
    private void OnNorthPoleButtonClick() => cameraController.SetNorthPoleView();
    private void OnSouthPoleButtonClick() => cameraController.SetSouthPoleView();
    private void OnTopButtonClick() => cameraController.SetTopDownView();

    #endregion

    #region Exit

    private void InitializeExitButton()
    {
        _exitButton.clicked += Application.Quit;
    }

    #endregion

    #region LoadMap

    private void LoadImagesFromStreamingAssets()
    {
        string streamingPath = Application.streamingAssetsPath;
        if (!Directory.Exists(streamingPath))
            Directory.CreateDirectory(streamingPath);

        var availableImages = new List<string>();
        string[] imageFiles = Directory.GetFiles(streamingPath, "*.*", SearchOption.TopDirectoryOnly);
        foreach (string file in imageFiles)
        {
            string extension = Path.GetExtension(file).ToLower();
            if (extension == ".png" || extension == ".jpg" || extension == ".jpeg" ||
                extension == ".bmp" || extension == ".tga" || extension == ".tif" ||
                extension == ".tiff")
            {
                string fileName = Path.GetFileName(file);
                if (fileName.Contains("_height"))
                    continue;
                availableImages.Add(fileName);
            }
        }

        Debug.Log($"Found {availableImages.Count} images in StreamingAssets");
        UpdateImageListUI(availableImages);
    }

    private void UpdateImageListUI(List<string> images)
    {
        _mapList.Clear();
        _mapItems.Clear();

        foreach (string imageName in images)
        {
            var btn = new Button();
            btn.AddToClassList("map-item");
            btn.text = Path.GetFileNameWithoutExtension(imageName);
            string capturedName = imageName;
            btn.clicked += () => OnImageItemSelected(capturedName);
            _mapList.Add(btn);
            _mapItems.Add(btn);
        }

        if (images.Count == 0)
        {
            var empty = new Label("(暂无地图)");
            empty.AddToClassList("empty-text");
            _mapList.Add(empty);
        }
    }

    private void OnImageItemSelected(string imageName)
    {
        string fullPath = Path.Combine(Application.streamingAssetsPath, imageName);
        Debug.Log($"Selected image: {imageName}");
        _mapFileName.text = Path.GetFileNameWithoutExtension(imageName);
        StartCoroutine(LoadTextureCoroutine(fullPath));
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
            if (_currentTexture != null)
                Destroy(_currentTexture);
            _currentTexture = newTexture;
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

    #region World Generation

    private void InitializeWorldGeneration()
    {
        if (WorldTerrainGenerator.Instance != null)
        {
            WorldTerrainGenerator.Instance.onProgress = OnGenerationProgress;
            WorldTerrainGenerator.Instance.onComplete = OnGenerationComplete;
            WorldTerrainGenerator.Instance.onError = OnGenerationError;
        }

        if (_waterSlider != null)
        {
            _waterSlider.lowValue = 0.1f;
            _waterSlider.highValue = 0.9f;
            _waterSlider.value = 0.5f;
            _waterSlider.RegisterValueChangedCallback(evt =>
            {
                _waterValue.text = $"{evt.newValue * 100:F0}%";
            });
            _waterValue.text = $"{_waterSlider.value * 100:F0}%";
        }

        if (_generateButton != null)
        {
            _generateButton.clicked += OnGenerateButtonClicked;
        }

        if (_seedRandomBtn != null)
        {
            _seedRandomBtn.clicked += () =>
            {
                int seed = System.Environment.TickCount;
                _seedInput.SetValueWithoutNotify(seed.ToString());
            };
        }
    }

    private void OnGenerateButtonClicked()
    {
        int seed = 0;
        if (_seedInput != null && !string.IsNullOrEmpty(_seedInput.text))
        {
            if (int.TryParse(_seedInput.text, out int parsed))
                seed = parsed;
        }

        if (seed == 0)
        {
            seed = System.Environment.TickCount;
            _seedInput.SetValueWithoutNotify(seed.ToString());
        }

        float waterCoverage = _waterSlider != null ? _waterSlider.value : 0.5f;
        if (_genProgress != null)
            _genProgress.text = "生成中...";

        if (WorldTerrainGenerator.Instance != null)
        {
            WorldTerrainGenerator.Instance.onProgress = OnGenerationProgress;
            WorldTerrainGenerator.Instance.onComplete = OnGenerationComplete;
            WorldTerrainGenerator.Instance.onError = OnGenerationError;
            WorldTerrainGenerator.Instance.GenerateWorld(seed, 0, waterCoverage);
        }
        else
        {
            Debug.LogError("WorldTerrainGenerator.Instance is null!");
        }
    }

    private void OnGenerationProgress(float progress)
    {
        if (_genProgress != null)
            _genProgress.text = $"生成中... {progress * 100:F0}%";
    }

    private void OnGenerationComplete(string path)
    {
        if (_genProgress != null)
            _genProgress.text = "生成完成";
        Debug.Log($"World generation complete: {path}");
        LoadImagesFromStreamingAssets();
    }

    private void OnGenerationError(string error)
    {
        if (_genProgress != null)
            _genProgress.text = "生成失败";
        Debug.LogError($"World generation error: {error}");
    }

    #endregion
}
