using System;

namespace WorldTerrain
{
    /// <summary>
    /// 步骤 1｜板块：按种子生成板块（中心 / 漂移方向 / 速度），再做逐像素球面 Voronoi 归属。
    ///
    /// 产物：<see cref="TerrainContext.Plates"/>、<see cref="TerrainContext.PlateIds"/>。
    /// 依赖：无（只用 Config 与 Rng）。
    /// </summary>
    public sealed class PlateGenerationStep : GenerationStep
    {
        public override string Name => "板块";

        public override (float from, float to) ProgressSpan => (0.00f, 0.15f);

        protected override void Execute(TerrainContext ctx, Action<float> progress)
        {
            Action<float> report = Map(progress);

            report?.Invoke(0.05f / 0.15f);
            ctx.Plates = PlateTectonicsGenerator.GeneratePlates(ctx.Config, ctx.Rng);

            report?.Invoke(1f);
            ctx.PlateIds = PlateTectonicsGenerator.AssignPlates(
                ctx.Width, ctx.Height, ctx.Plates, ctx.Noise);
        }
    }

    /// <summary>
    /// 步骤 2｜地形高度场。
    ///
    /// 两条路径：
    ///   - 球面（默认）：在单位球面等面积网格上生成，再投影成等距圆柱图；
    ///     没有极点奇点、没有经度接缝，同时产出球面径流场。
    ///   - 平面（旧路径，便于对比回退）：直接在矩形网格上生成。
    ///
    /// 产物：<see cref="TerrainContext.HeightField"/>、<see cref="TerrainContext.SphereRunoff"/>、
    /// <see cref="TerrainContext.SphereRunoffThreshold"/>。
    /// 依赖：<see cref="TerrainContext.Plates"/>。
    /// </summary>
    public sealed class TerrainHeightStep : GenerationStep
    {
        public override string Name => "地形高度场";

        public override (float from, float to) ProgressSpan => (0.15f, 0.30f);

        protected override void Execute(TerrainContext ctx, Action<float> progress)
        {
            ctx.Require(ctx.Plates != null, "Plates（板块）");

            if (!ctx.Config.planarGeneration)
            {
                // 球面生成域
                SphereTerrainResult sphere = SphericalTerrainGenerator.Generate(
                    ctx.Config, ctx.Plates, ctx.Noise, Map(progress));

                ctx.HeightField = sphere.Height;
                ctx.SphereRunoff = sphere.Runoff;
                ctx.SphereRunoffThreshold = sphere.RunoffChannelThreshold;
            }
            else
            {
                ctx.HeightField = PlateTectonicsGenerator.GenerateHeights(
                    ctx.Config, ctx.Rng, ctx.PlateIds, ctx.Plates, ctx.Noise);

                // 平面路径没有球面径流场：水文会自动退回平面判据，
                // 不会误用「上一张图留下的球面汇流」。
                ctx.SphereRunoff = null;
                ctx.SphereRunoffThreshold = 0f;
            }
        }
    }

    /// <summary>
    /// 步骤 3｜气候场（第一遍）：纬向蒸发 + 盛行风带输送 + 迎风坡降水/雨影。
    ///
    /// 产物：<see cref="TerrainContext.Moisture"/>。
    /// 依赖：<see cref="TerrainContext.HeightField"/>。
    /// </summary>
    public sealed class ClimateStep : GenerationStep
    {
        public override string Name => "气候场";

        public override (float from, float to) ProgressSpan => (0.30f, 0.37f);

        protected override void Execute(TerrainContext ctx, Action<float> progress)
        {
            ctx.Require(ctx.HeightField != null, "HeightField（高度场）");

            ctx.Moisture = ClimateModel.ComputeMoistureField(
                ctx.Width, ctx.Height, ctx.HeightField, ctx.Noise, ctx.Config.axialTilt,
                Map(progress));
        }
    }

    /// <summary>
    /// 步骤 4｜侵蚀：水力（雨滴冲刷河谷、下游沉积）+ 热力（休止角），强度与降水耦合。
    ///
    /// 球面路径已在球面格点上做过一次大尺度侵蚀（等面积、无极点接缝），
    /// 这里再在投影后的像素网格上做一次细尺度雕刻 —— 两级尺度互补：
    /// 球面那级决定山谷走向，这一级决定河谷的锐利程度。
    ///
    /// 原地修改：<see cref="TerrainContext.HeightField"/>。
    /// 依赖：HeightField、Moisture。
    /// </summary>
    public sealed class ErosionStep : GenerationStep
    {
        public override string Name => "侵蚀";

        public override (float from, float to) ProgressSpan => (0.37f, 0.47f);

        protected override void Execute(TerrainContext ctx, Action<float> progress)
        {
            ctx.Require(ctx.HeightField != null, "HeightField（高度场）");

            ErosionSimulator.Erode(ctx.Width, ctx.Height, ctx.HeightField, ctx.Moisture,
                ctx.Config.seed, Map(progress));
        }
    }

    /// <summary>
    /// 步骤 5｜海平面标定：侵蚀会整体削低陆地，重新标定海平面以保持海陆比例。
    ///
    /// 原地修改：<see cref="TerrainContext.HeightField"/>。
    /// 依赖：HeightField。
    /// </summary>
    public sealed class SeaLevelStep : GenerationStep
    {
        public override string Name => "海平面标定";

        public override (float from, float to) ProgressSpan => (0.47f, 0.48f);

        protected override void Execute(TerrainContext ctx, Action<float> progress)
        {
            ctx.Require(ctx.HeightField != null, "HeightField（高度场）");

            PlateTectonicsGenerator.ApplyWaterCoverage(
                ctx.Width, ctx.Height, ctx.HeightField, ctx.Config.waterCoverage);
        }
    }

    /// <summary>
    /// 步骤 6｜气候反馈：地形变了，复算一次气候 —— 迎风坡与雨影随之改变（气候 ↔ 地形一次反馈）。
    ///
    /// 产物：<see cref="TerrainContext.Moisture"/>（覆盖第一遍的结果）。
    /// 依赖：HeightField。
    /// </summary>
    public sealed class ClimateFeedbackStep : GenerationStep
    {
        public override string Name => "气候反馈";

        public override (float from, float to) ProgressSpan => (0.48f, 0.50f);

        protected override void Execute(TerrainContext ctx, Action<float> progress)
        {
            ctx.Require(ctx.HeightField != null, "HeightField（高度场）");

            ctx.Moisture = ClimateModel.ComputeMoistureField(
                ctx.Width, ctx.Height, ctx.HeightField, ctx.Noise, ctx.Config.axialTilt,
                Map(progress));
        }
    }
}
