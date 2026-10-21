using System;

namespace WorldTerrain
{
    /// <summary>
    /// 星球的物理属性：大小与自转方向。
    ///
    /// 这两项是**地图属性**（随地图一起保存、切换地图时一起应用），
    /// 不影响地形生成本身，只决定「这颗星球看起来多大、转哪边」。
    ///
    /// 大小用**半径（km）**表达，显示尺寸以地球半径为基准换算成倍数：
    /// 1.0 倍 = 6371 km。这样同一张地形图换到更大的星球上时，
    /// 观看比例（镜头距离 / 星球半径）保持不变。
    /// </summary>
    public static class PlanetProfile
    {
        /// <summary>基准半径（地球，km）。显示尺寸的 1.0 倍就是它。</summary>
        public const float ReferenceRadiusKm = 6371f;

        /// <summary>最小半径（0.4 倍地球，约火星量级）。</summary>
        public const float MinRadiusKm = 2548f;

        /// <summary>最大半径（2.5 倍地球，超级地球量级）。</summary>
        public const float MaxRadiusKm = 15928f;

        /// <summary>默认半径 = 地球。</summary>
        public const float DefaultRadiusKm = ReferenceRadiusKm;

        /// <summary>把任意半径夹到允许区间；NaN 回落到默认值。</summary>
        public static float ClampRadiusKm(float radiusKm)
        {
            if (float.IsNaN(radiusKm) || radiusKm <= 0f)
                return DefaultRadiusKm;
            if (radiusKm < MinRadiusKm) return MinRadiusKm;
            if (radiusKm > MaxRadiusKm) return MaxRadiusKm;
            return radiusKm;
        }

        /// <summary>半径 → 显示倍数（1.0 = 地球大小）。</summary>
        public static float ScaleForRadiusKm(float radiusKm)
        {
            return ClampRadiusKm(radiusKm) / ReferenceRadiusKm;
        }

        /// <summary>显示倍数 → 半径（km）。</summary>
        public static float RadiusKmForScale(float scale)
        {
            if (float.IsNaN(scale) || scale <= 0f)
                return DefaultRadiusKm;
            return ClampRadiusKm(scale * ReferenceRadiusKm);
        }

        /// <summary>显示用标签，例如 “6371 km（1.00× 地球）”。</summary>
        public static string RadiusLabel(float radiusKm)
        {
            float km = ClampRadiusKm(radiusKm);
            return $"{km:F0} km（{km / ReferenceRadiusKm:F2}×）";
        }

        /// <summary>自转方向的显示名。</summary>
        public static string RotationLabel(bool retrograde)
        {
            return retrograde ? "逆向自转" : "顺向自转";
        }

        /// <summary>
        /// 自转方向对「日行角」的符号：顺向 +1、逆向 −1。
        ///
        /// 时间本身仍然单调递增（0→24 时），变的是太阳绕星球转动的方向 ——
        /// 逆向自转时太阳自西向东升，与地球相反。
        /// </summary>
        public static float DayAngleSign(bool retrograde)
        {
            return retrograde ? -1f : 1f;
        }
    }
}
