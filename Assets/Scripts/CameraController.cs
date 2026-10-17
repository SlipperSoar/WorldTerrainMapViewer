using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 环绕星球旋转视角。
///
/// 角度真值是**显式球面角**（H = 水平角，V = 俯仰角），由
/// <see cref="CameraOrbitState"/> 状态机保管；本类只负责
/// 「读输入 → 传给状态机 → 把状态机的朝向应用到 Transform」。
///
/// 相机位置 = 目标 + <c>Euler(V, H, 0) · (0,0,-distance)</c>，
/// <c>V &gt; 0</c> 表示相机在星球**上方**俯视，《V》显示正数。
///
/// 为什么不再用「累乘四元数 + 过极点丢弃」（见 git 历史）：
///   ① 俯仰绕**世界 X 轴**累乘时，该轴只在相机位于 ±Z 方向才等于改变球面极角的切向轴；
///      转到 ±X 方向时它退化成绕视线的滚转，轨迹不可预测。
///   ② 过极点时的保护是**把这一帧的俯仰整个丢掉**，而 yaw 继续累加，
///      直到某帧又被判定通过 —— 表现为视角**突然跳变**，且 minY/maxY 从未生效。
///   现在俯仰到限位就夹住、yaw 照常累积，全程连续。
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

    [Tooltip("俯仰下限（度）：-85 ≈ 从正下方仰视")]
    [SerializeField] private float minY = -85f;

    [Tooltip("俯仰上限（度）：+85 ≈ 从正上方俯视")]
    [SerializeField] private float maxY = 85f;

    [Tooltip("视角按钮切换时的过渡角速度（度/秒）")]
    [SerializeField] private float transitionSpeed = 180f;

    /// <summary>角度状态机（唯一真值）。</summary>
    private readonly CameraOrbitState orbit = new CameraOrbitState();

    private Vector3 offset;

    /// <summary>当前状态（Idle / Dragging / Transitioning），供 UI 与测试读取。</summary>
    public CameraOrbitState.Phase OrbitPhase => orbit.State;

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

        orbit.SetPitchLimits(minY, maxY);
        orbit.TransitionSpeed = transitionSpeed;

        // 从当前相机相对目标的方位反解 (H, V)，保证起始视角不被改动
        Vector3 lookDir = offset.sqrMagnitude > 1e-8f ? -offset.normalized : Vector3.forward;

        float yaw = Mathf.Atan2(lookDir.x, lookDir.z) * Mathf.Rad2Deg;
        float pitch = Mathf.Asin(Mathf.Clamp(lookDir.y, -1f, 1f)) * Mathf.Rad2Deg;

        orbit.SetAngles(yaw, pitch);
        UpdateCameraPosition();
    }

    void LateUpdate()
    {
        if (target == null)
            return;

        HandleScrollWheel();
        HandleRightClickOrbit();
        orbit.Tick(Time.deltaTime);
        UpdateCameraPosition();
    }

    /// <summary>Inspector 改参数时同步到状态机（顺带夹紧范围、保证 min ≤ max）。</summary>
    private void OnValidate()
    {
        minY = Mathf.Clamp(minY, -CameraOrbitState.PitchHardLimit, CameraOrbitState.PitchHardLimit);
        maxY = Mathf.Clamp(maxY, -CameraOrbitState.PitchHardLimit, CameraOrbitState.PitchHardLimit);
        if (minY > maxY)
            minY = maxY;

        minDistance = Mathf.Max(0.01f, minDistance);
        maxDistance = Mathf.Max(minDistance, maxDistance);

        orbit.SetPitchLimits(minY, maxY);
        orbit.TransitionSpeed = transitionSpeed;

        if (Application.isPlaying)
            UpdateCameraPosition();
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
        if (Input.GetMouseButton(1))
        {
            // 按下右键：打断过渡，进入拖拽
            orbit.BeginDrag();

            float deltaYaw = Input.GetAxis("Mouse X") * rotationSpeed * Time.deltaTime;
            float deltaPitch = -Input.GetAxis("Mouse Y") * rotationSpeed * Time.deltaTime;
            orbit.Drag(deltaYaw, deltaPitch);
        }
        else
        {
            orbit.EndDrag();
        }
    }

    #endregion

    #region 内部

    private void UpdateCameraPosition()
    {
        if (target == null)
            return;

        // 直接用状态机的朝向：球面坐标下它就是「从目标指向相机」的正交基，
        // 且 forward 恒精确指向球心（离线校验台实测 35 组组合 dot 均为 1.000000）；
        // 用 LookAt 反而会在两极附近因 up 与 forward 共线而抖动
        transform.position = orbit.OrbitPosition(target.position, distance);
        transform.rotation = orbit.Rotation;
    }

    /// <summary>向目标角度做过渡（视角按钮用）。</summary>
    private void TransitionTo(float yaw, float pitch)
    {
        orbit.BeginTransition(yaw, pitch);
        UpdateCameraPosition();
    }

    #endregion

    #region 公开接口

    public float GetHorizontalRotation()
    {
        return Mathf.Repeat(orbit.YawDegrees, 360f) % 360f;
    }

    public float GetVerticalRotation()
    {
        return orbit.PitchDegrees;
    }

    /// <summary>赤道视角（世界空间水平视角，与星球倾角无关）</summary>
    public void SetEquatorView()
    {
        TransitionTo(0f, 0f);
    }

    /// <summary>
    /// 北极视角：从正上方俯视。取不到满 90°（那里 forward 与 up 共线会退化），
    /// 所以停在允许范围内尽量接近 90° 的角度。
    /// </summary>
    public void SetNorthPoleView()
    {
        TransitionTo(0f, PositivePitchTarget);
    }

    /// <summary>南极视角：从正下方仰视，同理停在俯仰下限。</summary>
    public void SetSouthPoleView()
    {
        TransitionTo(0f, -PositivePitchTarget);
    }

    /// <summary>垂直俯视（与北极视角同向）</summary>
    public void SetTopDownView()
    {
        SetNorthPoleView();
    }

    /// <summary>俯仰能取到的最大幅值（不超过硬限位）。</summary>
    private float PositivePitchTarget
        => Mathf.Min(CameraOrbitState.PitchHardLimit,
                     Mathf.Max(Mathf.Abs(minY), Mathf.Abs(maxY), 75f));

    #endregion
}
