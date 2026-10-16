using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using WorldTerrain;

public class EarthManager : MonoBehaviour
{
    #region Properties

    public static EarthManager Instance { get; private set; }

    [SerializeField] private Renderer earthRenderer;
    [SerializeField] private GameObject markerPrefab;
    [SerializeField] private Transform markersParent;
    [SerializeField, Range(0f, 0.3f)] private float heightScale = 0.15f;
    [SerializeField] private float labelMaxDisplayDistance = 20f;
    [SerializeField] private float labelBaseCharSize = 0.08f;
    [SerializeField] private float labelReferenceDistance = 5f;

    private List<MarkerData> markers = new List<MarkerData>();
    private int currentColorIndex = 0;
    private Renderer _plateOverlayRenderer;
    private GameObject _plateOverlayObject;

    /// <summary>云层预览（气候数据驱动）；可挂在本物体或子物体上。</summary>
    private CloudLayer _cloudLayer;

    // Draw mode
    private bool _drawMode = false;
    private List<GameObject> _lineObjects = new List<GameObject>();
    private GameObject _currentLineObj;
    private LineRenderer _currentLine;
    private List<Vector3> _currentPoints;
    private Transform _linesParent;
    [SerializeField] private float lineWidth = 0.012f;
    private string _currentMapName;
    private bool _isLoadingMarkers = false;

    public Color[] markerColors = new Color[]
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

        if (markerPrefab == null)
        {
            CreateDefaultMarkerPrefab();
        }

        _cloudLayer = GetComponentInChildren<CloudLayer>();
    }

    void Update()
    {
        HandleMarkerInput();
        HandleColorSelection();
        UpdateMarkerLabels();
        UpdateHumanLabels();
    }

    #endregion

    #region 云层预览（气候数据驱动）

    /// <summary>当前是否已建立云层（有 CloudLayer 组件即可，未必要有湿度数据）。</summary>
    public bool HasCloudLayer => _cloudLayer != null;

    /// <summary>云层是否可见。</summary>
    public bool IsCloudLayerVisible => _cloudLayer != null && _cloudLayer.IsVisible;

    /// <summary>云层是否拿到了气候湿度场（false = 均匀云量）。</summary>
    public bool CloudLayerHasMoisture => _cloudLayer != null && _cloudLayer.HasMoisture;

    /// <summary>
    /// 显示/隐藏云层。第一次显示时才真正建立球壳，避免没用到时多一个球体。
    /// </summary>
    public void SetCloudLayerVisible(bool visible)
    {
        if (_cloudLayer == null)
        {
            _cloudLayer = GetComponentInChildren<CloudLayer>();
            if (_cloudLayer == null)
            {
                Debug.LogWarning("EarthManager: 场景里没有 CloudLayer 组件，云层开关无效");
                return;
            }
        }

        _cloudLayer.SetEarthRenderer(earthRenderer);
        _cloudLayer.SetVisible(visible);

        // 刚显示时如果还没拿到湿度场，用当前地图的数据补上
        if (visible && !_cloudLayer.HasMoisture)
            UpdateCloudMoisture(_humanData);
    }

    /// <summary>设置云层基准云量（0-1）。</summary>
    public void SetCloudCoverage(float coverage)
    {
        if (_cloudLayer != null)
            _cloudLayer.SetCoverage(coverage);
    }

    /// <summary>季节进度（0-1，0 = 春分）：让云带随季节南北移动。</summary>
    public void SetCloudSeasonProgress(float normalizedYear)
    {
        if (_cloudLayer != null)
            _cloudLayer.SetSeasonProgress(normalizedYear);
    }

    /// <summary>
    /// 把湿度场推给云层（没有数据时清除，云层退化为均匀云量）。
    /// 湿度场存在 <c>&lt;地图名&gt;_human.json</c> 里，因此随人文数据一起更新。
    /// </summary>
    private void UpdateCloudMoisture(HumanGeographyData data)
    {
        if (_cloudLayer == null)
            return;

        byte[] moisture = data != null ? data.GetMoistureField() : null;
        if (moisture != null && data.moistureWidth > 0 && data.moistureHeight > 0)
        {
            _cloudLayer.SetMoistureField(moisture, data.moistureWidth, data.moistureHeight);
        }
        else
        {
            _cloudLayer.ClearMoistureField();
        }
    }

    #endregion

    #region Public Methods

    public void SetEarthSurface(Texture2D texture)
    {
        if (earthRenderer != null)
        {
            Material material = earthRenderer.material;
            material.SetTexture("_MainTex", texture);

            Debug.Log($"Successfully loaded texture: {texture}");
            Debug.Log($"Texture size: {texture.width}x{texture.height}");
        }
        else
        {
            Debug.LogError("Earth Renderer is not assigned!");
        }
    }

    public void SetEarthHeightMap(Texture2D heightMap)
    {
        if (earthRenderer != null)
        {
            Material material = earthRenderer.material;
            material.SetTexture("_HeightTex", heightMap);
            material.SetFloat("_HeightScale", heightScale);

            Debug.Log($"Successfully loaded height map: {heightMap}");
        }
        else
        {
            Debug.LogError("Earth Renderer is not assigned!");
        }
    }

    public void ClearEarthHeightMap()
    {
        if (earthRenderer != null)
        {
            Material material = earthRenderer.material;
            material.SetTexture("_HeightTex", null);
            material.SetFloat("_HeightScale", 0f);
        }
    }

    public void SetPlateOverlay(Texture2D plateTexture)
    {
        if (_plateOverlayRenderer == null)
            CreatePlateOverlaySphere();

        if (_plateOverlayRenderer != null)
        {
            _plateOverlayRenderer.material.SetTexture("_MainTex", plateTexture);
            _plateOverlayObject.SetActive(true);
            Debug.Log("Applied plate overlay texture to overlay sphere");
        }
    }

    public void SetPlateOverlayVisible(bool visible)
    {
        if (_plateOverlayObject != null)
            _plateOverlayObject.SetActive(visible);
    }

    public void SetTerrainDisplacement(bool enabled)
    {
        if (earthRenderer != null)
        {
            Material material = earthRenderer.material;
            if (enabled)
                material.EnableKeyword("_TERRAIN_DISPLACEMENT_ON");
            else
                material.DisableKeyword("_TERRAIN_DISPLACEMENT_ON");
        }
    }

    public Vector3? RaycastToEarthPosition(Vector3 screenPosition)
    {
        if (earthRenderer == null)
            return null;

        Ray ray = Camera.main.ScreenPointToRay(screenPosition);

        if (Physics.Raycast(ray, out RaycastHit hit))
        {
            if (hit.transform == earthRenderer.transform ||
                hit.transform.IsChildOf(earthRenderer.transform))
            {
                return hit.point;
            }
        }

        return null;
    }

    public GameObject AddMarkerAtPosition(Vector3 worldPosition, int colorIndex)
    {
        if (markersParent == null)
        {
            markersParent = new GameObject("Markers").transform;
            markersParent.SetParent(transform);
        }

        colorIndex = Mathf.Clamp(colorIndex, 0, markerColors.Length - 1);

        GameObject marker = Instantiate(markerPrefab, markersParent);
        marker.transform.position = worldPosition;

        // Align marker up to surface normal so the label sits above the surface
        if (earthRenderer != null)
        {
            Vector3 normal = (worldPosition - earthRenderer.transform.position).normalized;
            marker.transform.up = normal;
        }

        MarkerData markerData = new MarkerData
        {
            gameObject = marker,
            position = worldPosition,
            colorIndex = colorIndex,
            timestamp = Time.time,
            name = ""
        };

        CreateMarkerLabel(marker, markerData, markers.Count);
        markers.Add(markerData);

        Renderer markerRenderer = marker.GetComponent<Renderer>();
        if (markerRenderer != null)
        {
            markerRenderer.material.color = markerColors[colorIndex];
        }

        Debug.Log($"Added marker at {worldPosition}, color index: {colorIndex}");

        SaveMarkersAsJSON();

        return marker;
    }

    public void RemoveMarker(GameObject marker)
    {
        for (int i = markers.Count - 1; i >= 0; i--)
        {
            if (markers[i].gameObject == marker)
            {
                if (markers[i].labelMesh != null)
                    Destroy(markers[i].labelMesh.gameObject);
                markers.RemoveAt(i);
                Destroy(marker);
                UpdateAllMarkerLabels();
                SaveMarkersAsJSON();
                break;
            }
        }
    }

    public void ClearAllMarkers()
    {
        foreach (var marker in markers)
        {
            if (marker.labelMesh != null)
                Destroy(marker.labelMesh.gameObject);
            Destroy(marker.gameObject);
        }

        markers.Clear();

        SaveMarkersAsJSON();

        Debug.Log("Cleared all markers");
    }

    public void SetCurrentColorIndex(int index)
    {
        currentColorIndex = Mathf.Clamp(index, 0, markerColors.Length - 1);
        Debug.Log($"Selected marker color: {currentColorIndex}");
    }

    public int GetCurrentColorIndex()
    {
        return currentColorIndex;
    }

    public Color GetCurrentColor()
    {
        return markerColors[currentColorIndex];
    }

    public List<MarkerData> GetAllMarkers()
    {
        return new List<MarkerData>(markers);
    }

    public int GetMarkerCount()
    {
        return markers.Count;
    }

    public void SetMarkerName(GameObject marker, string name)
    {
        for (int i = 0; i < markers.Count; i++)
        {
            if (markers[i].gameObject == marker)
            {
                markers[i].name = name ?? "";
                UpdateMarkerLabelText(markers[i], i);
                SaveMarkersAsJSON();
                break;
            }
        }
    }

    public void UpdateAllMarkerLabels()
    {
        for (int i = 0; i < markers.Count; i++)
        {
            UpdateMarkerLabelText(markers[i], i);
        }
    }

    // ── Draw mode ──

    public bool IsDrawMode => _drawMode;

    public void SetCurrentMapName(string mapName)
    {
        _currentMapName = mapName;
    }

    public void SetDrawMode(bool enabled)
    {
        _drawMode = enabled;
        // Auto-disable terrain displacement while drawing, restore on exit
        SetTerrainDisplacement(!enabled);
    }

    public void UndoLastLine()
    {
        if (_lineObjects.Count > 0)
        {
            int last = _lineObjects.Count - 1;
            Destroy(_lineObjects[last]);
            _lineObjects.RemoveAt(last);
            SaveLinesAsJSON();
        }
    }

    public void ClearAllLines()
    {
        foreach (var obj in _lineObjects)
            Destroy(obj);
        _lineObjects.Clear();
        SaveLinesAsJSON();
    }

    public void SaveLinesAsPNG()
    {
        if (earthRenderer == null) return;
        Texture2D terrainTex = earthRenderer.material.GetTexture("_MainTex") as Texture2D;
        if (terrainTex == null)
        {
            Debug.LogError("No terrain texture found for line save");
            return;
        }

        int width = terrainTex.width;
        int height = terrainTex.height;

        // Render texture: copy terrain as background
        RenderTexture rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(terrainTex, rt);

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D result = new Texture2D(width, height, TextureFormat.RGBA32, false);
        result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        result.Apply();
        RenderTexture.active = prev;
        rt.Release();

        // Draw lines onto the texture
        Vector3 earthCenter = earthRenderer.transform.position;

        foreach (var lineObj in _lineObjects)
        {
            var lr = lineObj.GetComponent<LineRenderer>();
            if (lr == null) continue;
            Color lineColor = lr.material.color;

            for (int i = 0; i < lr.positionCount - 1; i++)
            {
                Vector3 p1 = lr.GetPosition(i);
                Vector3 p2 = lr.GetPosition(i + 1);
                DrawLineOnTexture(result, p1, p2, lineColor, earthCenter, width, height);
            }
        }

        // Save PNG
        string baseName = string.IsNullOrEmpty(_currentMapName) ? "lines" : _currentMapName;
        string path = System.IO.Path.Combine(
            Application.streamingAssetsPath, $"{baseName}_lines.png");
        System.IO.File.WriteAllBytes(path, result.EncodeToPNG());
        Debug.Log($"Saved lines PNG: {path}");

        Destroy(result);
    }

    private void SaveLinesAsJSON()
    {
        if (string.IsNullOrEmpty(_currentMapName)) return;

        string path = System.IO.Path.Combine(
            Application.streamingAssetsPath, $"{_currentMapName}_lines.json");

        if (earthRenderer == null || _lineObjects.Count == 0)
        {
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
            return;
        }

        Vector3 earthCenter = earthRenderer.transform.position;
        var data = new LineDataList();

        foreach (var lineObj in _lineObjects)
        {
            var lr = lineObj.GetComponent<LineRenderer>();
            if (lr == null || lr.positionCount == 0) continue;

            var lineEntry = new LineEntry
            {
                colorIndex = GetColorIndex(lr.material.color),
                points = new System.Collections.Generic.List<Vector2Serialized>()
            };

            for (int i = 0; i < lr.positionCount; i++)
            {
                Vector2 uv = WorldToUV(lr.GetPosition(i), earthCenter);
                lineEntry.points.Add(new Vector2Serialized { x = uv.x, y = uv.y });
            }
            data.lines.Add(lineEntry);
        }

        string json = JsonUtility.ToJson(data, true);
        System.IO.File.WriteAllText(path, json);
        Debug.Log($"Saved lines JSON: {path} ({data.lines.Count} lines)");
    }

    public void LoadLinesFromJSON(string mapName)
    {
        _currentMapName = mapName;
        ClearAllLinesWithoutSave();

        string path = System.IO.Path.Combine(
            Application.streamingAssetsPath, $"{mapName}_lines.json");
        if (!System.IO.File.Exists(path)) return;

        if (earthRenderer == null) return;
        Vector3 earthCenter = earthRenderer.transform.position;
        float earthRadius = earthRenderer.bounds.extents.x;

        string json = System.IO.File.ReadAllText(path);
        var data = JsonUtility.FromJson<LineDataList>(json);
        if (data == null || data.lines == null) return;

        if (_linesParent == null)
        {
            _linesParent = new GameObject("Lines").transform;
            _linesParent.SetParent(transform);
        }

        foreach (var lineEntry in data.lines)
        {
            if (lineEntry.points == null || lineEntry.points.Count < 2) continue;

            var lineObj = new GameObject("Line");
            lineObj.transform.SetParent(_linesParent, false);
            var lr = lineObj.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.startWidth = lineWidth;
            lr.endWidth = lineWidth;
            lr.material = new Material(Shader.Find("Unlit/Color"));
            int ci = Mathf.Clamp(lineEntry.colorIndex, 0, markerColors.Length - 1);
            lr.material.color = markerColors[ci];
            lr.numCornerVertices = 4;
            lr.numCapVertices = 4;

            lr.positionCount = lineEntry.points.Count;
            for (int i = 0; i < lineEntry.points.Count; i++)
            {
                float u = lineEntry.points[i].x;
                float v = lineEntry.points[i].y;
                Vector3 worldPos = UVToWorld(u, v, earthCenter, earthRadius);
                lr.SetPosition(i, worldPos);
            }
            _lineObjects.Add(lineObj);
        }

        Debug.Log($"Loaded {data.lines.Count} lines from {path}");
    }

    private void ClearAllLinesWithoutSave()
    {
        foreach (var obj in _lineObjects)
            Destroy(obj);
        _lineObjects.Clear();
    }

    private void SaveMarkersAsJSON()
    {
        if (_isLoadingMarkers) return;
        if (string.IsNullOrEmpty(_currentMapName)) return;

        string path = System.IO.Path.Combine(
            Application.streamingAssetsPath, $"{_currentMapName}_markers.json");

        if (earthRenderer == null || markers.Count == 0)
        {
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
            return;
        }

        Vector3 earthCenter = earthRenderer.transform.position;
        var data = new MarkerDataList();

        foreach (var marker in markers)
        {
            Vector2 uv = WorldToUV(marker.position, earthCenter);
            data.markers.Add(new MarkerEntry
            {
                colorIndex = marker.colorIndex,
                name = marker.name,
                u = uv.x,
                v = uv.y
            });
        }

        string json = JsonUtility.ToJson(data, true);
        System.IO.File.WriteAllText(path, json);
        Debug.Log($"Saved markers JSON: {path} ({data.markers.Count} markers)");
    }

    public void LoadMarkersFromJSON(string mapName)
    {
        _currentMapName = mapName;
        ClearAllMarkersWithoutSave();

        string path = System.IO.Path.Combine(
            Application.streamingAssetsPath, $"{mapName}_markers.json");
        if (!System.IO.File.Exists(path)) return;

        if (earthRenderer == null) return;
        Vector3 earthCenter = earthRenderer.transform.position;
        float earthRadius = earthRenderer.bounds.extents.x;

        _isLoadingMarkers = true;

        string json = System.IO.File.ReadAllText(path);
        var data = JsonUtility.FromJson<MarkerDataList>(json);
        if (data != null && data.markers != null)
        {
            foreach (var entry in data.markers)
            {
                Vector3 worldPos = UVToWorld(entry.u, entry.v, earthCenter, earthRadius);
                GameObject markerObj = AddMarkerAtPosition(worldPos, entry.colorIndex);
                if (!string.IsNullOrEmpty(entry.name))
                    SetMarkerName(markerObj, entry.name);
            }
        }

        _isLoadingMarkers = false;
        UpdateAllMarkerLabels();

        int count = data != null && data.markers != null ? data.markers.Count : 0;
        Debug.Log($"Loaded {count} markers from {path}");
    }

    private void ClearAllMarkersWithoutSave()
    {
        foreach (var marker in markers)
        {
            if (marker.labelMesh != null)
                Destroy(marker.labelMesh.gameObject);
            Destroy(marker.gameObject);
        }
        markers.Clear();
    }

    private int GetColorIndex(Color color)
    {
        for (int i = 0; i < markerColors.Length; i++)
        {
            if (Vector4.Distance((Vector4)color, (Vector4)markerColors[i]) < 0.01f)
                return i;
        }
        return 0;
    }

    private Vector3 UVToWorld(float u, float v, Vector3 earthCenter, float earthRadius)
    {
        float lon = (u - 0.5f) * 2f * Mathf.PI;
        float lat = (0.5f - v) * Mathf.PI;
        float x = Mathf.Cos(lat) * Mathf.Sin(lon);
        float y = Mathf.Sin(lat);
        float z = Mathf.Cos(lat) * Mathf.Cos(lon);
        // TransformDirection applies rotation only (no scale), then use world-space radius
        Vector3 worldDir = earthRenderer.transform.TransformDirection(new Vector3(x, y, z)).normalized;
        return earthCenter + worldDir * earthRadius;
    }

    private void DrawLineOnTexture(Texture2D tex, Vector3 p1, Vector3 p2,
        Color color, Vector3 earthCenter, int width, int height)
    {
        Vector2 uv1 = WorldToUV(p1, earthCenter);
        Vector2 uv2 = WorldToUV(p2, earthCenter);

        // Detect U-axis seam crossing (line wraps around left/right boundary)
        float du = uv2.x - uv1.x;
        if (Mathf.Abs(du) > 0.5f)
        {
            // Line crosses the u=0/1 seam — split into two segments at the boundary
            if (du > 0.5f)
            {
                // uv1 near right edge (u→1), uv2 near left edge (u→0)
                float t = (1f - uv1.x) / (uv2.x + 1f - uv1.x);
                float vCross = uv1.y + t * (uv2.y - uv1.y);
                DrawBresenhamLine(tex, uv1.x, uv1.y, 1f, vCross, color, width, height);
                DrawBresenhamLine(tex, 0f, vCross, uv2.x, uv2.y, color, width, height);
            }
            else
            {
                // uv1 near left edge (u→0), uv2 near right edge (u→1)
                float t = (0f - uv1.x) / (uv2.x - 1f - uv1.x);
                float vCross = uv1.y + t * (uv2.y - uv1.y);
                DrawBresenhamLine(tex, uv1.x, uv1.y, 0f, vCross, color, width, height);
                DrawBresenhamLine(tex, 1f, vCross, uv2.x, uv2.y, color, width, height);
            }
        }
        else
        {
            DrawBresenhamLine(tex, uv1.x, uv1.y, uv2.x, uv2.y, color, width, height);
        }
    }

    private void DrawBresenhamLine(Texture2D tex, float u1, float v1, float u2, float v2,
        Color color, int width, int height)
    {
        int x1 = Mathf.Clamp(Mathf.RoundToInt(u1 * width), 0, width - 1);
        int y1 = Mathf.Clamp(Mathf.RoundToInt(v1 * height), 0, height - 1);
        int x2 = Mathf.Clamp(Mathf.RoundToInt(u2 * width), 0, width - 1);
        int y2 = Mathf.Clamp(Mathf.RoundToInt(v2 * height), 0, height - 1);

        // Bresenham line algorithm
        int dx = Mathf.Abs(x2 - x1);
        int dy = Mathf.Abs(y2 - y1);
        int sx = x1 < x2 ? 1 : -1;
        int sy = y1 < y2 ? 1 : -1;
        int err = dx - dy;

        int penRadius = 2;
        while (true)
        {
            DrawCircleOnTexture(tex, x1, y1, penRadius, color, width, height);

            if (x1 == x2 && y1 == y2) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x1 += sx; }
            if (e2 < dx) { err += dx; y1 += sy; }
        }
    }

    private void DrawCircleOnTexture(Texture2D tex, int cx, int cy, int radius,
        Color color, int width, int height)
    {
        for (int y = -radius; y <= radius; y++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                if (x * x + y * y <= radius * radius)
                {
                    int px = cx + x;
                    int py = cy + y;
                    if (px >= 0 && px < width && py >= 0 && py < height)
                        tex.SetPixel(px, py, color);
                }
            }
        }
    }

    private Vector2 WorldToUV(Vector3 worldPos, Vector3 earthCenter)
    {
        // Convert to earth's local direction so rotation/tilt doesn't affect UV
        Vector3 worldDir = (worldPos - earthCenter).normalized;
        Vector3 dir = earthRenderer.transform.InverseTransformDirection(worldDir).normalized;
        float u = 0.5f + Mathf.Atan2(dir.x, dir.z) / (2f * Mathf.PI);
        float v = 0.5f - Mathf.Asin(dir.y) / Mathf.PI;
        return new Vector2(u, v);
    }

    #endregion

    #region Private Methods

    private void HandleMarkerInput()
    {
        if (_drawMode)
        {
            HandleDrawInput();
            return;
        }

        if (Input.GetMouseButtonDown(0))
        {
            Vector3? hitPosition = RaycastToEarthPosition(Input.mousePosition);

            if (hitPosition.HasValue)
            {
                AddMarkerAtPosition(hitPosition.Value, currentColorIndex);
            }
        }
    }

    private void HandleDrawInput()
    {
        if (Input.GetMouseButtonDown(0))
        {
            Vector3? hit = RaycastToEarthPosition(Input.mousePosition);
            if (hit.HasValue)
            {
                if (_linesParent == null)
                {
                    _linesParent = new GameObject("Lines").transform;
                    _linesParent.SetParent(transform);
                }

                _currentLineObj = new GameObject("Line");
                _currentLineObj.transform.SetParent(_linesParent, false);
                _currentLine = _currentLineObj.AddComponent<LineRenderer>();
                _currentLine.useWorldSpace = true;
                _currentLine.startWidth = lineWidth;
                _currentLine.endWidth = lineWidth;
                _currentLine.material = new Material(Shader.Find("Unlit/Color"));
                _currentLine.material.color = markerColors[currentColorIndex];
                _currentLine.numCornerVertices = 4;
                _currentLine.numCapVertices = 4;

                _currentPoints = new List<Vector3> { hit.Value };
                _currentLine.positionCount = 1;
                _currentLine.SetPosition(0, hit.Value);
            }
        }
        else if (Input.GetMouseButton(0) && _currentLine != null)
        {
            Vector3? hit = RaycastToEarthPosition(Input.mousePosition);
            if (hit.HasValue)
            {
                float minDist = 0.01f;
                if (_currentPoints.Count == 0 ||
                    Vector3.Distance(hit.Value, _currentPoints[_currentPoints.Count - 1]) > minDist)
                {
                    _currentPoints.Add(hit.Value);
                    _currentLine.positionCount = _currentPoints.Count;
                    _currentLine.SetPosition(_currentPoints.Count - 1, hit.Value);
                }
            }
        }
        else if (Input.GetMouseButtonUp(0) && _currentLineObj != null)
        {
            if (_currentPoints.Count < 2)
            {
                Destroy(_currentLineObj);
            }
            else
            {
                _lineObjects.Add(_currentLineObj);
                SaveLinesAsJSON();
            }
            _currentLineObj = null;
            _currentLine = null;
            _currentPoints = null;
        }
    }

    private void HandleColorSelection()
    {
        for (int i = 0; i < 10; i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha0 + i))
            {
                int keyNumber = (i == 0) ? 9 : i - 1;
                SetCurrentColorIndex(keyNumber);
            }
        }
    }

    private void CreateDefaultMarkerPrefab()
    {
        GameObject markerObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        markerObj.name = "Marker";
        markerObj.transform.localScale = Vector3.one * 0.05f;

        GameObject.Destroy(markerObj.GetComponent<Collider>());

        Material markerMaterial = new Material(Shader.Find("Standard"));
        markerMaterial.color = markerColors[0];
        markerObj.GetComponent<Renderer>().material = markerMaterial;

        markerPrefab = markerObj;

        DontDestroyOnLoad(markerObj);

        Debug.Log("Created default marker prefab");
    }

    private void CreateMarkerLabel(GameObject marker, MarkerData data, int index)
    {
        GameObject labelObj = new GameObject("Label");
        labelObj.transform.SetParent(markersParent, true);

        TextMesh textMesh = labelObj.AddComponent<TextMesh>();
        textMesh.text = $"#{index + 1}";
        textMesh.anchor = TextAnchor.LowerCenter;
        textMesh.alignment = TextAlignment.Center;
        textMesh.characterSize = labelBaseCharSize;
        textMesh.fontSize = 64;
        textMesh.color = Color.white;

        data.labelMesh = textMesh;
    }

    private void UpdateMarkerLabelText(MarkerData data, int index)
    {
        if (data.labelMesh == null) return;
        string display = string.IsNullOrEmpty(data.name)
            ? $"#{index + 1}"
            : $"#{index + 1} {data.name}";
        data.labelMesh.text = display;
    }

    private void UpdateMarkerLabels()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 earthCenter = earthRenderer != null ? earthRenderer.transform.position : Vector3.zero;

        for (int i = 0; i < markers.Count; i++)
        {
            var data = markers[i];
            if (data.labelMesh == null) continue;

            Vector3 markerPos = data.gameObject.transform.position;
            float distance = Vector3.Distance(cam.transform.position, markerPos);

            if (distance > labelMaxDisplayDistance)
            {
                data.labelMesh.gameObject.SetActive(false);
                continue;
            }

            data.labelMesh.gameObject.SetActive(true);

            // Position above the marker along surface normal
            Vector3 normal = (markerPos - earthCenter).normalized;
            float markerRadius = 0.5f * data.gameObject.transform.lossyScale.x;
            data.labelMesh.transform.position = markerPos + normal * (markerRadius + 0.02f);

            // Scale character size proportionally to distance for constant screen size
            float scale = distance / labelReferenceDistance;
            data.labelMesh.characterSize = labelBaseCharSize * scale;

            // Billboard: face the camera
            Vector3 dir = data.labelMesh.transform.position - cam.transform.position;
            if (dir != Vector3.zero)
                data.labelMesh.transform.rotation = Quaternion.LookRotation(dir);
        }
    }

    private void CreatePlateOverlaySphere()
    {
        if (earthRenderer == null)
        {
            Debug.LogError("Cannot create plate overlay: earthRenderer is not assigned!");
            return;
        }

        Transform earthTransform = earthRenderer.transform;

        GameObject overlayObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        overlayObj.name = "PlateOverlay";
        overlayObj.transform.SetParent(earthTransform, false);
        overlayObj.transform.localScale = Vector3.one * 0.205f;

        Destroy(overlayObj.GetComponent<Collider>());

        Shader overlayShader = Shader.Find("Custom/PlateOverlayShader");
        if (overlayShader == null)
        {
            Debug.LogError("PlateOverlayShader not found! Falling back to Diffuse.");
            overlayShader = Shader.Find("Diffuse");
        }

        Material mat = new Material(overlayShader);
        Renderer renderer = overlayObj.GetComponent<Renderer>();
        renderer.material = mat;

        _plateOverlayObject = overlayObj;
        _plateOverlayRenderer = renderer;

        Debug.Log("Created plate overlay sphere");
    }

    #endregion

    #region 人文图层（势力范围 / 交通线 / 聚落 / 地名）

    private HumanGeographyData _humanData;
    private string _humanMapName;
    private GameObject _humanOverlayObject;
    private Renderer _humanOverlayRenderer;
    private Texture2D _humanOverlayTexture;
    private Transform _humanLabelsParent;
    private bool _humanOverlayVisible = true;
    private readonly List<GameObject> _humanLabelObjects = new List<GameObject>();
    private readonly List<TextMesh> _humanLabelMeshes = new List<TextMesh>();
    private readonly List<float> _humanLabelSizes = new List<float>();
    private readonly List<Color> _humanLabelColors = new List<Color>();

    /// <summary>当前是否已加载人文图层</summary>
    public bool HasHumanGeography => _humanData != null && _humanData.HasContent;

    /// <summary>当前人文图层对应的地图名</summary>
    public string HumanMapName => _humanMapName;

    /// <summary>当前人文图层的聚落数量</summary>
    public int HumanSettlementCount => _humanData != null && _humanData.settlements != null
        ? _humanData.settlements.Count : 0;

    /// <summary>当前人文图层的国家数量</summary>
    public int HumanNationCount => _humanData != null && _humanData.nations != null
        ? _humanData.nations.Count : 0;

    /// <summary>
    /// 设置（或清除）人文图层：势力范围染色 + 交通线 + 聚落点叠在地球表面，
    /// 镇以上的聚落再附带中文地名标签。
    /// </summary>
    public void SetHumanGeography(HumanGeographyData data, string mapBaseName)
    {
        ClearHumanGeography();

        _humanData = data;
        _humanMapName = mapBaseName;

        // 云层预览的湿度场与人文数据存在同一个文件里，这里一起更新
        UpdateCloudMoisture(data);

        if (data == null || !data.HasContent)
        {
            Debug.Log($"地图 {mapBaseName} 没有人文数据");
            return;
        }

        BuildHumanOverlayTexture(data);
        CreateHumanOverlaySphere();
        BuildHumanLabels(data);
        SetHumanOverlayVisible(_humanOverlayVisible);

        Debug.Log($"人文图层：{data.settlements.Count} 聚落 / {data.nations.Count} 国 / " +
                  $"{data.routes.Count} 条交通线 / {data.features.Count} 处自然地名");
    }

    /// <summary>显示或隐藏整个人文图层</summary>
    public void SetHumanOverlayVisible(bool visible)
    {
        _humanOverlayVisible = visible;

        if (_humanOverlayObject != null)
            _humanOverlayObject.SetActive(visible && HasHumanGeography);

        for (int i = 0; i < _humanLabelObjects.Count; i++)
            if (_humanLabelObjects[i] != null)
                _humanLabelObjects[i].SetActive(visible && HasHumanGeography);
    }

    public bool IsHumanOverlayVisible => _humanOverlayVisible;

    /// <summary>
    /// 导出当前地图的「命名请求」文件（&lt;地图名&gt;_names.json）：
    /// 里面是每个聚落/自然要素/国家的地理与人文背景 + 空的 name 字段，
    /// 可以直接交给 AI 批量起中文地名。返回写出的文件路径。
    /// </summary>
    public string ExportNamingRequest()
    {
        if (!HasHumanGeography)
            return null;

        return HumanGeographyData.ExportNamingRequest(_humanMapName, _humanData);
    }

    /// <summary>
    /// 导入地名：默认读取 &lt;地图名&gt;_names.json（即导出后填好名字的同一份文件）。
    /// 成功后刷新地球上的地名标签，并把结果写回 &lt;地图名&gt;_human.json。
    /// 返回值带有「解析了多少条 / 应用了多少条」等细节，便于界面提示。
    /// </summary>
    public HumanGeographyData.NameImportResult ImportNames(string path = null)
    {
        var none = new HumanGeographyData.NameImportResult();
        if (!HasHumanGeography)
            return none;

        if (string.IsNullOrEmpty(path))
            path = HumanGeographyData.GetNamingRequestPath(_humanMapName);

        var result = HumanGeographyData.ApplyNamesFromFile(path, _humanData);
        if (result.applied > 0)
        {
            HumanGeographyData.Save(_humanMapName, _humanData);
            RefreshHumanLabels();
        }
        return result;
    }

    /// <summary>按当前数据重建聚落地名标签（导入新地名后调用）。</summary>
    private void RefreshHumanLabels()
    {
        for (int i = 0; i < _humanLabelObjects.Count; i++)
            if (_humanLabelObjects[i] != null)
                Destroy(_humanLabelObjects[i]);

        _humanLabelObjects.Clear();
        _humanLabelMeshes.Clear();
        _humanLabelSizes.Clear();
        _humanLabelColors.Clear();

        if (_humanData != null && _humanData.HasContent)
            BuildHumanLabels(_humanData);

        SetHumanOverlayVisible(_humanOverlayVisible);
    }

    private void ClearHumanGeography()
    {
        for (int i = 0; i < _humanLabelObjects.Count; i++)
            if (_humanLabelObjects[i] != null)
                Destroy(_humanLabelObjects[i]);

        _humanLabelObjects.Clear();
        _humanLabelMeshes.Clear();
        _humanLabelSizes.Clear();
        _humanLabelColors.Clear();

        if (_humanOverlayObject != null)
        {
            Destroy(_humanOverlayObject);
            _humanOverlayObject = null;
            _humanOverlayRenderer = null;
        }

        if (_humanOverlayTexture != null)
        {
            Destroy(_humanOverlayTexture);
            _humanOverlayTexture = null;
        }

        _humanData = null;
    }

    /// <summary>把势力范围、交通线与聚落点画进一张半透明贴图。</summary>
    private void BuildHumanOverlayTexture(HumanGeographyData data)
    {
        int tw = Mathf.Max(2, data.territoryWidth);
        int th = Mathf.Max(2, data.territoryHeight);
        int scale = Mathf.Clamp(1024 / tw, 2, 8);
        int texW = tw * scale;
        int texH = th * scale;

        var pixels = new Color32[texW * texH];
        var clear = new Color32(0, 0, 0, 0);
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = clear;

        // 1) 势力范围（按国家染色，国界更亮）
        byte[] territory = data.GetTerritory();
        if (territory != null && territory.Length >= tw * th)
        {
            for (int cy = 0; cy < th; cy++)
            {
                for (int cx = 0; cx < tw; cx++)
                {
                    int nation = territory[cy * tw + cx];
                    if (nation == 0)
                        continue;

                    bool border = IsTerritoryBorder(territory, tw, th, cx, cy, (byte)nation);
                    Color32 c = NationTint(data, nation - 1, border ? (byte)140 : (byte)62);
                    FillBlock(pixels, texW, texH, cx * scale, cy * scale, scale, c);
                }
            }
        }

        // 2) 交通线
        for (int r = 0; r < data.routes.Count; r++)
        {
            var route = data.routes[r];
            if (route.points == null || route.points.Count < 2)
                continue;

            Color32 c = route.kind == 2 ? new Color32(120, 210, 235, 200)
                      : route.kind == 1 ? new Color32(110, 175, 235, 210)
                      : new Color32(225, 190, 130, 190);
            for (int p = 0; p < route.points.Count - 1; p++)
                DrawLineUV(pixels, texW, texH, route.points[p], route.points[p + 1], c, 2);
        }

        // 3) 聚落点（大小表示等级，颜色取所属国家）
        for (int s = 0; s < data.settlements.Count; s++)
        {
            var st = data.settlements[s];
            int radius = st.level >= 4 ? 8 : st.level == 3 ? 6 : st.level == 2 ? 4 : 3;
            Color32 fill = NationTint(data, st.nationIndex, 235);
            FillCircleUV(pixels, texW, texH, st.u, st.v, radius, fill);
            if (st.level >= 4)
                FillCircleUV(pixels, texW, texH, st.u, st.v, Mathf.Max(1, radius / 2),
                    new Color32(255, 245, 210, 255));
            else if (st.isCapital)
                FillCircleUV(pixels, texW, texH, st.u, st.v, 1, new Color32(255, 255, 255, 255));
        }

        var tex = new Texture2D(texW, texH, TextureFormat.RGBA32, false);
        tex.SetPixels32(pixels);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.Apply();
        _humanOverlayTexture = tex;
    }

    private static bool IsTerritoryBorder(byte[] territory, int tw, int th, int cx, int cy, byte nation)
    {
        for (int dy = -1; dy <= 1; dy++)
        {
            int ny = cy + dy;
            if (ny < 0 || ny >= th)
                continue;
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = (cx + dx + tw) % tw;
                if (territory[ny * tw + nx] != nation)
                    return true;
            }
        }
        return false;
    }

    private static Color32 NationTint(HumanGeographyData data, int nationIndex, byte alpha)
    {
        if (data.nations == null || nationIndex < 0 || nationIndex >= data.nations.Count)
            return new Color32(200, 200, 200, alpha);

        var n = data.nations[nationIndex];
        byte r = (byte)Mathf.Clamp(n.r * 255f * 0.75f + 60f, 0f, 255f);
        byte g = (byte)Mathf.Clamp(n.g * 255f * 0.75f + 60f, 0f, 255f);
        byte b = (byte)Mathf.Clamp(n.b * 255f * 0.75f + 60f, 0f, 255f);
        return new Color32(r, g, b, alpha);
    }

    private static void FillBlock(Color32[] pixels, int texW, int texH, int x0, int y0, int size, Color32 c)
    {
        for (int y = y0; y < y0 + size && y < texH; y++)
        {
            if (y < 0)
                continue;
            for (int x = x0; x < x0 + size && x < texW; x++)
            {
                if (x < 0)
                    continue;
                pixels[y * texW + x] = c;
            }
        }
    }

    private static void FillCircleUV(Color32[] pixels, int texW, int texH, float u, float v, int radius, Color32 c)
    {
        int cx = Mathf.RoundToInt(u * texW);
        int cy = Mathf.RoundToInt(v * texH);
        for (int y = -radius; y <= radius; y++)
        {
            int py = cy + y;
            if (py < 0 || py >= texH)
                continue;
            for (int x = -radius; x <= radius; x++)
            {
                if (x * x + y * y > radius * radius)
                    continue;
                int px = ((cx + x) % texW + texW) % texW;
                pixels[py * texW + px] = c;
            }
        }
    }

    private static void DrawLineUV(Color32[] pixels, int texW, int texH,
        Vector2 a, Vector2 b, Color32 c, int width)
    {
        int x0 = Mathf.RoundToInt(a.x * texW);
        int y0 = Mathf.RoundToInt(a.y * texH);
        int x1 = Mathf.RoundToInt(b.x * texW);
        int y1 = Mathf.RoundToInt(b.y * texH);

        // 经度接缝：直接跨图绘制会拉出横贯整图的线，这里就近取短边
        if (Mathf.Abs(x1 - x0) > texW / 2)
        {
            if (x1 > x0) x0 += texW; else x1 += texW;
        }

        int dx = Mathf.Abs(x1 - x0);
        int dy = Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        int guard = Mathf.Min(4096, dx + dy + 4);

        while (guard-- > 0)
        {
            for (int oy = -width / 2; oy <= width / 2; oy++)
            {
                int py = y0 + oy;
                if (py < 0 || py >= texH)
                    continue;
                for (int ox = -width / 2; ox <= width / 2; ox++)
                {
                    int px = ((x0 + ox) % texW + texW) % texW;
                    pixels[py * texW + px] = c;
                }
            }

            if (x0 == x1 && y0 == y1)
                break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    private void CreateHumanOverlaySphere()
    {
        if (earthRenderer == null)
            return;

        Transform earthTransform = earthRenderer.transform;
        var overlayObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        overlayObj.name = "HumanOverlay";
        overlayObj.transform.SetParent(earthTransform, false);
        overlayObj.transform.localScale = Vector3.one * 0.2065f;
        Destroy(overlayObj.GetComponent<Collider>());

        Shader shader = Shader.Find("Custom/PlateOverlayShader");
        if (shader == null)
            shader = Shader.Find("Diffuse");

        var mat = new Material(shader);
        mat.SetTexture("_MainTex", _humanOverlayTexture);
        var renderer = overlayObj.GetComponent<Renderer>();
        renderer.material = mat;

        _humanOverlayObject = overlayObj;
        _humanOverlayRenderer = renderer;
    }

    private void BuildHumanLabels(HumanGeographyData data)
    {
        if (earthRenderer == null)
            return;

        if (_humanLabelsParent == null)
        {
            _humanLabelsParent = new GameObject("HumanLabels").transform;
            _humanLabelsParent.SetParent(transform);
        }

        Vector3 earthCenter = earthRenderer.transform.position;
        float earthRadius = earthRenderer.bounds.extents.x;

        for (int i = 0; i < data.settlements.Count; i++)
        {
            var s = data.settlements[i];
            if (s.level < 2)
                continue;                       // 村庄只画点，不挂标签，避免过密

            Vector3 pos = UVToWorld(s.u, s.v, earthCenter, earthRadius);

            var go = new GameObject($"Settlement_{i}");
            go.transform.SetParent(_humanLabelsParent, true);
            go.transform.position = pos;

            var textMesh = go.AddComponent<TextMesh>();
            textMesh.text = s.level >= 3
                ? $"{s.name}\n{FormatPopulation(s.population)}"
                : s.name;
            textMesh.anchor = TextAnchor.LowerCenter;
            textMesh.alignment = TextAlignment.Center;
            textMesh.fontSize = 64;
            textMesh.characterSize = s.level >= 4 ? 0.075f : (s.level == 3 ? 0.058f : 0.045f);
            Color labelColor = s.level >= 4 ? new Color(1f, 0.92f, 0.62f)
                             : (s.level == 3 ? new Color(1f, 1f, 1f) : new Color(0.86f, 0.90f, 0.95f));
            textMesh.color = labelColor;

            _humanLabelObjects.Add(go);
            _humanLabelMeshes.Add(textMesh);
            _humanLabelSizes.Add(textMesh.characterSize);
            _humanLabelColors.Add(labelColor);
        }
    }

    private static string FormatPopulation(int population)
    {
        if (population >= 10000000)
            return $"{population / 10000000f:F1}千万";
        if (population >= 10000)
            return $"{population / 10000f:F0}万";
        return population.ToString();
    }

    /// <summary>
    /// 地名标签：只显示朝向相机那一面（转到地球背面即消失），
    /// 并随距离缩放、始终面向相机，靠近地平线时渐隐。
    /// </summary>
    private void UpdateHumanLabels()
    {
        if (_humanLabelMeshes.Count == 0 || !_humanOverlayVisible)
            return;

        Camera cam = Camera.main;
        if (cam == null || earthRenderer == null)
            return;

        Vector3 earthCenter = earthRenderer.transform.position;
        float earthRadius = earthRenderer.bounds.extents.x;

        Vector3 camPos = cam.transform.position;
        Vector3 toCam = camPos - earthCenter;
        float camDistance = toCam.magnitude;
        if (camDistance < 1e-4f)
            return;

        Vector3 toCamDir = toCam / camDistance;

        // 球面可见性阈值：半径 R 的球在距离 D 处，法线 n 的表面点可见 ⟺ n·(相机方向) > R/D
        float horizon = earthRadius * 1.012f / camDistance;

        for (int i = 0; i < _humanLabelMeshes.Count; i++)
        {
            var mesh = _humanLabelMeshes[i];
            if (mesh == null)
                continue;

            Vector3 world = mesh.transform.position;
            Vector3 normal = (world - earthCenter).normalized;
            float facing = Vector3.Dot(normal, toCamDir) - horizon;

            float distance = Vector3.Distance(camPos, world);
            bool visible = facing > 0f && distance <= labelMaxDisplayDistance * 1.6f;

            if (mesh.gameObject.activeSelf != visible)
                mesh.gameObject.SetActive(visible);
            if (!visible)
                continue;

            // 靠近地平线时淡出，避免突然跳出
            Color c = _humanLabelColors[i];
            c.a = Mathf.Clamp01(facing / 0.08f);
            mesh.color = c;

            // 贴在地表上方
            mesh.transform.position = earthCenter + normal * (earthRadius * 1.012f);

            float scale = distance / labelReferenceDistance;
            mesh.characterSize = _humanLabelSizes[i] * Mathf.Max(0.35f, scale);

            Vector3 dir = mesh.transform.position - camPos;
            if (dir != Vector3.zero)
                mesh.transform.rotation = Quaternion.LookRotation(dir);
        }
    }

    #endregion
}

[System.Serializable]
public class MarkerData
{
    public GameObject gameObject;
    public Vector3 position;
    public int colorIndex;
    public float timestamp;
    public string name = "";
    public TextMesh labelMesh;
}

[System.Serializable]
public class LineEntry
{
    public int colorIndex;
    public System.Collections.Generic.List<Vector2Serialized> points;
}

[System.Serializable]
public class Vector2Serialized
{
    public float x;
    public float y;
}

[System.Serializable]
public class LineDataList
{
    public System.Collections.Generic.List<LineEntry> lines = new System.Collections.Generic.List<LineEntry>();
}

[System.Serializable]
public class MarkerEntry
{
    public int colorIndex;
    public string name;
    public float u;
    public float v;
}

[System.Serializable]
public class MarkerDataList
{
    public System.Collections.Generic.List<MarkerEntry> markers = new System.Collections.Generic.List<MarkerEntry>();
}