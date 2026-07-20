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

    private List<MarkerData> markers = new List<MarkerData>();
    private int currentColorIndex = 0;

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

        MarkerData markerData = new MarkerData
        {
            gameObject = marker,
            position = worldPosition,
            colorIndex = colorIndex,
            timestamp = Time.time
        };

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
                markers.RemoveAt(i);
                Destroy(marker);
                break;
            }
        }
    }

    public void ClearAllMarkers()
    {
        foreach (var marker in markers)
        {
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

    #endregion
}

[System.Serializable]
public class MarkerData
{
    public GameObject gameObject;
    public Vector3 position;
    public int colorIndex;
    public float timestamp;
}