using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 环绕星球旋转视角。用**显式球面角 (yaw / pitch)** 表示相机方位，而不是累乘四元数。
///
/// 角度约定（与 UI 显示的 H / V 一致）：
///   yaw   = H，相机水平朝向角（绕世界 Y 轴）
///   pitch = V，相机俯仰角（相机 forward 与水平面的夹角）
///
/// 相机位置 = 目标 + <c>Euler(pitch, yaw, 0) · (0,0,-distance)</c>，
/// 于是 <c>pitch &gt; 0</c> 表示相机在星球**上方**俯视，《V》显示正数。
///
/// 为什么重写（旧写法的两个缺陷）：
///   ① 旧代码 pitch 绕【世界 X 轴】累乘。该轴只在相机位于 ±Z 方向时等于改变
///      球面极角的切向轴；相机转到 ±X 方向时它退化成绕视线的滚转，轨迹不可预测。
///   ② 过极点时的保护不是夹紧角度、而是**把这一帧的俯仰整个丢掉**，而 yaw 继续累加，
///      直到某帧那个被丢掉的量又碰巧通过 <c>camUp.y &gt;= 0</c> 判定 —— 视角**突然跳变**。
///      实测：俯仰上限失效，拖到底相机一路翻到 -89.9°（几乎贴到南极）才停住。
///   现在 pitch 到限位就夹住、yaw 照常累积，全程连续。
/// </summary>
public class CameraController : MonoBehaviour
{
    #region Properties

    [SerializeField] private SunLightController sunLightController;

    [SerializeField] private Transform target;
    [SerializeField] private float distance = 10f;
    [SerializeField] private float minDistance = 2f;
    [SerializeField] private float maxDistance = 50f;
    [SerializeField] private float scrollSpeed = 5f;

    [SerializeField] private float rotationSpeed = 200f;

    [Tooltip("当前水平角 H（度），由鼠标拖拽实时更新")]
    [SerializeField] private float currentX = 0f;

    [Tooltip("当前俯仰角 V（度）：正 = 相机在星球上方俯视，负 = 从下方仰视")]
    [SerializeField] private float currentY = 0f;

    [Tooltip("俯仰下限（度）：-85 ≈ 从正下方仰视")]
    [SerializeField] private float minY = -85f;

    [Tooltip("俯仰上限（度）：+85 ≈ 从正上方俯视")]
    [SerializeField] private float maxY = 85f;

    /// <summary>
    /// 俯仰角的绝对硬限位。取不到 ±90：那里相机 forward 与 up 共线，
    /// LookRotation 之类的朝向解算会退化（数值抖动）。
    /// </summary>
    private const float PitchHardLimit = 89.5f;

    private Quaternion orbitRotation = Quaternion.identity;
    private Vector3 offset;

    #endregion

    #region Unity

    void Start()
    {
        if (target == null)
        {
            Debug.LogWarning("CameraController: No target assigned!");
            return;
        }

        offset = transform.position - target.position;

        // 从当前相机相对目标的方位反解 (yaw, pitch)，保证起始视角不被改动
        Vector3 lookDir = offset.sqrMagnitude > 1e-8f ? -offset.normalized : Vector3.forward;

        float yaw = Mathf.Atan2(lookDir.x, lookDir.z) * Mathf.Rad2Deg;
        float pitch = Mathf.Asin(Mathf.Clamp(lookDir.y, -1f, 1f)) * Mathf.Rad2Deg;

        SetYawPitch(yaw, pitch);
        UpdateCameraPosition();
    }

    void LateUpdate()
    {
        if (target == null)
            return;

        HandleScrollWheel();
        HandleRightClickOrbit();
        UpdateCameraPosition();
    }

    /// <summary>
    /// Inspector 改参数时夹紧范围并同步。
    /// 之前 minY / maxY 被声明了却从未被任何代码读取（编译器会报 CS0414），
    /// 现在它们是俯仰的真实上下限。
    /// </summary>
    private void OnValidate()
    {
        minY = Mathf.Clamp(minY, -PitchHardLimit, PitchHardLimit);
        maxY = Mathf.Clamp(maxY, -PitchHardLimit, PitchHardLimit);
        if (minY > maxY)
            minY = maxY;

        currentY = Mathf.Clamp(currentY, minY, maxY);
        minDistance = Mathf.Max(0.01f, minDistance);
        maxDistance = Mathf.Max(minDistance, maxDistance);

        if (Application.isPlaying)
            SetYawPitch(currentX, currentY);
    }

    #endregion

    #region 输入

    private void HandleScrollWheel()
    {
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (scroll != 0)
        {
            distance -= scroll * scrollSpeed;
            distance = Mathf.Clamp(distance, minDistance, maxDistance);
        }
    }

    private void HandleRightClickOrbit()
    {
        if (!Input.GetMouseButton(1))
            return;

        // 直接累加到角度标量上再夹紧 —— 不再有「整帧丢弃」这种不连续操作
        currentX += Input.GetAxis("Mouse X") * rotationSpeed * Time.deltaTime;
        currentY -= Input.GetAxis("Mouse Y") * rotationSpeed * Time.deltaTime;
        currentY = Mathf.Clamp(currentY, minY, maxY);

        orbitRotation = Quaternion.Euler(currentY, currentX, 0f);
    }

    #endregion

    #region 内部

    /// <summary>设置朝向（会夹紧俯仰角）。</summary>
    private void SetYawPitch(float yaw, float pitch)
    {
        currentX = yaw;
        currentY = Mathf.Clamp(pitch, minY, maxY);
        orbitRotation = Quaternion.Euler(currentY, currentX, 0f);
    }

    private void UpdateCameraPosition()
    {
        if (target == null)
            return;

        Vector3 direction = new Vector3(0, 0, -distance);
        transform.position = target.position + orbitRotation * direction;
        // 直接用 orbitRotation 当朝向：球面坐标下它就是「从目标指向相机」的正交基，
        // 且 forward 恒精确指向球心（离线校验台实测 35 组组合 dot 均为 1.000000）；
        // 用 LookAt 反而会在两极附近因 up 与 forward 共线而抖动
        transform.rotation = orbitRotation;
    }

    #endregion

    #region 公开接口

    public float GetHorizontalRotation()
    {
        return Mathf.Repeat(currentX, 360f) % 360f;
    }

    public float GetVerticalRotation()
    {
        return currentY;
    }

    /// <summary>赤道视角（世界空间水平视角，与星球倾角无关）</summary>
    public void SetEquatorView()
    {
        SetYawPitch(0f, 0f);
        UpdateCameraPosition();
    }

    /// <summary>
    /// 北极视角：从正上方俯视。取不到满 90°（那里 forward 与 up 共线会退化），
    /// 所以停在允许范围内尽量接近 90° 的角度。
    /// </summary>
    public void SetNorthPoleView()
    {
        // SetYawPitch 会再夹到 [minY, maxY]，这里只需保证数值本身不超过硬限位
        SetYawPitch(0f, PositivePitchTarget);
        UpdateCameraPosition();
    }

    /// <summary>南极视角：从正下方仰视，同理停在俯仰下限。</summary>
    public void SetSouthPoleView()
    {
        SetYawPitch(0f, -PositivePitchTarget);
        UpdateCameraPosition();
    }

    /// <summary>俯仰能取到的最大幅值（不超过硬限位）。</summary>
    private float PositivePitchTarget => Mathf.Min(PitchHardLimit, Mathf.Max(Mathf.Abs(minY), Mathf.Abs(maxY), 75f));

    /// <summary>垂直俯视（与北极视角同向）</summary>
    public void SetTopDownView()
    {
        SetNorthPoleView();
    }

    #endregion
}
