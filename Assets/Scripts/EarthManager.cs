using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

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
    }

    void Update()
    {
        HandleMarkerInput();
        HandleColorSelection();
        UpdateMarkerLabels();
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
        float x = Mathf.Cos(lat) * Mathf.Cos(lon);
        float y = Mathf.Sin(lat);
        float z = Mathf.Cos(lat) * Mathf.Sin(lon);
        // TransformDirection applies rotation only (no scale), then use world-space radius
        Vector3 worldDir = earthRenderer.transform.TransformDirection(new Vector3(x, y, z)).normalized;
        return earthCenter + worldDir * earthRadius;
    }

    private void DrawLineOnTexture(Texture2D tex, Vector3 p1, Vector3 p2,
        Color color, Vector3 earthCenter, int width, int height)
    {
        Vector2 uv1 = WorldToUV(p1, earthCenter);
        Vector2 uv2 = WorldToUV(p2, earthCenter);

        int x1 = Mathf.RoundToInt(uv1.x * width);
        int y1 = Mathf.RoundToInt(uv1.y * height);
        int x2 = Mathf.RoundToInt(uv2.x * width);
        int y2 = Mathf.RoundToInt(uv2.y * height);

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
        float u = 0.5f + Mathf.Atan2(dir.z, dir.x) / (2f * Mathf.PI);
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