using UnityEngine;
using WorldTerrain;

/// <summary>
/// 云层预览：在星球外侧套一层略大的半透明球壳，由**气候数据**驱动云的分布与流动。
///
/// 与「云层效果实现方案」的对应关系（方案原文是 Three.js 的，这里是 Unity 移植）：
///   - 半透明云层球壳（planetRadius × 1.008）           → 本类的 <see cref="EnsureSphere"/>
///   - 湿度场作为 uniforms 传入，调制噪声阈值           → <c>_MoistureTex</c> + 着色器里的 coverage
///   - 受盛行风带控制的流动（uv 随时间偏移）            → 着色器里的 <c>WindBand</c> + <c>_FlowSpeed</c>
///   - 多层 FBM 细节 + 按太阳方向的散射/银边            → 着色器里的 <c>Fbm</c> + <c>_SunDirection</c>
///   - 地轴倾角/季节造成的南北移动                      → <see cref="SeasonShift"/> → <c>_SeasonShift</c>
///
/// 云量不随机：湿度高的区域（赤道、迎风坡）云更浓，干旱区（沙漠、雨影区）云更稀。
/// </summary>
[DisallowMultipleComponent]
public class CloudLayer : MonoBehaviour
{
    [Header("引用")]
    [SerializeField, Tooltip("星球（地球）的 Renderer，云层球壳会挂在它下面")]
    private Renderer earthRenderer;

    [SerializeField, Tooltip("太阳平行光；留空时自动取 RenderSettings.sun")]
    private Light sunLight;

    [Header("球壳")]
    [SerializeField, Range(0.02f, 0.6f)]
    [Tooltip("云层高出地表的厚度，单位是星球半径的倍数（0.12 = 地表外 12% 半径）。" +
             "太薄会被地形位移的山峰穿破 —— 地形最大外凸约 0.15 倍半径")]
    private float shellThickness = 0.12f;

    [Header("云量")]
    [SerializeField, Range(0f, 1f), Tooltip("基准云量：越大云越多（0.65 ≈ 湿区半空有云、干区几乎无云）")]
    private float coverage = 0.65f;

    [SerializeField, Range(0f, 1f), Tooltip("湿度对云量的影响强度")]
    private float moistureInfluence = 0.55f;

    [SerializeField, Range(0f, 1f), Tooltip("河网/湖泊/三角洲的水汽加成")]
    private float waterInfluence = 0.30f;

    [Header("形态与流动")]
    [SerializeField, Range(1f, 12f), Tooltip("云缘锐度：越大边缘越硬")]
    private float sharpness = 5f;

    [SerializeField, Range(0.5f, 12f), Tooltip("基础噪声频率：越大云块越碎")]
    private float noiseScale = 3f;

    [SerializeField, Range(1f, 30f), Tooltip("细节噪声频率")]
    private float detailScale = 11f;

    [SerializeField, Range(0f, 1f), Tooltip("细节噪声占比")]
    private float detailAmount = 0.35f;

    [SerializeField, Range(0f, 2f), Tooltip("随风带流动的速度")]
    private float flowSpeed = 0.02f;

    [Header("光照")]
    [SerializeField, Range(0f, 1f)] private float maxOpacity = 0.92f;
    [SerializeField] private Color cloudColor = Color.white;
    [SerializeField] private Color shadowColor = new Color(0.42f, 0.47f, 0.58f, 1f);
    [SerializeField, Range(0f, 2f)] private float silverLining = 0.65f;

    private GameObject _sphereObject;
    private Renderer _sphereRenderer;
    private Material _material;
    private Texture2D _moistureTexture;
    private Texture2D _waterTexture;

    /// <summary>尺寸按世界包围盒算一次后打一条日志，方便核对是否与星球吻合。</summary>
    private bool _logScaleOnce = true;

    /// <summary>季节南北位移（年内的归一化位置 0-1 换算而来），由 <see cref="SetSeasonProgress"/> 设置。</summary>
    private float _seasonShift;

    public bool IsVisible => _sphereObject != null && _sphereObject.activeSelf;

    /// <summary>云层是否已经建好并拿到湿度数据。</summary>
    public bool HasMoisture => _moistureTexture != null;

    /// <summary>
    /// 期望的显隐状态，默认 **false**（组件刚加上时不该自己冒出来）。
    /// UI 开关按这个值初始化，保证「开关」与「画面」一致。
    /// </summary>
    [SerializeField, Tooltip("启动时是否显示云层（UI 开关按这个值初始化）")]
    private bool startVisible;

    #region Unity

    private void Awake()
    {
        if (earthRenderer == null)
            earthRenderer = GetComponentInChildren<Renderer>();
    }

    private void Start()
    {
        // 启动时就把球壳建好并按 startVisible 关掉：
        // 这样 UI 开关读到的初始值必然等于画面状态，不会出现「没勾选却显示」。
        SetVisible(startVisible);
    }

    private void Update()
    {
        if (_material == null)
            return;

        // 太阳方向：云层向阳面亮、背阳面暗、靠太阳一侧有银边
        Vector3 dir = GetSunDirection();
        _material.SetVector("_SunDirection", new Vector4(dir.x, dir.y, dir.z, 0f));
        _material.SetFloat("_SeasonShift", _seasonShift);
    }

    #endregion

    #region 公开接口

    /// <summary>设置星球引用（未在 Inspector 指定时使用）。</summary>
    public void SetEarthRenderer(Renderer renderer)
    {
        earthRenderer = renderer;
        if (_sphereObject != null)
        {
            _sphereObject.transform.SetParent(earthRenderer != null ? earthRenderer.transform : null, false);
            ApplyScale();
        }
    }

    /// <summary>设置太阳光引用。</summary>
    public void SetSunLight(Light light)
    {
        sunLight = light;
    }

    /// <summary>
    /// 设置云层的湿度场（等距圆柱投影，0-1）。
    /// 由地图的 <see cref="HumanGeographyData.GetMoistureField"/> 提供；
    /// 没有数据时云层退化为「均匀云量」，仍然可用。
    /// </summary>
    public void SetMoistureField(byte[] field, int width, int height)
    {
        if (field == null || width <= 0 || height <= 0 || field.Length < width * height)
        {
            ClearMoistureField();
            return;
        }

        EnsureSphere();
        if (_material == null)
            return;

        if (_moistureTexture != null)
            Destroy(_moistureTexture);

        var tex = new Texture2D(width, height, TextureFormat.R8, false, true);
        tex.name = "CloudMoisture";
        tex.wrapMode = TextureWrapMode.Repeat;      // 经度方向无缝
        tex.filterMode = FilterMode.Bilinear;
        tex.SetPixelData(field, 0);
        tex.Apply(false, false);

        _moistureTexture = tex;
        _material.SetTexture("_MoistureTex", tex);
        Debug.Log($"云层：已载入湿度场 {width}×{height}");
    }

    /// <summary>清除湿度场（切到没有气候数据的地图时调用）。</summary>
    public void ClearMoistureField()
    {
        if (_moistureTexture != null)
        {
            Destroy(_moistureTexture);
            _moistureTexture = null;
        }

        if (_material != null)
        {
            // 退回均匀灰：没有湿度数据时云量处处相同
            _material.SetTexture("_MoistureTex", Texture2D.grayTexture);
        }
    }

    /// <summary>
    /// 设置水汽加成场（河网 / 湖泊 / 三角洲，0-1）。
    /// 水体是重要的水汽来源，其上空与下风方向云更密。
    /// </summary>
    public void SetWaterField(byte[] field, int width, int height)
    {
        if (field == null || width <= 0 || height <= 0 || field.Length < width * height)
            return;

        EnsureSphere();
        if (_material == null)
            return;

        if (_waterTexture != null)
            Destroy(_waterTexture);

        var tex = new Texture2D(width, height, TextureFormat.R8, false, true);
        tex.name = "CloudWater";
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Bilinear;
        tex.SetPixelData(field, 0);
        tex.Apply(false, false);

        _waterTexture = tex;
        _material.SetTexture("_WaterTex", tex);
    }

    /// <summary>设置季节进度（0-1，0 = 春分）。云带随季节南北移动。</summary>
    public void SetSeasonProgress(float normalizedYear)
    {
        // 夏至（0.25）云带北移、冬至（0.75）南移：用 sin 给出 ±0.35 的纬度位移
        float phase = (normalizedYear - 0.25f) * Mathf.PI * 2f;
        _seasonShift = -Mathf.Sin(phase) * 0.35f;
    }

    /// <summary>
    /// 整体显隐云层。
    /// 注意：**必须能隐掉一个还不存在的球壳**（先把球壳建出来再关掉），
    /// 否则「默认关」会失效 —— 只要有人先调用过 SetMoistureField 把球壳建起来，
    /// 它就会一直显示，而 UI 上的开关还是未勾选的状态。
    /// </summary>
    public void SetVisible(bool visible)
    {
        EnsureSphere();

        if (_sphereObject != null)
            _sphereObject.SetActive(visible);
    }

    /// <summary>设置基准云量（0-1）。</summary>
    public void SetCoverage(float value)
    {
        coverage = Mathf.Clamp01(value);
        if (_material != null)
            _material.SetFloat("_Coverage", coverage);
    }

    #endregion

    #region 内部

    private Vector3 GetSunDirection()
    {
        Light light = sunLight != null ? sunLight : RenderSettings.sun;
        if (light == null)
            return Vector3.right;

        // 平行光的 forward 指向「光线传播方向」（从太阳射向星球），
        // 云层需要的是「从星球指向太阳」的方向，因此取反。
        return -light.transform.forward;
    }

    private void EnsureSphere()
    {
        if (_sphereObject != null)
            return;

        if (earthRenderer == null)
            earthRenderer = GetComponentInChildren<Renderer>();

        if (earthRenderer == null)
        {
            Debug.LogWarning("CloudLayer: 没有找到星球的 Renderer，云层无法建立");
            return;
        }

        Shader shader = Shader.Find("Custom/CloudLayerShader");
        if (shader == null)
        {
            Debug.LogError("CloudLayerShader 未找到，云层无法建立");
            return;
        }

        _sphereObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _sphereObject.name = "CloudLayer";
        _sphereObject.transform.SetParent(earthRenderer.transform, false);
        ApplyScale();

        // 云层只用于显示，不要参与射线检测（否则会挡住在地表打标记）
        var collider = _sphereObject.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);

        _material = new Material(shader);
        _material.SetFloat("_Coverage", coverage);
        _material.SetFloat("_MoistureInfluence", moistureInfluence);
        _material.SetFloat("_WaterInfluence", waterInfluence);
        _material.SetFloat("_Sharpness", sharpness);
        _material.SetFloat("_NoiseScale", noiseScale);
        _material.SetFloat("_DetailScale", detailScale);
        _material.SetFloat("_DetailAmount", detailAmount);
        _material.SetFloat("_FlowSpeed", flowSpeed);
        _material.SetFloat("_Opacity", maxOpacity);
        _material.SetColor("_Color", cloudColor);
        _material.SetColor("_ShadowColor", shadowColor);
        _material.SetFloat("_SilverLining", silverLining);
        _material.SetTexture("_MoistureTex", _moistureTexture != null
            ? (Texture)_moistureTexture : Texture2D.grayTexture);
        _material.SetTexture("_WaterTex", _waterTexture != null
            ? (Texture)_waterTexture : Texture2D.blackTexture);

        _sphereRenderer = _sphereObject.GetComponent<Renderer>();
        _sphereRenderer.material = _material;
        _sphereRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _sphereRenderer.receiveShadows = false;

        Debug.Log("云层：已建立云层球壳");
    }

    /// <summary>
    /// 把云层球壳缩放到「星球世界半径 × (1 + thickness)」。
    /// 星球半径取自**网格包围盒 × 世界缩放**（见 <see cref="GetEarthWorldRadius"/>），
    /// 而不是照抄 localScale；再按父级等比缩放换算成云层的本地缩放。
    /// </summary>
    private void ApplyScale()
    {
        if (_sphereObject == null || earthRenderer == null)
            return;

        _sphereObject.transform.localPosition = Vector3.zero;
        _sphereObject.transform.localRotation = Quaternion.identity;

        // 用**星球自身网格**的世界半径定尺寸，而不是照抄 localScale：
        // 星球网格的顶点半径不一定是 0.5，地形位移、非等比例缩放、
        // 甚至 Renderer.bounds 把子物体算进来，都会让 localScale 失去意义。
        float earthWorldRadius = GetEarthWorldRadius();
        float parentScale = GetParentUniformScale();

        // 厚度下限 0.02：地形位移的山峰可外凸到 0.15 倍半径，太薄会被穿破
        float thickness = Mathf.Max(0.02f, shellThickness);
        float desiredWorldRadius = earthWorldRadius * (1f + thickness);

        // 图元球网格半径是 0.5（直径 1），所以「网格半径 1」到「世界半径 R」需要 2R / parentScale。
        float localScale = parentScale > 1e-6f ? 2f * desiredWorldRadius / parentScale : desiredWorldRadius;
        _sphereObject.transform.localScale = new Vector3(localScale, localScale, localScale);

        if (_logScaleOnce)
        {
            _logScaleOnce = false;
            MeshFilter earthFilter = earthRenderer != null
                ? earthRenderer.GetComponent<MeshFilter>() : null;
            float meshRadius = -1f;
            if (earthFilter != null && earthFilter.sharedMesh != null)
            {
                Bounds mb = earthFilter.sharedMesh.bounds;
                meshRadius = Mathf.Max(mb.extents.x, Mathf.Max(mb.extents.y, mb.extents.z));
            }
            Vector3 ls = earthRenderer.transform.lossyScale;
            Vector3 be = earthRenderer.bounds.extents;
            float boundsRadius = Mathf.Max(be.x, Mathf.Max(be.y, be.z));

            Debug.Log($"云层尺寸：星球网格半径 {meshRadius:F4} × 缩放 {ls.x:F3} = 世界半径 {earthWorldRadius:F3}；" +
                      $"云层厚 {thickness:F3} 倍半径 → 云层世界半径 {desiredWorldRadius:F3}" +
                      $"（高出地表 {(desiredWorldRadius - earthWorldRadius):F3}）；" +
                      $"父级缩放 {parentScale:F3} → 云层本地缩放 {localScale:F3}。" +
                      $"（Renderer.bounds 半径 {boundsRadius:F3} 含子物体，仅作对照）");
        }
    }

    /// <summary>
    /// 星球的世界空间半径。
    /// 优先用**星球自身网格**算，避免把挂在星球下面的子物体（标点 / 标签 / 地名文字）
    /// 也算进去 —— <c>Renderer.bounds</c> 是含子物体的，子物体一多就会明显偏小或偏大。
    ///
    /// 只用 <c>Mesh.bounds</c>（本地空间包围盒）而不是 <c>Mesh.vertices</c>：
    /// 导入的模型（如 Sphere.fbx）默认 **没有开 Read/Write**，
    /// 访问顶点会报 "isReadable is false"，而 <c>Mesh.bounds</c> 不需要该设置。
    /// </summary>
    private float GetEarthWorldRadius()
    {
        if (earthRenderer == null)
            return 1f;

        Transform earth = earthRenderer.transform;

        MeshFilter filter = earth.GetComponent<MeshFilter>();
        if (filter != null && filter.sharedMesh != null)
        {
            Mesh mesh = filter.sharedMesh;
            Bounds mb = mesh.bounds;
            float meshRadius = Mathf.Max(mb.extents.x, Mathf.Max(mb.extents.y, mb.extents.z));
            if (meshRadius > 1e-5f)
            {
                Vector3 s = earth.lossyScale;
                float maxScale = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                return meshRadius * maxScale;
            }
        }

        // 兜底：网格包围盒退化时（极少数情况）才用 Renderer.bounds，并接受它含子物体
        Vector3 extents = earthRenderer.bounds.extents;
        float r = Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z));
        return r > 1e-4f ? r : 1f;
    }

    /// <summary>父级的**等比**缩放（若父级被非等比例缩放，取最小分量以免乘出椭圆）。</summary>
    private float GetParentUniformScale()
    {
        Transform parent = _sphereObject != null ? _sphereObject.transform.parent : null;
        if (parent == null)
            return 1f;

        Vector3 s = parent.lossyScale;
        float m = Mathf.Min(Mathf.Abs(s.x), Mathf.Min(Mathf.Abs(s.y), Mathf.Abs(s.z)));
        return m > 1e-6f ? m : 1f;
    }

    /// <summary>Inspector 里改动参数时同步到材质与球壳（运行时也生效）。</summary>
    private void OnValidate()
    {
        shellThickness = Mathf.Clamp(shellThickness, 0.02f, 0.6f);

        if (_material == null)
            return;

        _material.SetFloat("_Coverage", coverage);
        _material.SetFloat("_MoistureInfluence", moistureInfluence);
        _material.SetFloat("_WaterInfluence", waterInfluence);
        _material.SetFloat("_Sharpness", sharpness);
        _material.SetFloat("_NoiseScale", noiseScale);
        _material.SetFloat("_DetailScale", detailScale);
        _material.SetFloat("_DetailAmount", detailAmount);
        _material.SetFloat("_FlowSpeed", flowSpeed);
        _material.SetFloat("_Opacity", maxOpacity);
        _material.SetColor("_Color", cloudColor);
        _material.SetColor("_ShadowColor", shadowColor);
        _material.SetFloat("_SilverLining", silverLining);

        if (Application.isPlaying && _sphereObject != null)
            ApplyScale();
    }

    private void OnDestroy()
    {
        if (_moistureTexture != null)
            Destroy(_moistureTexture);
        if (_waterTexture != null)
            Destroy(_waterTexture);
        if (_material != null)
            Destroy(_material);
    }

    #endregion
}
