using System;
using UnityEngine;

namespace WorldTerrain
{
    /// <summary>
    /// 一次地形生成过程中**所有步骤共享的状态与阶段性产物**。
    ///
    /// 设计意图：把原来串在 <c>WorldTerrainGenerator.GenerateInBackground</c> 里的长链路
    /// （板块 → 地形 → 气候 → 侵蚀 → 水文 → 分类 → 渲染 → 人文）拆开。
    /// 每个步骤只依赖这个上下文里**已经填好的字段**，并把自己负责的字段填好；
    /// 因此任何一步都可以脱离整条管线单独调用与测试（构造一个只填了前置字段的上下文即可）。
    ///
    /// 字段按「由哪个步骤产出」分组，注释里标了步骤名，便于追依赖。
    /// 所有逐格数组长度都是 <c>Width * Height</c>（球面网格是内部细节，不外泄）。
    /// </summary>
    public sealed class TerrainContext
    {
        // ── 输入（由调用方填入，任何步骤都不应修改）──

        /// <summary>生成参数（种子、尺寸、水面占比、地轴倾角等）。</summary>
        public WorldGenConfig Config;

        /// <summary>地形噪声源。纯函数实现，可跨线程共享。</summary>
        public SeededNoise Noise;

        /// <summary>
        /// 随机源。**只有 <see cref="PlateGenerationStep"/> 使用它**，
        /// 而且必须在任何并行步骤之前按固定顺序取完，否则结果不可复现。
        /// </summary>
        public System.Random Rng;

        // ── 尺寸快捷方式 ──

        /// <summary>地图宽度（像素）。</summary>
        public int Width => Config.width;

        /// <summary>地图高度（像素）。</summary>
        public int Height => Config.height;

        /// <summary>像素总数。</summary>
        public int CellCount => Config.width * Config.height;

        // ── 阶段产物 ──

        /// <summary>[板块] 构造出的板块列表。</summary>
        public PlateInfo[] Plates;

        /// <summary>[板块] 每像素所属板块编号。</summary>
        public int[] PlateIds;

        /// <summary>[地形] 高度场（米，海平面为 0）。后续步骤原地修改它。</summary>
        public float[] HeightField;

        /// <summary>[地形] 球面径流场（只有球面路径才有，平面路径为 null）。</summary>
        public float[] SphereRunoff;

        /// <summary>[地形] 球面径流场的成河阈值分位。</summary>
        public float SphereRunoffThreshold;

        /// <summary>[气候] 最终的湿度场（0-1）。侵蚀与水文都依赖它。</summary>
        public float[] Moisture;

        /// <summary>[水文] 河道、湖泊、三角洲等掩膜。</summary>
        public HydrologyData Hydrology;

        /// <summary>[分类] 每像素的地形类型。</summary>
        public TerrainType[] TerrainTypes;

        /// <summary>[渲染] 灰度高度图颜色。</summary>
        public Color32[] HeightColors;

        /// <summary>[渲染] 彩色地形图颜色。</summary>
        public Color32[] TerrainColors;

        /// <summary>[渲染] 板块分界叠加图颜色（含透明度）。</summary>
        public Color32[] PlateOverlayColors;

        /// <summary>[人文] 聚落、国家、交通线与自然地名。</summary>
        public HumanGeographyData Human;

        // ── 诊断信息（可选，便于测试与日志）──

        /// <summary>最后执行的步骤名，出错时用于定位。</summary>
        public string LastStepName;

        // ── 便捷构造 ──

        public TerrainContext(WorldGenConfig config)
        {
            Config = config;
            Noise = new SeededNoise(config.seed);
            Rng = new System.Random(config.seed);
        }

        /// <summary>检查某个步骤的前置产物是否齐备；不齐时抛出说明清楚的异常。</summary>
        public void Require(bool condition, string what)
        {
            if (!condition)
                throw new InvalidOperationException(
                    $"步骤「{LastStepName}」缺少前置数据：{what}（请先执行产出它的步骤）");
        }

        /// <summary>
        /// 把最终结果整理成对外的数据结构。
        /// 查看器/落盘都面向它，不再直接摸上下文。
        /// </summary>
        public WorldGenResult ToResult()
        {
            return new WorldGenResult
            {
                width = Width,
                height = Height,
                heightField = HeightField,
                plateIds = PlateIds,
                terrainTypes = TerrainTypes,
                heightColors = HeightColors,
                terrainColors = TerrainColors,
                plateOverlayColors = PlateOverlayColors,
                axialTilt = Config.axialTilt,
                waterCoverage = Config.waterCoverage,
                human = Human
            };
        }
    }
}
