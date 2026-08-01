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

    #endregion

    #region Private Methods

    private void HandleMarkerInput()
    {
        if (Input.GetMouseButtonDown(0))
        {
            Vector3? hitPosition = RaycastToEarthPosition(Input.mousePosition);

            if (hitPosition.HasValue)
            {
                AddMarkerAtPosition(hitPosition.Value, currentColorIndex);
            }
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