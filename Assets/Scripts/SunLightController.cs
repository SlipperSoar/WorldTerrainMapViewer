using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using WorldTerrain;

public class SunLightController : MonoBehaviour
{
    #region Properties

    [Header("Day/Night Settings")] [SerializeField]
    private float dayDuration = 240f;

    [SerializeField] private float currentTimeOfDay = 0f;
    [SerializeField] private bool autoCycle = true;
    [SerializeField, Range(0.1f, 10f)] private float speedMultiplier = 1f;

    [Header("Season Settings")] [SerializeField]
    private float yearDuration = 86400f;

    [SerializeField] private float currentDayOfYear = 0f;
    [SerializeField, Range(0f, 90f), Tooltip("地轴倾角（度）。这是地图属性：载入地图时由地图元数据覆写")]
    private float axialTilt = 23.5f;
    [SerializeField] private bool enableSeasons = false;

    [Header("References")] [SerializeField]
    private Transform earthTransform;

    [SerializeField] private Light sunLight;

    [Header("Light Settings")] [SerializeField]
    private Color lightColor = Color.white;

    [SerializeField, Range(0f, 2f)] private float lightIntensity = 1f;

    [Header("Initialization")] [SerializeField]
    private bool applyAxialTiltOnInit = true;

    /// <summary>
    /// 自转方向：+1 顺向（地球式，太阳自东向西）、−1 逆向。
    /// 只影响时间流逝时太阳的绕行方向，不影响时间本身的速度。
    /// </summary>
    [SerializeField] private int rotationDirection = 1;

    private float dayProgress = 0f;
    private float yearProgress = 0f;

    /// <summary>
    /// 未施加地轴倾角时地球的朝向。地轴倾角始终以它为基准重新计算，
    /// 因此可以在地图载入/滑动条拖动时反复改变倾角而不会累积旋转。
    /// </summary>
    private Quaternion baseEarthRotation;
    private bool baseEarthRotationCaptured = false;

    /// <summary>
    /// 地轴倾角（度）——地图属性
    /// </summary>
    public float AxialTilt => axialTilt;

    /// <summary>
    /// 一天的持续时间（秒）
    /// </summary>
    public float DayDuration => dayDuration;

    /// <summary>
    /// 一年的持续时间（秒）
    /// </summary>
    public float YearDuration => yearDuration;

    /// <summary>
    /// 是否启用自动循环
    /// </summary>
    public bool IsAutoCycleEnabled => autoCycle;

    /// <summary>
    /// 时间流速倍率
    /// </summary>
    public float SpeedMultiplier => speedMultiplier;

    public void SetSpeedMultiplier(float speed)
    {
        speedMultiplier = Mathf.Clamp(speed, 0.1f, 10f);
    }

    #endregion

    void Awake()
    {
        // 在任何人施加倾角之前记录地球的基准朝向（地轴倾角是地图属性，
        // 运行时会被地图元数据反复修改）
        CaptureBaseEarthRotation();
    }

    void Start()
    {
        if (sunLight == null)
        {
            sunLight = GetComponent<Light>();

            if (sunLight == null)
            {
                Debug.LogWarning("SunLightController: No Light component found!");
            }
        }

        InitializeEarthAxialTilt();
        UpdateSunPosition();
        InitializeLightSettings();
    }

    void Update()
    {
        if (autoCycle)
        {
            currentTimeOfDay += Time.deltaTime * speedMultiplier;
            currentTimeOfDay %= dayDuration;

            if (enableSeasons)
            {
                currentDayOfYear += Time.deltaTime * speedMultiplier;
                currentDayOfYear %= yearDuration;
            }
        }

        dayProgress = currentTimeOfDay / dayDuration;
        yearProgress = currentDayOfYear / yearDuration;

        UpdateSunPosition();
    }

    private void InitializeLightSettings()
    {
        if (sunLight == null)
            return;

        sunLight.color = lightColor;
        sunLight.intensity = lightIntensity;
    }

    private void InitializeEarthAxialTilt()
    {
        if (earthTransform == null)
        {
            Debug.Log("Skipping Earth axial tilt initialization (no earth transform)");
            return;
        }

        CaptureBaseEarthRotation();

        if (!applyAxialTiltOnInit)
        {
            Debug.Log("Skipping Earth axial tilt initialization (disabled)");
            return;
        }

        ApplyAxialTiltToEarth();

        Debug.Log($"Applied Earth axial tilt: {axialTilt} degrees");
    }

    /// <summary>
    /// 记录未施加倾角时地球的朝向，必须在任何倾角施加之前调用。
    /// </summary>
    private void CaptureBaseEarthRotation()
    {
        if (baseEarthRotationCaptured || earthTransform == null)
            return;

        baseEarthRotation = earthTransform.rotation;
        baseEarthRotationCaptured = true;
    }

    /// <summary>
    /// 以基准朝向重新计算地球朝向，避免反复设置倾角时累积旋转。
    /// </summary>
    private void ApplyAxialTiltToEarth()
    {
        if (earthTransform == null)
            return;

        CaptureBaseEarthRotation();
        earthTransform.rotation = baseEarthRotation * Quaternion.Euler(axialTilt, 0f, 0f);
    }

    /// <summary>
    /// 设置地轴倾角（度，0-90）。地轴倾角是地图属性，载入地图时由地图元数据
    /// 调用；设置后地球朝向与阳光方向会立即更新。
    /// </summary>
    public void SetAxialTilt(float degrees)
    {
        axialTilt = Mathf.Clamp(degrees, 0f, 90f);
        ApplyAxialTiltToEarth();
        UpdateSunPosition();
    }

    private void UpdateSunPosition()
    {
        if (earthTransform == null)
        {
            UpdateSunPositionWithoutEarth();
            return;
        }

        // 自转方向：顺向 +1、逆向 −1。时间本身仍单调递增，变的是太阳绕行的方向 ——
        // 逆向自转的星球上太阳自西向东升，与地球相反。
        float dayAngle = dayProgress * 360f * rotationDirection;

        Vector3 sunDirection = Quaternion.Euler(0, dayAngle, 0) * Vector3.forward;

        if (enableSeasons)
        {
            sunDirection = Quaternion.Euler(SeasonPitchDegrees(yearProgress), 0, 0) * sunDirection;
        }

        transform.rotation = Quaternion.LookRotation(-sunDirection);
    }

    private void UpdateSunPositionWithoutEarth()
    {
        float dayAngle = dayProgress * 360f * rotationDirection - 90f;

        float seasonOffset = 0f;
        if (enableSeasons)
        {
            seasonOffset = SeasonPitchDegrees(yearProgress);
        }

        transform.rotation = Quaternion.Euler(dayAngle, seasonOffset, 0);
    }

    /// <summary>
    /// 季节摆角（度）：阳光方向绕 X 轴的俯仰。
    ///
    /// 太阳赤纬 δ 由地轴倾角与太阳黄经决定（春分为 λ=0，夏至 90°）：
    ///     δ(λ) = arcsin(sin ε · sin λ)
    /// 摆角本身要取 **ε − δ**：因为地轴倾角是施加在「地球」上的，
    /// 阳光方向的俯仰必须同时抵消地球那侧的倾斜、再给出目标赤纬。
    /// 设摆角为 θ 时，日下点纬度 = ε − θ，因此 θ = ε − δ 时正好得到 δ。
    ///
    /// 旧实现的 `ε·(1 + sin(2πy))` 有三处错：
    ///   1. 取值恒在 [0, 2ε]，从不取负 —— 南半球永远没有真正的夏季；
    ///   2. 夏至给 2ε、冬至给 0、春分给 ε，季节**整体反相**（北极在「夏至」背对太阳）；
    ///   3. 春分（应 δ=0）拿到 δ=+ε。
    /// 实测日下点纬度与理论赤纬的最大偏差达 **47°**（= 2ε）。
    /// </summary>
    private float SeasonPitchDegrees(float normalizedYear)
    {
        double dayOfYear = ClimateModel.SpringEquinoxDay + normalizedYear * ClimateModel.TropicalYear;
        double declination = ClimateModel.SolarDeclination(axialTilt, dayOfYear);
        return (float)(axialTilt - declination);
    }

    public void SetTimeOfDay(float time)
    {
        currentTimeOfDay = Mathf.Clamp(time, 0, dayDuration);
        dayProgress = currentTimeOfDay / dayDuration;
        UpdateSunPosition();
    }

    /// <summary>是否逆向自转。</summary>
    public bool IsRetrograde => rotationDirection < 0;

    /// <summary>
    /// 设置自转方向：<c>true</c> 为逆向（太阳反向绕行）。
    /// 立即重算光照，不需要等到下一帧。
    /// </summary>
    public void SetRotationDirection(bool retrograde)
    {
        int direction = retrograde ? -1 : 1;
        if (direction == rotationDirection)
            return;

        rotationDirection = direction;
        UpdateSunPosition();
        Debug.Log($"自转方向：{PlanetProfile.RotationLabel(retrograde)}");
    }

    public void SetDayOfYear(float day)
    {
        currentDayOfYear = Mathf.Clamp(day, 0, yearDuration);
        yearProgress = currentDayOfYear / yearDuration;
        UpdateSunPosition();
    }

    public void SetSeason(int seasonIndex)
    {
        float[] seasonDays = new float[]
        {
            yearDuration * 0.0f,
            yearDuration * 0.25f,
            yearDuration * 0.5f,
            yearDuration * 0.75f
        };

        int index = Mathf.Clamp(seasonIndex, 0, 3);
        currentDayOfYear = seasonDays[index];
        yearProgress = currentDayOfYear / yearDuration;
        UpdateSunPosition();
    }

    public void JumpToNoon()
    {
        SetTimeOfDay(dayDuration * 0.25f);
    }

    public void JumpToMidnight()
    {
        SetTimeOfDay(dayDuration * 0.75f);
    }

    public void JumpToSunrise()
    {
        SetTimeOfDay(0f);
    }

    public void JumpToSunset()
    {
        SetTimeOfDay(dayDuration * 0.5f);
    }

    public void SetSpringEquinox()
    {
        SetDayOfYear(yearDuration * 0.0f);
    }

    public void SetSummerSolstice()
    {
        SetDayOfYear(yearDuration * 0.25f);
    }

    public void SetAutumnEquinox()
    {
        SetDayOfYear(yearDuration * 0.5f);
    }

    public void SetWinterSolstice()
    {
        SetDayOfYear(yearDuration * 0.75f);
    }

    public void ToggleAutoCycle(bool enabled)
    {
        autoCycle = enabled;
    }

    public void ToggleSeasons(bool enabled)
    {
        enableSeasons = enabled;
        UpdateSunPosition();
    }

    public void ResetEarthAxialTilt()
    {
        if (earthTransform == null)
            return;

        ApplyAxialTiltToEarth();

        Debug.Log($"Reset Earth axial tilt to: {axialTilt} degrees");
    }

    public void RemoveAxialTilt()
    {
        if (earthTransform == null)
            return;

        CaptureBaseEarthRotation();
        earthTransform.rotation = baseEarthRotation;

        Debug.Log("Removed Earth axial tilt");
    }

    public void SetLightColor(Color color)
    {
        lightColor = color;

        if (sunLight != null)
        {
            sunLight.color = lightColor;
        }
    }

    public void SetLightIntensity(float intensity)
    {
        lightIntensity = Mathf.Clamp(intensity, 0f, 2f);

        if (sunLight != null)
        {
            sunLight.intensity = lightIntensity;
        }
    }

    public float GetNormalizedTimeOfDay()
    {
        return dayProgress;
    }

    public float GetNormalizedDayOfYear()
    {
        return yearProgress;
    }

    public string GetCurrentSeasonName()
    {
        if (!enableSeasons)
            return "N/A";

        float normalizedYear = yearProgress;

        if (normalizedYear < 0.25f)
            return "Spring";
        else if (normalizedYear < 0.5f)
            return "Summer";
        else if (normalizedYear < 0.75f)
            return "Autumn";
        else
            return "Winter";
    }

    public string GetTimeOfDayName()
    {
        float normalizedTime = dayProgress;

        if (normalizedTime < 0.2f || normalizedTime > 0.8f)
            return "Night";
        else if (normalizedTime < 0.3f)
            return "Sunrise";
        else if (normalizedTime < 0.45f)
            return "Morning";
        else if (normalizedTime < 0.55f)
            return "Noon";
        else if (normalizedTime < 0.7f)
            return "Afternoon";
        else
            return "Sunset";
    }
}