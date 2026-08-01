using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class SunlightDirectionController : MonoBehaviour
{
    [SerializeField] private Light sunLight;
    [SerializeField] private bool showArrow = true;
    [SerializeField] private Transform earthAxis;
    [SerializeField] private float axisLength = 8f;
    [SerializeField] private float axisRadius = 0.008f;

    private GameObject _arrowObject;
    private bool _axisInitialized;
    private bool _cachedShowArrow;

#if UNITY_EDITOR
    private void OnEnable()
    {
        if (!Application.isPlaying)
            EditorApplication.update += EditorUpdate;
    }

    private void OnDisable()
    {
        if (!Application.isPlaying)
            EditorApplication.update -= EditorUpdate;
    }

    private void EditorUpdate()
    {
        if (Application.isPlaying) return;
        UpdateArrow();
    }
#endif

    private void LateUpdate()
    {
        UpdateArrow();
    }

    private void UpdateArrow()
    {
        if (sunLight == null) return;

        Vector3 lightDirection = sunLight.transform.forward;

        // Arrow points along local +X = lightDirection (toward Earth / sunlit side).
        // Child at local (-15,0,0) = sunDirection side (where the sun is).
        transform.rotation = Quaternion.LookRotation(lightDirection, Vector3.up) * Quaternion.Euler(0f, -90f, 0f);

        EnsureAxisVisual();

        if (_cachedShowArrow != showArrow)
        {
            _cachedShowArrow = showArrow;
            if (_arrowObject == null && transform.childCount > 0)
                _arrowObject = transform.GetChild(0).gameObject;
            if (_arrowObject != null)
                _arrowObject.SetActive(showArrow);
            if (earthAxis != null)
                earthAxis.gameObject.SetActive(showArrow);
        }
    }

    public void SetArrowVisible(bool visible)
    {
        showArrow = visible;
    }

    private void EnsureAxisVisual()
    {
        if (_axisInitialized || earthAxis == null) return;

        // Create the thin cylinder visual as a child of the existing EarthAxis
        if (earthAxis.childCount == 0)
        {
            GameObject cylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cylinder.transform.SetParent(earthAxis, false);
            Destroy(cylinder.GetComponent<Collider>());

            // Cylinder default is 2 units tall along Y; scale to desired length
            float halfLen = axisLength * 0.5f;
            cylinder.transform.localScale = new Vector3(axisRadius, halfLen, axisRadius);
            cylinder.transform.localPosition = new Vector3(0f, halfLen, 0f);

            Material mat = new Material(Shader.Find("Unlit/Color"));
            mat.color = new Color(1f, 0.85f, 0.3f, 1f);
            cylinder.GetComponent<Renderer>().material = mat;
        }

        earthAxis.gameObject.SetActive(showArrow);
        _cachedShowArrow = showArrow;
        _axisInitialized = true;
    }
}
