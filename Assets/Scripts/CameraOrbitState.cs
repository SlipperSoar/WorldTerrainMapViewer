using System;
using UnityEngine;

/// <summary>
/// 环绕相机的**显式状态机**。
///
/// 为什么要状态机：旧实现把「当前角度、拖拽中的增量、是否被限位夹住」全揉在
/// 一个累乘四元数里，边界情况（到极点、拖拽中途点按钮、过渡动画被打断）的耦合是隐性的 ——
/// 出问题时只能靠复现去猜。改成显式状态后，每条转换都有名字、都能单独断言。
///
/// 状态与转换：
///
///   Idle ──开始拖拽──▶ Dragging
///   Dragging ──松开──▶ Idle
///   Dragging ──点视角按钮──▶ Transitioning（拖拽被取消）
///   Transitioning ──动画完成──▶ Idle
///   Transitioning ──开始拖拽──▶ Dragging（动画被取消，从当前角度接着拖）
///   any ──外部 SetAngles──▶ Idle（立即跳转）
///
/// 两条不变量（由本类保证，测试可断言）：
///   1. 唯一真值是 <see cref="YawDegrees"/> / <see cref="PitchDegrees"/> 两个标量，
///      朝向永远由它们重建 —— 不做四元数累乘
///   2. Pitch 永远夹在 [<see cref="MinPitch"/>, <see cref="MaxPitch"/>] 内，
///      到限位是**夹紧**而不是丢弃当帧输入
/// </summary>
public sealed class CameraOrbitState
{
    /// <summary>相机状态。</summary>
    public enum Phase
    {
        /// <summary>静止：没有拖拽、没有过渡。</summary>
        Idle,

        /// <summary>右键拖拽中：跟随鼠标增量改角度，受限位夹紧。</summary>
        Dragging,

        /// <summary>视角过渡动画中：向目标角度插值。</summary>
        Transitioning
    }

    /// <summary>俯仰角的绝对硬限位。取不到 ±90：那里 forward 与 up 共线会退化。</summary>
    public const float PitchHardLimit = 89.5f;

    /// <summary>过渡动画的角速度（度/秒）。</summary>
    public float TransitionSpeed { get; set; } = 180f;

    /// <summary>渲染用：当前朝向。</summary>
    public Quaternion Rotation => Quaternion.Euler(PitchDegrees, YawDegrees, 0f);

    /// <summary>当前状态。</summary>
    public Phase State { get; private set; } = Phase.Idle;

    /// <summary>水平角 H（度）。</summary>
    public float YawDegrees { get; private set; }

    /// <summary>俯仰角 V（度）：正 = 相机在星球上方俯视。</summary>
    public float PitchDegrees { get; private set; }

    /// <summary>俯仰下限（度）。</summary>
    public float MinPitch { get; private set; } = -85f;

    /// <summary>俯仰上限（度）。</summary>
    public float MaxPitch { get; private set; } = 85f;

    /// <summary>是否正在拖拽。</summary>
    public bool IsDragging => State == Phase.Dragging;

    /// <summary>是否正在做过渡动画。</summary>
    public bool IsTransitioning => State == Phase.Transitioning;

    // 过渡目标
    private float targetYaw;
    private float targetPitch;

    // ── 配置 ──

    /// <summary>设置俯仰上下限（会夹紧当前值，必要时打断过渡）。</summary>
    public void SetPitchLimits(float min, float max)
    {
        min = Clamp(min, -PitchHardLimit, PitchHardLimit);
        max = Clamp(max, -PitchHardLimit, PitchHardLimit);
        if (min > max)
            min = max;

        MinPitch = min;
        MaxPitch = max;

        // 限位收窄后当前角度可能越界，就地夹回来
        PitchDegrees = Clamp(PitchDegrees, MinPitch, MaxPitch);

        if (State == Phase.Transitioning)
            targetPitch = Clamp(targetPitch, MinPitch, MaxPitch);
    }

    /// <summary>直接设定角度（外部跳转，例如初始化）。会立即进入 Idle。</summary>
    public void SetAngles(float yaw, float pitch)
    {
        YawDegrees = yaw;
        PitchDegrees = Clamp(pitch, MinPitch, MaxPitch);
        targetYaw = YawDegrees;
        targetPitch = PitchDegrees;
        State = Phase.Idle;
    }

    // ── 状态转换 ──

    /// <summary>开始拖拽。若正在过渡，则取消过渡、从当前角度接着拖。</summary>
    public void BeginDrag()
    {
        if (State == Phase.Dragging)
            return;

        State = Phase.Dragging;
    }

    /// <summary>
    /// 拖拽增量。<paramref name="deltaYaw"/>/<paramref name="deltaPitch"/> 单位是度。
    /// 只在 <see cref="Phase.Dragging"/> 下生效；到限位时夹紧（不丢输入）。
    /// </summary>
    public void Drag(float deltaYaw, float deltaPitch)
    {
        if (State != Phase.Dragging)
            return;

        YawDegrees += deltaYaw;
        PitchDegrees = Clamp(PitchDegrees + deltaPitch, MinPitch, MaxPitch);
    }

    /// <summary>结束拖拽，回到 Idle。</summary>
    public void EndDrag()
    {
        if (State == Phase.Dragging)
            State = Phase.Idle;
    }

    /// <summary>开始向目标角度做过渡动画（取消拖拽）。</summary>
    public void BeginTransition(float yaw, float pitch)
    {
        targetYaw = yaw;
        targetPitch = Clamp(pitch, MinPitch, MaxPitch);
        State = Phase.Transitioning;
    }

    /// <summary>取消过渡，停在当前角度。</summary>
    public void CancelTransition()
    {
        if (State == Phase.Transitioning)
            State = Phase.Idle;
    }

    // ── 每帧推进 ──

    /// <summary>
    /// 推进状态机。返回 true 表示这一帧角度/朝向有变化（调用方需要更新相机）。
    /// <paramref name="deltaTime"/> 为秒。
    /// </summary>
    public bool Tick(float deltaTime)
    {
        if (State != Phase.Transitioning)
            return false;

        float step = Mathf.Max(0f, TransitionSpeed) * Mathf.Max(0f, deltaTime);

        float yawDelta = Mathf.DeltaAngle(YawDegrees, targetYaw);
        float pitchDelta = targetPitch - PitchDegrees;
        float maxDelta = Mathf.Max(Mathf.Abs(yawDelta), Mathf.Abs(pitchDelta));

        if (maxDelta <= 1e-3f)
        {
            // 足够接近：吸附到目标并结束过渡
            YawDegrees = targetYaw;
            PitchDegrees = targetPitch;
            State = Phase.Idle;
            return true;
        }

        if (step <= 0f || step >= maxDelta)
        {
            YawDegrees = targetYaw;
            PitchDegrees = targetPitch;
            State = Phase.Idle;
            return true;
        }

        float t = step / maxDelta;
        YawDegrees = Mathf.Repeat(YawDegrees + yawDelta * t, 360f);
        PitchDegrees = Clamp(PitchDegrees + pitchDelta * t, MinPitch, MaxPitch);
        return true;
    }

    /// <summary>
    /// 原地重建的球面相机位置：目标 + Rotation * (0,0,-distance)。
    /// forward 恒精确指向球心（离线校验台已量化验证）。
    /// </summary>
    public Vector3 OrbitPosition(Vector3 targetPosition, float distance)
    {
        return targetPosition + Rotation * new Vector3(0f, 0f, -distance);
    }

    public override string ToString()
        => $"{State} H={YawDegrees:F1}° V={PitchDegrees:F1}°" +
           (State == Phase.Transitioning ? $"（目标 {targetYaw:F1}° / {targetPitch:F1}°）" : "");

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
}
