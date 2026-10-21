using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;
using WorldTerrain;
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
    private Slider _tiltSlider;
    private Label _tiltValue;
    private Slider _planetSizeSlider;
    private Label _planetSizeValue;
    private Toggle _retrogradeToggle;
    private Slider _resolutionSlider;
    private Label _resolutionValue;
    private Label _resolutionHint;
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
    private Button _drawModeButton;
    private Button _undoLineButton;
    private Button _clearLinesButton;
    private Button _saveLinesButton;
    private Toggle _plateOverlayToggle;
    private Toggle _terrainDisplacementToggle;
    private Toggle _cloudLayerToggle;
    private Toggle _humanOverlayToggle;
    private Button _exportNamesButton;
    private Button _importNamesButton;
    private int _lastColorIndex = -1;
    // State
    private bool _isUpdatingTimeSlider = false;
    private bool _isUpdatingSeasonSlider = false;
    private float _pendingAxialTilt = MapMetaData.DefaultAxialTilt;
    private float _pendingPlanetRadiusKm = PlanetProfile.DefaultRadiusKm;
    private bool _pendingRetrograde;
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
        InitializePlateOverlayToggle();
        InitializeTerrainDisplacementToggle();
        InitializeCloudLayerToggle();
        InitializeHumanOverlayToggle();

        // UXML 里已有的两个开关也紧凑化（放在最后：新插入的开关此时都已就位）
        MakeToggleCompact(_autoCycleToggle);
        MakeToggleCompact(_arrowToggle);

        // 侧边栏的「逆向自转」也是 UXML 里的 Toggle，同样要紧凑化 ——
        // 否则内置 label 的定宽 + 输入区 0 宽会把勾选框裁掉（见 CompactToggleInternals）
        MakeToggleCompact(_retrogradeToggle);

        // 布局自检：顶栏宽度超出可用空间时会压到右侧栏，这里一次性报出来
        _root.schedule.Execute(ReportTopBarLayout).StartingIn(200);
        InitializeNameTransferButtons();
        InitializeColorPalette();
        InitializeDrawModeButtons();
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

        // Sync state when EarthManager becomes available
        if (EarthManager.Instance != null)
        {
            if (_colorPalette != null)
                InitializeColorPalette();
            if (_terrainDisplacementToggle != null)
                EarthManager.Instance.SetTerrainDisplacement(_terrainDisplacementToggle.value);
        }
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
        HandleDrawModeShortcuts();
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
        _tiltSlider = _root.Q<Slider>("tilt-slider");
        _tiltValue = _root.Q<Label>("tilt-value");
        _planetSizeSlider = _root.Q<Slider>("planet-size-slider");
        _planetSizeValue = _root.Q<Label>("planet-size-value");
        _retrogradeToggle = _root.Q<Toggle>("retrograde-toggle");
        _resolutionSlider = _root.Q<Slider>("resolution-slider");
        _resolutionValue = _root.Q<Label>("resolution-value");
        _resolutionHint = _root.Q<Label>("resolution-hint");
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

        // ScaleWithScreenSize: UIToolkit layout is at reference resolution (1920x1080),
        // Input.mousePosition is in physical screen pixels. Convert mouse to UI space.
        float panelH = _root.panel.visualTree.layout.height;
        float scale = panelH > 0 ? (float)Screen.height / panelH : 1f;
        Vector2 mousePos = Input.mousePosition;
        // Convert from screen bottom-left to UI top-left coordinate space
        Vector2 uiMousePos = new Vector2(mousePos.x / scale, (Screen.height - mousePos.y) / scale);

        float panelWidth = _slidePanel.resolvedStyle.width > 0
            ? _slidePanel.resolvedStyle.width
            : 340f;
        bool mouseInTrigger = uiMousePos.x >= 0 && uiMousePos.x <= panelWidth;
        bool mouseOverPanel = _isPanelVisible && MouseOverSlidePanel(uiMousePos);

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

    private bool MouseOverSlidePanel(Vector2 uiMousePos)
    {
        if (_slidePanel == null) return false;

        var bound = _slidePanel.worldBound;
        return uiMousePos.x >= bound.xMin && uiMousePos.x <= bound.xMax &&
               uiMousePos.y >= bound.yMin && uiMousePos.y <= bound.yMax;
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

        if (EarthManager.Instance != null)
            EarthManager.Instance.UpdateAllMarkerLabels();
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

        // Index label (non-editable)
        var indexLabel = new Label($"#{index + 1}");
        indexLabel.AddToClassList("marker-index-label");
        container.Add(indexLabel);

        // Editable name field
        var nameField = new TextField();
        nameField.AddToClassList("marker-name-field");
        nameField.value = markerData.name;
        GameObject markerObj = markerData.gameObject;
        nameField.RegisterCallback<FocusOutEvent>(evt =>
        {
            string name = nameField.value.Trim();
            if (EarthManager.Instance != null)
                EarthManager.Instance.SetMarkerName(markerObj, name);
            nameField.SetValueWithoutNotify(name);
        });
        container.Add(nameField);

        // Delete button
        var deleteBtn = new Button();
        deleteBtn.AddToClassList("marker-delete-btn");
        deleteBtn.text = "删除";
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

    /// <summary>
    /// 造一个紧凑开关（用于顶栏）。
    ///
    /// **只调间距，不增删 Toggle 内部元素**。
    ///
    /// 实测结构（Tuanjie 2022.3，见 <see cref="ReportToggleLayout"/> 打印）：
    ///   Toggle
    ///     [0] Label          class=unity-base-field__label,unity-toggle__label   默认宽 150px（文字只占约 28px）
    ///     [1] VisualElement  class=unity-base-field__input,unity-toggle__input   **默认宽 0**
    ///           [1.0] VisualElement name="unity-checkmark"                      24×24，被父级 0 宽裁掉 → 看不见
    ///
    /// 所以「看不见勾选框」和「文字与勾选框之间大片空白」是同一个原因：
    /// 输入区宽度为 0，而 label 被默认样式的 padding 撑到 150px。
    /// 这里给输入区固定宽度、把 label 的 padding 收掉。
    /// </summary>
    private static Toggle CreateCompactToggle(string text, string name, bool value)
    {
        var toggle = new Toggle(text)
        {
            name = name,
            value = value,
            style =
            {
                fontSize = 11,
                flexShrink = 0,
                flexGrow = 0,
                marginLeft = 0,
                marginRight = 0,
                paddingLeft = 0,
                paddingRight = 0,
                flexDirection = FlexDirection.Row,
                alignItems = Align.Center
            }
        };

        CompactToggleInternals(toggle);
        return toggle;
    }

    /// <summary>
    /// 统一处理 Toggle 内部那两层的尺寸（label 收窄、输入区给宽度）。
    /// UXML 里已有的 Toggle 也走这里。
    /// </summary>
    private static void CompactToggleInternals(Toggle toggle)
    {
        if (toggle == null)
            return;

        // ① 标签：实测默认宽 150px（文字只占约 28px），且 pl/pr 都是 0 ——
        //    说明是默认样式把宽度定死了，必须显式放回「按内容自适应」。
        Label label = toggle.labelElement;
        if (label != null)
        {
            label.style.width = StyleKeyword.Auto;
            label.style.minWidth = 0;
            label.style.maxWidth = 10000f;      // 用一个大数明确解除上限（比 StyleKeyword.None 稳妥）
            label.style.flexBasis = StyleKeyword.Auto;
            label.style.flexGrow = 0;
            label.style.flexShrink = 0;
            label.style.paddingLeft = 0;
            label.style.paddingRight = 0;
            label.style.marginLeft = 0;
            label.style.marginRight = 2;
            label.style.whiteSpace = WhiteSpace.NoWrap;
        }

        // ② 输入区（勾选框的父级）：实测宽度为 0，必须给一个明确宽度，否则里面的勾选框被裁掉
        VisualElement input = FindChildByClass(toggle, "unity-toggle__input");
        if (input == null && toggle.childCount > 1)
            input = toggle[1];                       // 结构固定时它就是第 2 个子元素

        if (input != null)
        {
            input.style.width = CheckmarkSize;
            input.style.minWidth = CheckmarkSize;
            input.style.maxWidth = CheckmarkSize;
            input.style.flexGrow = 0;
            input.style.flexShrink = 0;
            input.style.marginLeft = 4;
            input.style.marginRight = 0;
            input.style.paddingLeft = 0;
            input.style.paddingRight = 0;
            input.style.alignItems = Align.Center;
            input.style.justifyContent = Justify.Center;
        }
    }

    /// <summary>勾选框的边长（实测 24×24，输入区给这个宽度它才不会溢出）。</summary>
    private const float CheckmarkSize = 24f;

    /// <summary>按 USS 类名找直接子元素（避免依赖子元素下标）。</summary>
    private static VisualElement FindChildByClass(VisualElement parent, string className)
    {
        if (parent == null)
            return null;

        for (int i = 0; i < parent.childCount; i++)
        {
            VisualElement child = parent[i];
            if (child.ClassListContains(className))
                return child;
        }
        return null;
    }

    /// <summary>
    /// 把 UXML 里已有的 Toggle 也改造成紧凑版。
    /// （UXML 里直接写的 Toggle 用默认样式，同样存在「标签被定宽、输入区 0 宽勾选框被裁掉」的问题。）
    /// </summary>
    private static void MakeToggleCompact(Toggle toggle)
    {
        if (toggle == null)
            return;

        toggle.style.flexShrink = 0;
        toggle.style.flexGrow = 0;
        toggle.style.marginRight = 0;
        toggle.style.paddingLeft = 0;
        toggle.style.paddingRight = 0;
        toggle.style.flexDirection = FlexDirection.Row;
        toggle.style.alignItems = Align.Center;

        CompactToggleInternals(toggle);
    }

    private void InitializePlateOverlayToggle()
    {
        if (_arrowToggle == null || _arrowToggle.parent == null)
            return;

        // Create the toggle programmatically to avoid UXML rendering issues
        _plateOverlayToggle = CreateCompactToggle("断层带", "plate-overlay-toggle", true);

        // Insert after the arrow toggle
        var parent = _arrowToggle.parent;
        int arrowIndex = parent.IndexOf(_arrowToggle);
        parent.Insert(arrowIndex + 1, _plateOverlayToggle);

        _plateOverlayToggle.RegisterValueChangedCallback(evt =>
        {
            if (EarthManager.Instance != null)
                EarthManager.Instance.SetPlateOverlayVisible(evt.newValue);
        });
    }

    private void InitializeTerrainDisplacementToggle()
    {
        if (_plateOverlayToggle == null || _plateOverlayToggle.parent == null)
            return;

        _terrainDisplacementToggle = CreateCompactToggle("地形凹凸", "terrain-displacement-toggle", true);

        var parent = _plateOverlayToggle.parent;
        int plateIndex = parent.IndexOf(_plateOverlayToggle);
        parent.Insert(plateIndex + 1, _terrainDisplacementToggle);

        _terrainDisplacementToggle.RegisterValueChangedCallback(evt =>
        {
            if (EarthManager.Instance != null)
                EarthManager.Instance.SetTerrainDisplacement(evt.newValue);
        });
    }

    /// <summary>
    /// 云层预览开关：在星球外侧套一层半透明云层球壳，
    /// 云的分布由地图的气候湿度场驱动（湿地多云、沙漠少云），并随风带流动。
    /// </summary>
    private void InitializeCloudLayerToggle()
    {
        if (_terrainDisplacementToggle == null || _terrainDisplacementToggle.parent == null)
            return;

        _cloudLayerToggle = CreateCompactToggle("云层", "cloud-layer-toggle", false);

        var parent = _terrainDisplacementToggle.parent;
        int index = parent.IndexOf(_terrainDisplacementToggle);
        parent.Insert(index + 1, _cloudLayerToggle);

        // 初始值取**云层的实际状态**，而不是写死 false 或 true：
        // 否则会出现「开关没勾选、云层却显示」这类 UI 与画面不一致的情况。
        bool initial = EarthManager.Instance != null && EarthManager.Instance.IsCloudLayerVisible;
        _cloudLayerToggle.SetValueWithoutNotify(initial);

        _cloudLayerToggle.RegisterValueChangedCallback(evt =>
        {
            if (EarthManager.Instance != null)
                EarthManager.Instance.SetCloudLayerVisible(evt.newValue);
        });

        // 持续对齐：CloudLayer 在 Start 里才会按 startVisible 建好球壳并设显隐，
        // 而那时本界面已初始化完毕，所以这里每帧把开关拉回真实状态（没有变化时不做任何事）。
        _cloudLayerToggle.schedule.Execute(() =>
        {
            if (EarthManager.Instance == null)
                return;
            bool actual = EarthManager.Instance.IsCloudLayerVisible;
            if (actual != _cloudLayerToggle.value)
                _cloudLayerToggle.SetValueWithoutNotify(actual);
        }).Every(200);
    }

    /// <summary>
    /// 地名导出/导入按钮：导出 &lt;地图名&gt;_names.json（含地理背景与空 name 字段），
    /// 交给 AI 填好中文地名后原样导入，即可刷新到地图上。
    /// </summary>
    private void InitializeNameTransferButtons()
    {
        if (_generateButton == null || _generateButton.parent == null)
            return;

        var parent = _generateButton.parent;

        _exportNamesButton = new Button(OnExportNamesClicked) { text = "导出地名请求" };
        _exportNamesButton.AddToClassList("draw-button");
        parent.Insert(parent.IndexOf(_generateButton) + 1, _exportNamesButton);

        _importNamesButton = new Button(OnImportNamesClicked) { text = "导入地名" };
        _importNamesButton.AddToClassList("draw-button");
        parent.Insert(parent.IndexOf(_exportNamesButton) + 1, _importNamesButton);
    }

    private void OnExportNamesClicked()
    {
        if (EarthManager.Instance == null)
            return;

        string path = EarthManager.Instance.ExportNamingRequest();
        if (_genProgress != null)
            _genProgress.text = path != null
                ? $"已导出 {Path.GetFileName(path)}"
                : "当前地图没有人文数据";
    }

    private void OnImportNamesClicked()
    {
        if (EarthManager.Instance == null)
            return;

        var result = EarthManager.Instance.ImportNames();
        if (_genProgress != null)
            _genProgress.text = result.Describe();
    }

    /// <summary>人文图层开关（势力范围 / 交通线 / 聚落与地名）</summary>
    private void InitializeHumanOverlayToggle()
    {
        if (_terrainDisplacementToggle == null || _terrainDisplacementToggle.parent == null)
            return;

        _humanOverlayToggle = CreateCompactToggle("人文", "human-overlay-toggle", true);

        var parent = _terrainDisplacementToggle.parent;
        int index = parent.IndexOf(_terrainDisplacementToggle);
        parent.Insert(index + 1, _humanOverlayToggle);

        _humanOverlayToggle.RegisterValueChangedCallback(evt =>
        {
            if (EarthManager.Instance != null)
                EarthManager.Instance.SetHumanOverlayVisible(evt.newValue);
        });
    }

    /// <summary>
    /// 顶栏布局自检（只在启动后跑一次）：
    /// 报出顶栏实际宽度与可用空间，方便判断是否还会与左右两栏重叠。
    /// 顶栏两侧各留 margin（右 164 / 左 8），所以能用的宽度是 root 宽度减去这两项。
    /// </summary>
    private void ReportTopBarLayout()
    {
        if (_root == null)
            return;

        VisualElement topBar = _root.Q<VisualElement>("top-bar");
        if (topBar == null)
            return;

        float rootWidth = _root.resolvedStyle.width;
        float barWidth = topBar.resolvedStyle.width;
        float available = rootWidth - 164f - 8f;

        if (barWidth <= 0f || rootWidth <= 0f)
            return;                       // 布局还没算出来，本轮跳过

        // 正常时不打日志；只有真的放不下才报警（顶栏过宽会盖住右侧栏）
        if (barWidth > available)
            Debug.LogWarning($"顶栏过宽：{barWidth:F0}px > 可用 {available:F0}px" +
                             $"（窗口 {rootWidth:F0}px，两侧让位 172px），会与左右两栏重叠。" +
                             "可考虑把「流速」「季节」两组收进侧边栏，或缩短开关文案。");

        ReportToggleLayout();
    }

    /// <summary>
    /// 自检 Toggle 的内部尺寸。两种异常都会让「文字与勾选框之间空白过大」甚至「看不见勾选框」：
    ///   ① 标签被默认样式定宽（实测曾为 150px，而文字只占 23px）
    ///   ② 输入区宽度为 0（它装着 24px 的勾选框，宽 0 会把勾选框裁掉）
    /// 正常时**不输出任何日志**，只有异常才报警 —— 以后换引擎版本时能立刻发现。
    /// </summary>
    private void ReportToggleLayout()
    {
        if (_cloudLayerToggle == null || _cloudLayerToggle.childCount < 2)
            return;

        VisualElement label = _cloudLayerToggle[0];
        VisualElement input = _cloudLayerToggle[1];

        float labelWidth = label.resolvedStyle.width;
        float inputWidth = input.resolvedStyle.width;

        const float labelMaxExpected = 64f;      // 标签按内容自适应时只有二十几像素
        bool labelTooWide = labelWidth > labelMaxExpected;
        bool inputTooNarrow = inputWidth + 0.5f < CheckmarkSize;

        if (!labelTooWide && !inputTooNarrow)
            return;

        var sb = new StringBuilder("顶栏开关尺寸异常：");
        if (labelTooWide)
            sb.Append($"标签被撑宽到 {labelWidth:F0}px（应在 {labelMaxExpected:F0}px 内），" +
                      "文字与勾选框之间会留大片空白；");
        if (inputTooNarrow)
            sb.Append($"输入区仅 {inputWidth:F0}px，装不下 {CheckmarkSize:F0}px 的勾选框（会被裁掉看不见）；");
        sb.Append($"\n  Toggle w={_cloudLayerToggle.resolvedStyle.width:F0}，" +
                  $"Label w={labelWidth:F0}，Input w={inputWidth:F0}");

        Debug.LogWarning(sb.ToString());
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

                // 云带随季节南北移动
                if (EarthManager.Instance != null)
                    EarthManager.Instance.SetCloudSeasonProgress(normalizedYear);
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
                if (fileName.Contains("_height") || fileName.Contains("_plates") || fileName.Contains("_lines"))
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

    /// <summary>
    /// 载入一张地图并应用到星球。
    ///
    /// 不再手工拼各个文件名：先由 <see cref="MapStore"/> 读成 <see cref="MapData"/> 契约，
    /// 再按契约逐项应用 —— 缺高度图就不做凹凸、缺板块图就隐藏叠加层、
    /// 缺人文就清空人文图层。新增图层时只需要改契约与这里。
    /// </summary>
    private IEnumerator LoadTextureCoroutine(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogError($"File not found: {filePath}");
            yield break;
        }

        string baseName = Path.GetFileNameWithoutExtension(filePath);
        MapData map = MapStore.Load(baseName);

        if (map == null)
        {
            Debug.LogError($"载入地图失败：找不到 {baseName} 的地形图");
            yield break;
        }

        if (!map.Validate(out string reason))
        {
            Debug.LogError($"载入地图失败：{reason}");
            yield break;
        }

        // ── 彩色地形图（必需）──
        Texture2D terrainTex = DecodeTexture(map.terrainPng, false);
        if (terrainTex == null)
        {
            Debug.LogError($"地形图解码失败：{filePath}");
            yield break;
        }

        if (_currentTexture != null)
            Destroy(_currentTexture);
        _currentTexture = terrainTex;
        EarthManager.Instance.SetEarthSurface(terrainTex);

        // ── 高度图（可选：没有就不做地形凹凸）──
        Texture2D heightTex = map.HasHeight ? DecodeTexture(map.heightPng, true) : null;
        if (heightTex != null)
            EarthManager.Instance.SetEarthHeightMap(heightTex);
        else
            EarthManager.Instance.ClearEarthHeightMap();

        // ── 板块分界叠加图（可选）──
        Texture2D plateTex = map.HasPlateOverlay ? DecodeTexture(map.plateOverlayPng, false) : null;
        if (plateTex != null)
            EarthManager.Instance.SetPlateOverlay(plateTex);
        else
            EarthManager.Instance.SetPlateOverlayVisible(false);

        // ── 地图属性（地轴倾角、水面占比）：都已在契约里 ──
        ApplyMapProperties(map);

        // ── 人文图层（可选）──
        if (Application.isPlaying && EarthManager.Instance != null)
            EarthManager.Instance.SetHumanGeography(map.human, baseName);

        Debug.Log($"已载入地图 {map}");

        // ── 用户标注（不属于地图内容，单独载入）──
        EarthManager.Instance.LoadLinesFromJSON(baseName);
        EarthManager.Instance.LoadMarkersFromJSON(baseName);

        yield return null;
    }

    /// <summary>把 PNG 字节解成贴图；失败返回 null。</summary>
    private static Texture2D DecodeTexture(byte[] png, bool linear)
    {
        if (png == null || png.Length == 0)
            return null;

        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear);
        if (!tex.LoadImage(png))
        {
            Destroy(tex);
            return null;
        }

        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Repeat;

        // 释放 CPU 侧像素副本：显示只需要 GPU 那份。
        // 8192 档每张省 134 MB，地形/高度/板块三层共省约 400 MB。
        tex.Apply(false, true);
        return tex;
    }

    #endregion

    #region Draw Mode

    private void InitializeDrawModeButtons()
    {
        var bottomBar = _root.Q<VisualElement>("bottom-bar");
        if (bottomBar == null) return;

        _drawModeButton = new Button();
        _drawModeButton.AddToClassList("draw-button");
        _drawModeButton.text = "画线模式";
        _drawModeButton.clicked += () =>
        {
            if (EarthManager.Instance != null)
                EarthManager.Instance.SetDrawMode(!EarthManager.Instance.IsDrawMode);
            SyncDrawModeState();
        };
        bottomBar.Add(_drawModeButton);

        _undoLineButton = new Button();
        _undoLineButton.AddToClassList("draw-button");
        _undoLineButton.text = "撤销线";
        _undoLineButton.clicked += () =>
        {
            if (EarthManager.Instance != null)
                EarthManager.Instance.UndoLastLine();
        };
        bottomBar.Add(_undoLineButton);

        _clearLinesButton = new Button();
        _clearLinesButton.AddToClassList("draw-button");
        _clearLinesButton.text = "清除线";
        _clearLinesButton.clicked += () =>
        {
            if (EarthManager.Instance != null)
                EarthManager.Instance.ClearAllLines();
        };
        bottomBar.Add(_clearLinesButton);

        _saveLinesButton = new Button();
        _saveLinesButton.AddToClassList("draw-button");
        _saveLinesButton.text = "保存线";
        _saveLinesButton.clicked += () =>
        {
            if (EarthManager.Instance != null)
                EarthManager.Instance.SaveLinesAsPNG();
        };
        bottomBar.Add(_saveLinesButton);

        UpdateDrawModeButtonLabel();
    }

    private void UpdateDrawModeButtonLabel()
    {
        if (_drawModeButton == null) return;
        bool isDraw = EarthManager.Instance != null && EarthManager.Instance.IsDrawMode;
        _drawModeButton.text = isDraw ? "画线模式 ●" : "画线模式";
    }

    private void SyncDrawModeState()
    {
        bool isDraw = EarthManager.Instance != null && EarthManager.Instance.IsDrawMode;
        _drawModeButton.text = isDraw ? "画线模式 ●" : "画线模式";
        if (_terrainDisplacementToggle != null)
            _terrainDisplacementToggle.SetValueWithoutNotify(!isDraw);
    }

    private void HandleDrawModeShortcuts()
    {
        if (Input.GetKeyDown(KeyCode.L))
        {
            if (EarthManager.Instance != null)
                EarthManager.Instance.SetDrawMode(!EarthManager.Instance.IsDrawMode);
            SyncDrawModeState();
        }

        if (Input.GetKeyDown(KeyCode.Z))
        {
            if (EarthManager.Instance != null)
                EarthManager.Instance.UndoLastLine();
        }
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

        // 地轴倾角：地图属性，生成时由该滑动条决定（0°-90°）
        if (_tiltSlider != null)
        {
            _tiltSlider.lowValue = MapMetaData.MinAxialTilt;
            _tiltSlider.highValue = MapMetaData.MaxAxialTilt;

            float initialTilt = sunLightController != null
                ? sunLightController.AxialTilt
                : MapMetaData.DefaultAxialTilt;
            _tiltSlider.value = MapMetaData.ClampAxialTilt(initialTilt);

            _tiltSlider.RegisterValueChangedCallback(evt =>
            {
                // 该滑动条只是「下次生成」的参数，不实时改变星球：
                // 星球显示的倾角必须始终等于当前这张地图生成时使用的倾角
                UpdateTiltLabel(MapMetaData.ClampAxialTilt(evt.newValue));
            });
            UpdateTiltLabel(_tiltSlider.value);
        }

        // 星球大小：地图属性，生成时由该滑动条决定（0.4~2.5 倍地球半径）
        if (_planetSizeSlider != null)
        {
            _planetSizeSlider.lowValue = PlanetProfile.MinRadiusKm;
            _planetSizeSlider.highValue = PlanetProfile.MaxRadiusKm;
            _planetSizeSlider.value = PlanetProfile.DefaultRadiusKm;

            _planetSizeSlider.RegisterValueChangedCallback(evt =>
            {
                // 与地轴倾角一样：只是「下次生成」的参数，不实时改变当前星球
                UpdatePlanetSizeLabel(PlanetProfile.ClampRadiusKm(evt.newValue));
            });
            UpdatePlanetSizeLabel(_planetSizeSlider.value);
        }

        if (_generateButton != null)
        {
            _generateButton.clicked += OnGenerateButtonClicked;
        }

        // 生成分辨率：滑条走 4 个离散档位（1024 / 2048 / 4096 / 8192 宽度）
        if (_resolutionSlider != null)
        {
            // 本机内存允许到哪一档：滑条上限直接卡在这里，用户拖不到更高档位
            int maxIndex = MapResolution.MaxAllowedIndex(SystemInfo.systemMemorySize);

            _resolutionSlider.lowValue = 0;
            _resolutionSlider.highValue = Mathf.Max(0, Mathf.Min(maxIndex < 0 ? 0 : maxIndex,
                                                                 MapResolution.Widths.Length - 1));
            _resolutionSlider.value = Mathf.Clamp(
                MapResolution.DefaultIndex, 0, Mathf.RoundToInt(_resolutionSlider.highValue));

            // 连最低档都放不下：禁用生成，并说明原因
            if (maxIndex < 0)
            {
                _resolutionSlider.SetEnabled(false);
                if (_generateButton != null)
                    _generateButton.SetEnabled(false);

                if (_resolutionHint != null)
                {
                    _resolutionHint.text =
                        $"可用内存不足（本机 {SystemInfo.systemMemorySize} MB），无法生成";
                    _resolutionHint.AddToClassList("resolution-hint-warn");
                }

                Debug.LogWarning($"内存不足：物理内存 {SystemInfo.systemMemorySize} MB，" +
                                 $"连 {MapResolution.Label(MapResolution.MinWidth)} 都放不下" +
                                 $"（需要 {MapResolution.RequiredLabel(MapResolution.MinWidth)}），已禁用生成");
            }

            _resolutionSlider.RegisterValueChangedCallback(evt =>
            {
                int upper = Mathf.RoundToInt(_resolutionSlider.highValue);
                int index = Mathf.Clamp(Mathf.RoundToInt(evt.newValue), 0, upper);

                // 吸附到整数档位：滑条本身没有步进，这里把值扣回离散点
                if (!Mathf.Approximately(_resolutionSlider.value, index))
                    _resolutionSlider.SetValueWithoutNotify(index);

                UpdateResolutionLabel(index);
            });

            if (maxIndex >= 0)
                UpdateResolutionLabel((int)_resolutionSlider.value);
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

    /// <summary>
    /// 刷新分辨率显示：标签给出「宽×高」，下面一行给出内存需求；
    /// 同时说明本机允许的最高档位（被内存限制时会点明）。
    /// </summary>
    private void UpdateResolutionLabel(int index)
    {
        int width = MapResolution.WidthAt(index);

        if (_resolutionValue != null)
            _resolutionValue.text = MapResolution.Label(width);

        if (_resolutionHint == null)
            return;

        int maxWidth = MapResolution.MaxAllowedWidth(SystemInfo.systemMemorySize);

        if (maxWidth <= 0)
        {
            // 连最低档都放不下：这里只说结论，不要用一个假的档位名误导
            _resolutionHint.text = $"可用内存不足（本机 {SystemInfo.systemMemorySize} MB）";
            _resolutionHint.AddToClassList("resolution-hint-warn");
            return;
        }

        _resolutionHint.text =
            $"内存 {MapResolution.EstimateLabel(width)}｜本机最高 {MapResolution.Label(maxWidth)}";

        // 被内存卡住（本机允许的最高档低于最高档位）时标黄，提醒用户不是程序出错
        bool limited = maxWidth < MapResolution.MaxWidth;
        if (limited)
            _resolutionHint.AddToClassList("resolution-hint-warn");
        else
            _resolutionHint.RemoveFromClassList("resolution-hint-warn");
    }

    /// <summary>当前选中生成宽度（像素）。</summary>
    private int SelectedGenerationWidth => _resolutionSlider != null
        ? MapResolution.WidthAt(Mathf.RoundToInt(_resolutionSlider.value))
        : MapResolution.DefaultWidth;

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
        float axialTilt = _tiltSlider != null ? _tiltSlider.value : MapMetaData.DefaultAxialTilt;
        axialTilt = MapMetaData.ClampAxialTilt(axialTilt);
        _pendingAxialTilt = axialTilt;

        float planetRadiusKm = _planetSizeSlider != null
            ? PlanetProfile.ClampRadiusKm(_planetSizeSlider.value)
            : PlanetProfile.DefaultRadiusKm;
        bool retrograde = _retrogradeToggle != null && _retrogradeToggle.value;
        _pendingPlanetRadiusKm = planetRadiusKm;
        _pendingRetrograde = retrograde;

        if (_genProgress != null)
            _genProgress.text = "生成中...";

        if (WorldTerrainGenerator.Instance != null)
        {
            int width = SelectedGenerationWidth;

            // 双保险：滑条已经卡住上限，这里再挡一次（防止程序化改值绕过）
            if (!MapResolution.IsAllowed(width, SystemInfo.systemMemorySize))
            {
                string reason =
                    $"{MapResolution.Label(width)} 需要 {MapResolution.RequiredLabel(width)}，" +
                    $"超出本机内存预算（物理内存 {SystemInfo.systemMemorySize} MB，" +
                    $"最高允许 {MapResolution.Label(MapResolution.MaxAllowedWidth(SystemInfo.systemMemorySize))}）";

                Debug.LogError($"已阻止生成：{reason}");
                if (_genProgress != null)
                    _genProgress.text = "内存不足，已阻止生成";
                return;
            }

            Debug.Log($"开始生成：{MapResolution.Label(width)}，seed {seed}，" +
                      $"水面 {waterCoverage * 100:F0}%，倾角 {axialTilt:F1}°，" +
                      $"星球 {PlanetProfile.RadiusLabel(planetRadiusKm)}，" +
                      $"{PlanetProfile.RotationLabel(retrograde)}，" +
                      $"预计内存 {MapResolution.EstimateLabel(width)}");

            WorldTerrainGenerator.Instance.onProgress = OnGenerationProgress;
            WorldTerrainGenerator.Instance.onComplete = OnGenerationComplete;
            WorldTerrainGenerator.Instance.onError = OnGenerationError;
            WorldTerrainGenerator.Instance.GenerateWorld(
                seed, width, waterCoverage, axialTilt, planetRadiusKm, retrograde);
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
        {
            int settlements = EarthManager.Instance != null ? EarthManager.Instance.HumanSettlementCount : 0;
            int nations = EarthManager.Instance != null ? EarthManager.Instance.HumanNationCount : 0;
            _genProgress.text = settlements > 0
                ? $"生成完成（{settlements} 聚落 / {nations} 国）"
                : "生成完成";
        }
        Debug.Log($"World generation complete: {path}");

        // 生成使用的地轴倾角 / 星球大小 / 自转方向都已写入地图元数据，
        // 这里确保视觉状态与之一致
        SetTiltSliderValue(_pendingAxialTilt);
        ApplyAxialTiltToEarth(_pendingAxialTilt);
        ApplyPlanetAppearance(_pendingPlanetRadiusKm, _pendingRetrograde);

        LoadImagesFromStreamingAssets();
    }

    private void OnGenerationError(string error)
    {
        if (_genProgress != null)
            _genProgress.text = "生成失败";
        Debug.LogError($"World generation error: {error}");
    }

    #endregion

    #region Axial Tilt (地图属性)

    private void UpdateTiltLabel(float tilt)
    {
        if (_tiltValue != null)
            _tiltValue.text = $"{tilt:F1}°";
    }

    private void UpdatePlanetSizeLabel(float radiusKm)
    {
        if (_planetSizeValue != null)
            _planetSizeValue.text = $"{PlanetProfile.ClampRadiusKm(radiusKm):F0} km";
    }

    /// <summary>
    /// 应用星球大小与自转方向：缩放星球本体、等比调整镜头距离、翻转太阳日行方向。
    ///
    /// 三者必须一起改 —— 只放大星球会让相机陷进地面，只改方向会让昼夜与星球尺寸不匹配。
    /// 编辑模式下只同步 UI：改场景里星球的缩放会被存进场景文件，污染工程。
    /// </summary>
    private void ApplyPlanetAppearance(float radiusKm, bool retrograde)
    {
        float km = PlanetProfile.ClampRadiusKm(radiusKm);
        float scale = PlanetProfile.ScaleForRadiusKm(km);

        if (Application.isPlaying)
        {
            if (EarthManager.Instance != null)
                EarthManager.Instance.SetPlanetRadiusKm(km);

            if (cameraController != null)
                cameraController.SetPlanetScale(scale);

            if (sunLightController != null)
                sunLightController.SetRotationDirection(retrograde);

            Debug.Log($"星球外观：{PlanetProfile.RadiusLabel(km)}，" +
                      $"{PlanetProfile.RotationLabel(retrograde)}，镜头 ×{scale:F2}");
        }

        // 滑动条同步到这颗星球实际使用的值（它是「下次生成」的参数，
        // 但载入地图后要和当前星球一致，否则用户会以为改过）
        if (_planetSizeSlider != null)
            _planetSizeSlider.SetValueWithoutNotify(km);
        UpdatePlanetSizeLabel(km);

        if (_retrogradeToggle != null)
            _retrogradeToggle.SetValueWithoutNotify(retrograde);
    }

    /// <summary>
    /// 同步滑动条显示值（不触发回调）。
    /// </summary>
    private void SetTiltSliderValue(float tilt)
    {
        tilt = MapMetaData.ClampAxialTilt(tilt);

        if (_tiltSlider != null)
        {
            _tiltSlider.lowValue = MapMetaData.MinAxialTilt;
            _tiltSlider.highValue = MapMetaData.MaxAxialTilt;
            _tiltSlider.SetValueWithoutNotify(tilt);
        }

        UpdateTiltLabel(tilt);
    }

    /// <summary>
    /// 把地轴倾角应用到地球。编辑模式下只更新 UI，避免污染场景中的地球朝向。
    /// </summary>
    private void ApplyAxialTiltToEarth(float tilt)
    {
        tilt = MapMetaData.ClampAxialTilt(tilt);

        if (!Application.isPlaying || sunLightController == null)
            return;

        sunLightController.SetAxialTilt(tilt);
    }

    /// <summary>
    /// 应用地图自身的属性（地轴倾角、水面占比、星球大小、自转方向），并同步到 UI 与地球。
    /// 参数是 <see cref="MapData"/> 契约：这些值已经在载入时从 meta 读好，
    /// 这里不再自己读一遍文件。
    /// </summary>
    private void ApplyMapProperties(MapData map)
    {
        if (map == null)
            return;

        float tilt = MapMetaData.ClampAxialTilt(map.axialTilt);
        SetTiltSliderValue(tilt);
        ApplyAxialTiltToEarth(tilt);

        if (_waterSlider != null)
        {
            _waterSlider.SetValueWithoutNotify(
                Mathf.Clamp(map.waterCoverage, _waterSlider.lowValue, _waterSlider.highValue));
            if (_waterValue != null)
                _waterValue.text = $"{_waterSlider.value * 100:F0}%";
        }

        // 星球大小与自转方向：缩放星球 + 等比调整镜头 + 翻转太阳日行方向
        float radiusKm = PlanetProfile.ClampRadiusKm(map.planetRadiusKm);
        _pendingPlanetRadiusKm = radiusKm;
        _pendingRetrograde = map.rotationRetrograde;
        ApplyPlanetAppearance(radiusKm, map.rotationRetrograde);

        Debug.Log($"已应用地图属性 '{map.baseName}'：axialTilt={tilt:F1}°，" +
                  $"waterCoverage={map.waterCoverage * 100:F1}%，" +
                  $"planet={PlanetProfile.RadiusLabel(radiusKm)}，" +
                  $"{PlanetProfile.RotationLabel(map.rotationRetrograde)}");
    }

    #endregion
}
