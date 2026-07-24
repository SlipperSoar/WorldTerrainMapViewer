using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SunLightController : MonoBehaviour
{
    #region Properties

    [Header("Day/Night Settings")] [SerializeField]
    private float dayDuration = 240f;

    [SerializeField] private float currentTimeOfDay = 0f;
    [SerializeField] private bool autoCycle = true;

    [Header("Season Settings")] [SerializeField]
    private float yearDuration = 86400f;

    [SerializeField] private float currentDayOfYear = 0f;
    [SerializeField] private float axialTilt = 23.5f;
    [SerializeField] private bool enableSeasons = false;

    [Header("References")] [SerializeField]
    private Transform earthTransform;

    [SerializeField] private Light sunLight;

    [Header("Light Settings")] [SerializeField]
    private Color lightColor = Color.white;

    [SerializeField, Range(0f, 2f)] private float lightIntensity = 1f;

    [Header("Initialization")] [SerializeField]
    private bool applyAxialTiltOnInit = true;

    private float dayProgress = 0f;
    private float yearProgress = 0f;
    private Quaternion initialEarthRotation;

    /// <summary>
    /// 地轴倾角
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

    #endregion

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
            currentTimeOfDay += Time.deltaTime;
            currentTimeOfDay %= dayDuration;

            if (enableSeasons)
            {
                currentDayOfYear += Time.deltaTime;
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
        if (earthTransform == null || !applyAxialTiltOnInit)
        {
            Debug.Log("Skipping Earth axial tilt initialization");
            return;
        }

        initialEarthRotation = earthTransform.rotation;

        earthTransform.Rotate(Vector3.right, axialTilt, Space.Self);

        Debug.Log($"Applied Earth axial tilt: {axialTilt} degrees");
    }

    private void UpdateSunPosition()
    {
        if (earthTransform == null)
        {
            UpdateSunPositionWithoutEarth();
            return;
        }

        float dayAngle = dayProgress * 360f;

        Vector3 sunDirection = Quaternion.Euler(0, dayAngle, 0) * Vector3.forward;

        if (enableSeasons)
        {
            float seasonOffset = axialTilt * (1f + Mathf.Sin(yearProgress * Mathf.PI * 2f));
            sunDirection = Quaternion.Euler(seasonOffset, 0, 0) * sunDirection;
        }

        transform.rotation = Quaternion.LookRotation(-sunDirection);
    }

    private void UpdateSunPositionWithoutEarth()
    {
        float dayAngle = dayProgress * 360f - 90f;

        float seasonOffset = 0f;
        if (enableSeasons)
        {
            seasonOffset = axialTilt * (1f + Mathf.Sin(yearProgress * Mathf.PI * 2f));
        }

        transform.rotation = Quaternion.Euler(dayAngle, seasonOffset, 0);
    }

    public void SetTimeOfDay(float time)
    {
        currentTimeOfDay = Mathf.Clamp(time, 0, dayDuration);
        dayProgress = currentTimeOfDay / dayDuration;
        UpdateSunPosition();
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

        earthTransform.rotation = initialEarthRotation;

        earthTransform.Rotate(Vector3.right, axialTilt, Space.Self);

        Debug.Log($"Reset Earth axial tilt to: {axialTilt} degrees");
    }

    public void RemoveAxialTilt()
    {
        if (earthTransform == null)
            return;

        earthTransform.rotation = initialEarthRotation;

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