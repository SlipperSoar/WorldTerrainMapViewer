using System;

namespace WorldTerrain
{
    /// <summary>
    /// 行星气候模型：由纬度与地图属性「地轴倾角」推算终年积雪（雪线与冰盖）范围。
    ///
    /// 采用半经验公式（把最热月平均气温 0℃ 的等温线高度近似为雪线）：
    ///     T_s(λ,ε) = 28 - 0.0055·λ² + 1.0·(ε-23.44)·(λ/90)²      [℃]  海平面最热月平均气温
    ///     H_m      = max(T_s, 0) / 5.5                            [km] 雪线中值
    ///     H_min    = max(0, H_m - (0.7 + 0.012·λ))                [km] 湿润山地（海洋性）
    ///     H_max    = H_m + (1.0 + 0.022·λ)                        [km] 干旱内陆山地
    /// 其中 λ=|纬度|，ε=地轴倾角。湿润处取靠近 H_min，干旱处取靠近 H_max。
    ///
    /// 公式给的是真实海拔（赤道约 5 km）。生成地图的地形起伏远小于地球
    /// （地球陆地最高 8848 m，本工具生成的地图通常只有 1~2 km），因此雪线按
    /// 「地图最高陆地海拔 / 地球最高陆地海拔」等比标定后使用，使公式中的纬度、
    /// 倾角与干湿差异都能在同一比例下体现（等价于把雪线表示为行星起伏的比例）。
    ///
    /// 冰盖界限与海冰界限同样由 T_s 推得：T_s ≤ 0 处陆地海平面即终年积雪，
    /// T_s ≤ <see cref="SeaIceTemperature"/> 处海面常年结冰。
    ///
    /// 仅使用 System.Math，可在后台线程安全调用。
    /// </summary>
    public static class PolarClimate
    {
        // ── 半经验公式常数 ──

        /// <summary>公式基准地轴倾角（地球，度）。</summary>
        public const double ReferenceTilt = 23.44;

        /// <summary>海平面最热月平均气温的常数项（℃）。</summary>
        public const double SeaLevelSummerBase = 28.0;

        /// <summary>纬度平方项系数（℃/度²）。</summary>
        public const double LatitudeCooling = 0.0055;

        /// <summary>地轴倾角项系数（℃/度）：倾角越大，高纬夏季越暖，雪线越高。</summary>
        public const double ObliquityWarming = 1.0;

        /// <summary>气温直减率（℃/km）。</summary>
        public const double LapseRate = 5.5;

        /// <summary>干湿区间：湿润侧宽度 Δ- = 0.7 + 0.012·λ (km)。</summary>
        public const double WetRangeWidth = 0.7;
        public const double WetRangeSlope = 0.012;

        /// <summary>干湿区间：干旱侧宽度 Δ+ = 1.0 + 0.022·λ (km)。</summary>
        public const double DryRangeWidth = 1.0;
        public const double DryRangeSlope = 0.022;

        /// <summary>地球陆地最高海拔（m），用于把绝对雪线标定到地图起伏。</summary>
        public const double EarthMaxLandElevation = 8848.0;

        /// <summary>地图起伏过小时的参考海拔下限（m）。</summary>
        public const double MinimumReferenceElevation = 1200.0;

        /// <summary>海冰：海面常年结冰的气温阈值（℃），比陆地冰盖更冷。</summary>
        public const double SeaIceTemperature = -2.0;

        /// <summary>雪线附近的斑状积雪过渡带（相对参考海拔的比例）。</summary>
        public const double SnowLineTransitionRatio = 0.08;

        /// <summary>雪线抖动幅度（相对参考海拔的比例），让冰缘不规则。</summary>
        public const double SnowLineNoiseRatio = 0.08;

        /// <summary>纬度方向的冰缘过渡带宽度（度）。</summary>
        private const double LatitudeTransition = 3.5;

        // ── 气温与雪线（公式原值，真实海拔） ──

        /// <summary>
        /// 海平面最热月平均气温（℃）。λ 为纬度绝对值（度），ε 为地轴倾角（度）。
        /// </summary>
        public static double SeaLevelSummerTemperature(double absLat, double axialTilt)
        {
            double lat = Clamp(absLat, 0.0, 90.0);
            double tilt = Clamp(axialTilt, 0.0, 90.0);
            double t = lat / 90.0;
            return SeaLevelSummerBase
                 - LatitudeCooling * lat * lat
                 + ObliquityWarming * (tilt - ReferenceTilt) * t * t;
        }

        /// <summary>
        /// 把海平面气温按海拔递减到给定高度（℃，最热月平均）。
        /// 递减率见 <see cref="LapseRate"/>；雪线就是该值降到 0℃ 的高度。
        /// </summary>
        public static double TemperatureAtElevation(double seaLevelTemperature, double elevationMeters)
        {
            if (elevationMeters <= 0.0)
                return seaLevelTemperature;
            return seaLevelTemperature - elevationMeters / 1000.0 * LapseRate;
        }

        /// <summary>
        /// 雪线中值（米，公式原值）。返回 0 表示海平面即终年积雪。
        /// </summary>
        public static double SnowLineMedianElevation(double absLat, double axialTilt)
        {
            double t = SeaLevelSummerTemperature(absLat, axialTilt);
            if (t <= 0.0)
                return 0.0;
            return t / LapseRate * 1000.0;
        }

        /// <summary>
        /// 雪线区间（米，公式原值）：[H_min, H_max]。
        /// 气温越低（越靠极地）干湿造成的不确定区间越小，到海平面雪线处收敛为 0。
        /// </summary>
        public static void SnowLineRange(double absLat, double axialTilt,
            out double minMeters, out double maxMeters)
        {
            double lat = Clamp(absLat, 0.0, 90.0);
            double median = SnowLineMedianElevation(lat, axialTilt) / 1000.0;   // km
            if (median <= 0.0)
            {
                minMeters = 0.0;
                maxMeters = 0.0;
                return;
            }

            // 气温接近 0℃ 时雪线必然贴近海平面，干湿差异随之收敛
            double narrowing = Math.Min(1.0, median / 0.5);
            double deltaMinus = (WetRangeWidth + WetRangeSlope * lat) * narrowing;
            double deltaPlus = (DryRangeWidth + DryRangeSlope * lat) * narrowing;

            minMeters = Math.Max(0.0, median - deltaMinus) * 1000.0;
            maxMeters = (median + deltaPlus) * 1000.0;
        }

        // ── 冰盖 / 海冰界限（由同一气温函数推得） ──

        /// <summary>
        /// 海平面最热月平均气温降到 threshold（℃）的纬度绝对值（度）。
        /// 返回 &gt; 90 表示该倾角下任何纬度都达不到这个温度（全星球无海平面冰盖）。
        /// </summary>
        public static double TemperatureLimitLatitude(double axialTilt, double threshold)
        {
            double tilt = Clamp(axialTilt, 0.0, 90.0);
            // 28 - 0.0055λ² + (ε-23.44)(λ/90)² = threshold
            double denom = LatitudeCooling - ObliquityWarming * (tilt - ReferenceTilt) / 8100.0;
            if (denom <= 1e-9)
                return NoIceLatitude;

            double numerator = SeaLevelSummerBase - threshold;
            if (numerator <= 0.0)
                return 0.0;

            return Math.Sqrt(numerator / denom);
        }

        /// <summary>任何纬度都达不到该温度时的返回标记（大于 90，覆盖率恒为 0）。</summary>
        public const double NoIceLatitude = 180.0;

        /// <summary>
        /// 陆地永久冰盖（海平面即终年积雪）的纬度界限：最热月平均气温 &lt; 0℃ 处。
        /// </summary>
        public static double IceLimitLatitude(double axialTilt)
        {
            return TemperatureLimitLatitude(axialTilt, 0.0);
        }

        /// <summary>
        /// 海冰（海面常年冻结）的纬度界限：需要比陆地冰盖更冷
        /// （海水比热大、冰点低）。
        /// </summary>
        public static double SeaIceLatitude(double axialTilt)
        {
            return TemperatureLimitLatitude(axialTilt, SeaIceTemperature);
        }

        // ── 地图起伏标定 ──

        /// <summary>
        /// 参考海拔（米）：取地图最高陆地海拔，并保证不低于下限。
        /// </summary>
        public static double ReferenceElevation(double maxLandElevation)
        {
            if (double.IsNaN(maxLandElevation))
                return MinimumReferenceElevation;
            return Math.Max(maxLandElevation, MinimumReferenceElevation);
        }

        /// <summary>
        /// 海拔标定系数：地图起伏相对地球起伏的比例（地球陆地最高 8848 m）。
        /// 绝对雪线乘以它即得到该地图上等效的雪线高度。
        /// </summary>
        public static double ReliefScale(double maxLandElevation)
        {
            return ReferenceElevation(maxLandElevation) / EarthMaxLandElevation;
        }

        /// <summary>雪线区间按地图起伏标定后的结果（米）。</summary>
        public static void ScaledSnowLineRange(double absLat, double axialTilt,
            double maxLandElevation, out double minMeters, out double maxMeters)
        {
            SnowLineRange(absLat, axialTilt, out double min, out double max);
            double k = ReliefScale(maxLandElevation);
            minMeters = min * k;
            maxMeters = max * k;
        }

        /// <summary>
        /// 该地图上某纬度的永久雪线（米，已按地图起伏标定）：
        /// 湿润处靠近区间下限（海洋性山地雪线低），干旱处靠近上限（内陆雪线高）。
        /// </summary>
        /// <param name="absLat">纬度绝对值（度）</param>
        /// <param name="axialTilt">地轴倾角（度）</param>
        /// <param name="maxLandElevation">该地图最高陆地海拔（米），用作标定基准</param>
        /// <param name="moisture">当地湿度（0-1），湿润取低雪线，干旱取高雪线</param>
        public static double SnowLineElevation(double absLat, double axialTilt,
            double maxLandElevation, double moisture)
        {
            ScaledSnowLineRange(absLat, axialTilt, maxLandElevation, out double min, out double max);
            double m = Clamp(moisture, 0.0, 1.0);
            return max + (min - max) * m;
        }

        /// <summary>雪线附近的斑状积雪过渡带宽度（米）。</summary>
        public static double SnowLineTransition(double maxLandElevation)
        {
            return ReferenceElevation(maxLandElevation) * SnowLineTransitionRatio;
        }

        /// <summary>雪线抖动幅度（米），让冰缘呈不规则形态。</summary>
        public static double SnowLineNoiseAmplitude(double maxLandElevation)
        {
            return ReferenceElevation(maxLandElevation) * SnowLineNoiseRatio;
        }

        // ── 覆盖率（0-1，供噪声判定使用） ──

        /// <summary>纬度方向的常年冰盖覆盖率（0-1），冰缘在此区间内渐变。</summary>
        public static double LatitudeIceCoverage(double absLat, double axialTilt)
        {
            double limit = IceLimitLatitude(axialTilt);
            return SmoothRamp(absLat, limit - LatitudeTransition, limit);
        }

        /// <summary>海冰覆盖率（0-1）。</summary>
        public static double SeaIceCoverage(double absLat, double axialTilt)
        {
            double limit = SeaIceLatitude(axialTilt);
            return SmoothRamp(absLat, limit - LatitudeTransition, limit);
        }

        /// <summary>
        /// 海拔方向的常年冰盖覆盖率（0-1）：海拔高于当地雪线即为常年积雪，
        /// 雪线以下一个过渡带内为斑状积雪（由噪声决定），使冰缘不规则。
        /// </summary>
        public static double ElevationIceCoverage(float elevation, double snowLine, double transition)
        {
            if (transition <= 0.0)
                return elevation >= snowLine ? 1.0 : 0.0;
            return SmoothRamp(elevation, snowLine - transition, snowLine);
        }

        /// <summary>平滑渐变的覆盖率（0-1），用作噪声判定的阈值。</summary>
        private static double SmoothRamp(double value, double from, double to)
        {
            if (to <= from)
                return value >= to ? 1.0 : 0.0;

            double t = Clamp((value - from) / (to - from), 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
