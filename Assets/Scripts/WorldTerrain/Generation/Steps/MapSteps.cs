using System;

namespace WorldTerrain
{
    /// <summary>
    /// 步骤 7｜水文：河网、湖泊、三角洲。
    ///
    /// 球面径流场显式传入：河网走向按球面真实汇流判定，平面汇流只决定河宽。
    /// 湖泊在像素网格上判定，但面积门槛按真实球面面积（cos lat）折算，纬度不失真。
    ///
    /// 产物：<see cref="TerrainContext.Hydrology"/>。
    /// 依赖：HeightField、Moisture、SphereRunoff/SphereRunoffThreshold（球面路径）。
    /// </summary>
    public sealed class HydrologyStep : GenerationStep
    {
        public override string Name => "水文";

        public override (float from, float to) ProgressSpan => (0.50f, 0.65f);

        protected override void Execute(TerrainContext ctx, Action<float> progress)
        {
            ctx.Require(ctx.HeightField != null, "HeightField（高度场）");

            ctx.Hydrology = HydrologyGenerator.Generate(
                ctx.Width, ctx.Height, ctx.HeightField, ctx.Noise,
                Map(progress), ctx.Moisture,
                ctx.SphereRunoff, ctx.SphereRunoffThreshold, ctx.Config.axialTilt);
        }
    }

    /// <summary>
    /// 步骤 8｜地形分类：地形类型 + 冰盖/雪线规则（依据地图的地轴倾角）。
    ///
    /// 产物：<see cref="TerrainContext.TerrainTypes"/>。
    /// 依赖：HeightField、PlateIds、Hydrology、Moisture。
    /// </summary>
    public sealed class ClassificationStep : GenerationStep
    {
        public override string Name => "地形分类";

        public override (float from, float to) ProgressSpan => (0.65f, 0.70f);

        protected override void Execute(TerrainContext ctx, Action<float> progress)
        {
            ctx.Require(ctx.HeightField != null, "HeightField（高度场）");
            ctx.Require(ctx.PlateIds != null, "PlateIds（板块归属）");

            // 群系判定用「海事」湿度场：副热带岛屿与海岸不该被陆地式的降水曲线判成荒漠
            ctx.BiomeMoisture = ClimateModel.ComputeMoistureField(
                ctx.Width, ctx.Height, ctx.HeightField, ctx.Noise, ctx.Config.axialTilt);

            ctx.TerrainTypes = TerrainClassifier.Classify(
                ctx.Width, ctx.Height, ctx.HeightField,
                ctx.PlateIds, ctx.Hydrology, ctx.Noise, ctx.Config.axialTilt, ctx.BiomeMoisture);
        }
    }

    /// <summary>
    /// 步骤 9｜板块分界图（含透明度，既能落盘也能当叠加层）。
    ///
    /// 产物：<see cref="TerrainContext.PlateOverlayColors"/>。
    /// 依赖：PlateIds。
    /// </summary>
    public sealed class PlateOverlayStep : GenerationStep
    {
        public override string Name => "板块分界图";

        public override (float from, float to) ProgressSpan => (0.70f, 0.75f);

        protected override void Execute(TerrainContext ctx, Action<float> progress)
        {
            ctx.Require(ctx.PlateIds != null, "PlateIds（板块归属）");

            ctx.PlateOverlayColors = TerrainMapRenderer.RenderPlateMap(
                ctx.Width, ctx.Height, ctx.PlateIds, true);
        }
    }

    /// <summary>
    /// 步骤 10｜灰度高度图（供地形凹凸位移使用）。
    ///
    /// 产物：<see cref="TerrainContext.HeightColors"/>。
    /// 依赖：HeightField。
    /// </summary>
    public sealed class HeightMapStep : GenerationStep
    {
        public override string Name => "高度图";

        public override (float from, float to) ProgressSpan => (0.75f, 0.82f);

        protected override void Execute(TerrainContext ctx, Action<float> progress)
        {
            ctx.Require(ctx.HeightField != null, "HeightField（高度场）");

            ctx.HeightColors = TerrainMapRenderer.RenderHeightMap(
                ctx.Width, ctx.Height, ctx.HeightField);
        }
    }

    /// <summary>
    /// 步骤 11｜彩色地形图（按海拔分层设色 + 水系/冰盖叠加）。
    ///
    /// 产物：<see cref="TerrainContext.TerrainColors"/>。
    /// 依赖：HeightField、TerrainTypes、Hydrology。
    /// </summary>
    public sealed class TerrainMapStep : GenerationStep
    {
        public override string Name => "地形图";

        public override (float from, float to) ProgressSpan => (0.82f, 0.92f);

        protected override void Execute(TerrainContext ctx, Action<float> progress)
        {
            ctx.Require(ctx.HeightField != null, "HeightField（高度场）");
            ctx.Require(ctx.TerrainTypes != null, "TerrainTypes（地形分类）");

            ctx.TerrainColors = TerrainMapRenderer.RenderTerrainMap(
                ctx.Width, ctx.Height, ctx.HeightField,
                ctx.TerrainTypes, ctx.Hydrology, ctx.Noise);
        }
    }

    /// <summary>
    /// 步骤 12｜人文：聚落 / 国家 / 交通线 / 中文地名。
    ///
    /// 顺带把湿度场降采样后挂到人文数据上，供云层预览使用
    /// （复用 <c>&lt;地图名&gt;_human.json</c>，不新增文件）。
    ///
    /// 产物：<see cref="TerrainContext.Human"/>。
    /// 依赖：HeightField、Hydrology、Moisture。
    /// </summary>
    public sealed class HumanStep : GenerationStep
    {
        /// <summary>云层用的湿度场目标宽度（降采样后约 32 KB）。</summary>
        public const int CloudMoistureWidth = 256;

        public override string Name => "人文";

        public override (float from, float to) ProgressSpan => (0.92f, 1.00f);

        protected override void Execute(TerrainContext ctx, Action<float> progress)
        {
            ctx.Require(ctx.HeightField != null, "HeightField（高度场）");

            ctx.Human = HumanGeographyGenerator.Generate(
                ctx.Width, ctx.Height, ctx.HeightField, ctx.Hydrology,
                ctx.Config.axialTilt, ctx.Noise, ctx.Config.seed,
                Map(progress), ctx.Moisture);

            AttachCloudMoisture(ctx);
        }

        /// <summary>
        /// 把湿度场降采样后挂到人文数据上。单独抽出来，便于测试与将来改成独立落盘。
        /// </summary>
        public static void AttachCloudMoisture(TerrainContext ctx)
        {
            if (ctx.Human == null || ctx.Moisture == null)
                return;

            byte[] cloudMoisture = HumanGeographyData.DownsampleMoisture(
                ctx.Moisture, ctx.Width, ctx.Height, CloudMoistureWidth,
                out int mw, out int mh);

            ctx.Human.SetMoistureField(cloudMoisture, mw, mh);
        }
    }
}
