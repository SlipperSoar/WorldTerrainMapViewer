using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 阳光方向指示器：让箭头沿光照方向指向星球，并在星球北极端画一根地轴。
///
/// 这两样东西的尺寸都必须**跟随星球大小**：
///   - 箭头原本是写死的世界坐标（离中心 5 个单位、缩放 10），
///     星球放大到 2.5 倍（半径 5）时整根箭头会插进星球里 —— 就是「穿模」
///   - 地轴原本是固定 8 个单位长的圆柱，星球变大后会被埋进球体
///
/// 因此这里以「首次见到的星球半径」为基准，按比例缩放位置与尺寸；
/// 星球半径取 <see cref="EarthManager.PlanetWorldRadius"/>（世界空间的真实半径）。
/// </summary>
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

    // 首次见到的星球半径，作为「1 倍」基准；之后所有尺寸按比例换算
    private float _basePlanetRadius = -1f;
    private Vector3 _arrowBaseLocalPosition;
    private Vector3 _arrowBaseLocalScale;

    private Transform _axisCylinder;
    private float _axisAppliedRadius = -1f;

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
            if (_arrowObject != null)
                _arrowObject.SetActive(showArrow);
            if (earthAxis != null)
                earthAxis.gameObject.SetActive(showArrow);
        }

        ApplyPlanetScale();
    }

    /// <summary>当前星球的世界半径；拿不到时返回 0（表示保持原尺寸）。</summary>
    private float GetPlanetRadius()
    {
        if (EarthManager.Instance != null)
        {
            float radius = EarthManager.Instance.PlanetWorldRadius;
            if (radius > 0f)
                return radius;
        }
        return 0f;
    }

    /// <summary>
    /// 按星球半径等比缩放箭头与地轴。
    ///
    /// 用**比例**换算而不是每次直接赋值，因此可以在不同尺寸的星球之间来回切换而不累积误差。
    /// </summary>
    private void ApplyPlanetScale()
    {
        float radius = GetPlanetRadius();
        if (radius <= 0f)
            return;

        if (_basePlanetRadius < 0f)
        {
            _basePlanetRadius = radius;
            if (_arrowObject != null)
            {
                _arrowBaseLocalPosition = _arrowObject.transform.localPosition;
                _arrowBaseLocalScale = _arrowObject.transform.localScale;
            }
        }

        float k = radius / _basePlanetRadius;
        if (k <= 0f || float.IsNaN(k))
            return;

        if (_arrowObject != null)
        {
            _arrowObject.transform.localPosition = _arrowBaseLocalPosition * k;
            _arrowObject.transform.localScale = _arrowBaseLocalScale * k;
        }

        // 地轴：高度至少盖住球体（2R），并留出 20% 余量
        if (_axisCylinder != null && !Mathf.Approximately(k, _axisAppliedRadius))
        {
            _axisAppliedRadius = k;

            float halfLen = Mathf.Max(axisLength * 0.5f * k, radius * 1.2f);
            _axisCylinder.localScale = new Vector3(axisRadius * k, halfLen, axisRadius * k);
            _axisCylinder.localPosition = new Vector3(0f, halfLen, 0f);
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

            _axisCylinder = cylinder.transform;
        }
        else
        {
            _axisCylinder = earthAxis.GetChild(0);
        }

        earthAxis.gameObject.SetActive(showArrow);
        _cachedShowArrow = showArrow;
        _axisInitialized = true;

        // 找到箭头子物体（不是地轴那个）
        if (_arrowObject == null)
        {
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (earthAxis != null && child == earthAxis) continue;
                _arrowObject = child.gameObject;
                break;
            }
        }
    }
}
