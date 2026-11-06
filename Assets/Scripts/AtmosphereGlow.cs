using UnityEngine;

/// <summary>
/// 行星大气边缘辉光：在星球外面套一层**比云层还大**的透明球壳，只画 Fresnel 边缘光环。
///
/// 为什么独立成一层（而不是算在地表 shader 里）：
///   - 地表开了凹凸位移后，贴在表面的辉光会跟着顶点变形、几乎看不见；
///   - 云层球壳（半径 ×1.12）套在星球外，会把贴在地表的辉光整个盖住。
/// 本球壳默认半径 ×(1+0.16)=×1.16，渲染队列也比云层靠后（Transparent+10），
/// 因此辉光始终是**最外一圈**，与地形凹凸、云层显隐都无关。
///
/// 由 <see cref="EarthManager"/> 在 Awake 里自动获取/创建，不需要改场景文件。
/// 球壳缩放算法与 <see cref="CloudLayer"/> 保持一致（用星球自身网格的世界半径，
/// 而不是含子物体的 Renderer.bounds）。
/// </summary>
[DisallowMultipleComponent]
public class AtmosphereGlow : MonoBehaviour
{
    [Header("引用")]
    [SerializeField, Tooltip("星球（地球）的 Renderer；留空时自动取子物体")]
    private Renderer earthRenderer;

    [Header("球壳")]
    [SerializeField, Range(0.13f, 0.6f)]
    [Tooltip("大气层高出地表的厚度（星球半径的倍数）。必须大于云层的 0.12，才能套在云层外")]
    private float shellThickness = 0.16f;

    [Header("辉光")]
    [SerializeField] private Color glowColor = new Color(0.45f, 0.72f, 1.0f, 1f);
    [SerializeField, Range(0.5f, 8f)] private float edgeFalloff = 2.2f;
    [SerializeField, Range(0f, 3f)] private float glowStrength = 1.1f;
    [SerializeField, Range(0f, 2f)] private float sunBoost = 0.6f;
    [SerializeField, Tooltip("启动时是否显示大气辉光")] private bool startVisible = true;

    private GameObject _sphereObject;
    private Material _material;

    public bool IsVisible => _sphereObject != null && _sphereObject.activeSelf;

    #region Unity

    private void Awake()
    {
        if (earthRenderer == null)
            earthRenderer = GetComponentInChildren<Renderer>();
    }

    private void Start()
    {
        // 启动时就建好并按 startVisible 显隐，保证画面与开关一致
        SetVisible(startVisible);
    }

    #endregion

    #region 公开接口

    public void SetEarthRenderer(Renderer renderer)
    {
        earthRenderer = renderer;
        if (_sphereObject != null)
            _sphereObject.transform.SetParent(renderer != null ? renderer.transform : null, false);
        RefreshScale();
    }

    public void SetVisible(bool visible)
    {
        EnsureSphere();
        if (_sphereObject != null)
            _sphereObject.SetActive(visible);
    }

    /// <summary>星球尺寸变化后重算球壳大小（由 EarthManager.SetPlanetRadiusKm 调用）。</summary>
    public void RefreshScale()
    {
        if (_sphereObject != null)
            ApplyScale();
    }

    public void SetColor(Color c)
    {
        glowColor = c;
        if (_material != null) _material.SetColor("_FresnelColor", c);
    }

    public void SetStrength(float s)
    {
        glowStrength = s;
        if (_material != null) _material.SetFloat("_FresnelStrength", s);
    }

    #endregion

    #region 内部

    private void EnsureSphere()
    {
        if (_sphereObject != null)
            return;

        if (earthRenderer == null)
            earthRenderer = GetComponentInChildren<Renderer>();
        if (earthRenderer == null)
        {
            Debug.LogWarning("AtmosphereGlow: 没有找到星球的 Renderer，辉光球壳无法建立");
            return;
        }

        Shader shader = Shader.Find("Custom/AtmosphereGlowShader");
        if (shader == null)
        {
            Debug.LogError("AtmosphereGlowShader 未找到，辉光球壳无法建立");
            return;
        }

        _sphereObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _sphereObject.name = "AtmosphereGlow";
        _sphereObject.transform.SetParent(earthRenderer.transform, false);
        ApplyScale();

        // 不参与射线检测（否则会挡住在地表打标记）
        var collider = _sphereObject.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);

        _material = new Material(shader);
        _material.SetColor("_FresnelColor", glowColor);
        _material.SetFloat("_FresnelPower", edgeFalloff);
        _material.SetFloat("_FresnelStrength", glowStrength);
        _material.SetFloat("_SunInfluence", sunBoost);

        Renderer r = _sphereObject.GetComponent<Renderer>();
        r.material = _material;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;

        Debug.Log("大气辉光：已建立球壳（半径倍率 " + (1f + shellThickness).ToString("F2") + "）");
    }

    /// <summary>
    /// 把球壳缩放到「星球世界半径 × (1 + thickness)」。
    /// 算法与 CloudLayer.ApplyScale 一致：用星球自身网格的世界半径，避免把子物体算进去。
    /// </summary>
    private void ApplyScale()
    {
        if (_sphereObject == null || earthRenderer == null)
            return;

        _sphereObject.transform.localPosition = Vector3.zero;
        _sphereObject.transform.localRotation = Quaternion.identity;

        float earthWorldRadius = GetEarthWorldRadius();
        Transform parent = _sphereObject.transform.parent;
        Vector3 ps = parent != null ? parent.lossyScale : Vector3.one;
        float parentScale = Mathf.Min(Mathf.Abs(ps.x), Mathf.Min(Mathf.Abs(ps.y), Mathf.Abs(ps.z)));
        if (parentScale <= 1e-6f) parentScale = 1f;

        float thickness = Mathf.Max(0.13f, shellThickness);   // 保证大于云层 0.12
        float desiredWorldRadius = earthWorldRadius * (1f + thickness);

        // 图元球网格半径 0.5（直径 1），世界半径 R 需要 2R / parentScale
        float localScale = 2f * desiredWorldRadius / parentScale;
        _sphereObject.transform.localScale = new Vector3(localScale, localScale, localScale);
    }

    /// <summary>星球的世界空间半径（用自身网格包围盒，不用含子物体的 Renderer.bounds）。</summary>
    private float GetEarthWorldRadius()
    {
        if (earthRenderer == null)
            return 1f;

        Transform earth = earthRenderer.transform;
        MeshFilter filter = earth.GetComponent<MeshFilter>();
        if (filter != null && filter.sharedMesh != null)
        {
            Bounds mb = filter.sharedMesh.bounds;
            float meshRadius = Mathf.Max(mb.extents.x, Mathf.Max(mb.extents.y, mb.extents.z));
            if (meshRadius > 1e-5f)
            {
                Vector3 s = earth.lossyScale;
                float maxScale = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                return meshRadius * maxScale;
            }
        }

        Vector3 extents = earthRenderer.bounds.extents;
        float r = Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z));
        return r > 1e-4f ? r : 1f;
    }

    private void OnValidate()
    {
        if (_material == null)
            return;
        _material.SetColor("_FresnelColor", glowColor);
        _material.SetFloat("_FresnelPower", edgeFalloff);
        _material.SetFloat("_FresnelStrength", glowStrength);
        _material.SetFloat("_SunInfluence", sunBoost);
    }

    private void OnDestroy()
    {
        if (_material != null)
            Destroy(_material);
    }

    #endregion
}
