using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System.IO;

public class UIController : MonoBehaviour
{
    #region Properties

    [SerializeField] private Text mapFileName;
    [SerializeField] private Transform imageListContainer;
    [SerializeField] private GameObject imageListItemPrefab;
    [SerializeField] private RayCastArea inOutArea;
    [SerializeField] private RectTransform uiPanel;
    [SerializeField] private float slideDuration = 0.3f;
    [SerializeField] private float slideDistance = 300f;

    [Space] [SerializeField, Tooltip("一键北极")]
    private Button northButton;

    [SerializeField, Tooltip("一键赤道")] private Button equatorButton;
    [SerializeField, Tooltip("一键南极")] private Button southButton;
    [SerializeField, Tooltip("一键俯视")] private Button topButton;

    [SerializeField] private Text earthRotate;
    [SerializeField] private CameraController cameraController;

    private Texture2D currentTexture;
    private List<string> availableImages = new List<string>();
    private List<GameObject> listItemObjects = new List<GameObject>();
    private bool isUIVisible = false;
    private Coroutine slideCoroutine;

    #endregion

    #region Unity

    void Start()
    {
        InitializeTween();
        InitializeEarth();
        InitializeImageList();
        LoadImagesFromStreamingAssets();
    }

    private void Update()
    {
        UpdateCameraRotationDisplay();
    }

    void OnDestroy()
    {
        if (currentTexture != null)
        {
            Destroy(currentTexture);
        }

        ClearListItemObjects();
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
                availableImages.Add(Path.GetFileName(file));
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
}