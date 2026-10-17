using System;
using System.Collections.Generic;
using System.Text;

namespace WorldTerrain
{
    /// <summary>
    /// 有序的生成管线：按顺序执行 <see cref="IGenerationStep"/>，并把每个步骤内部的 0-1 进度
    /// 映射到该步骤在总进度条上占的区间。
    ///
    /// 之所以把进度区间**显式**写在每个步骤里（而不是按权重平均分），是为了保持
    /// 进度条的时间分布与原实现一致：球面地形那一段本来就慢，占的区间也大。
    /// </summary>
    public sealed class GenerationPipeline
    {
        private readonly List<IGenerationStep> steps = new List<IGenerationStep>();

        public IReadOnlyList<IGenerationStep> Steps => steps;

        public GenerationPipeline Add(IGenerationStep step)
        {
            if (step == null)
                throw new ArgumentNullException(nameof(step));
            steps.Add(step);
            return this;
        }

        /// <summary>
        /// 完整管线：板块 → 地形 → 气候 → 侵蚀 → 海平面 → 气候反馈 → 水文
        /// → 分类 → 渲染（板块图/高度图/地形图） → 人文。
        /// </summary>
        public static GenerationPipeline CreateDefault()
        {
            return new GenerationPipeline()
                .Add(new PlateGenerationStep())
                .Add(new TerrainHeightStep())
                .Add(new ClimateStep())
                .Add(new ErosionStep())
                .Add(new SeaLevelStep())
                .Add(new ClimateFeedbackStep())
                .Add(new HydrologyStep())
                .Add(new ClassificationStep())
                .Add(new PlateOverlayStep())
                .Add(new HeightMapStep())
                .Add(new TerrainMapStep())
                .Add(new HumanStep());
        }

        /// <summary>执行整条管线。</summary>
        public void Run(TerrainContext ctx, Action<float> onProgress = null)
        {
            if (ctx == null)
                throw new ArgumentNullException(nameof(ctx));

            foreach (IGenerationStep step in steps)
                RunOne(step, ctx, onProgress);
        }

        /// <summary>
        /// 只执行名字出现在 <paramref name="stepNames"/> 里的步骤（按管线顺序）。
        /// 用于「只生成地形不生成人文」这类子集运行，以及单步调试。
        /// </summary>
        public void RunSubset(TerrainContext ctx, IEnumerable<string> stepNames,
            Action<float> onProgress = null)
        {
            if (ctx == null)
                throw new ArgumentNullException(nameof(ctx));
            if (stepNames == null)
                return;

            var wanted = new HashSet<string>(stepNames);
            foreach (IGenerationStep step in steps)
                if (wanted.Contains(step.Name))
                    RunOne(step, ctx, onProgress);
        }

        /// <summary>
        /// 单独执行一个步骤（单元测试入口）。
        /// 未注册该名字时返回 false。
        /// </summary>
        public bool RunStep(string stepName, TerrainContext ctx, Action<float> onProgress = null)
        {
            foreach (IGenerationStep step in steps)
            {
                if (step.Name == stepName)
                {
                    RunOne(step, ctx, onProgress);
                    return true;
                }
            }
            return false;
        }

        /// <summary>按名字取步骤（找不到返回 null）。</summary>
        public IGenerationStep Find(string stepName)
        {
            foreach (IGenerationStep step in steps)
                if (step.Name == stepName)
                    return step;
            return null;
        }

        /// <summary>管线概览（日志/自检用）。</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            foreach (IGenerationStep step in steps)
            {
                (float from, float to) = step.ProgressSpan;
                sb.Append($"  {step.Name,-24} {from * 100,5:F1}% → {to * 100,5:F1}%\n");
            }
            return sb.ToString();
        }

        private static void RunOne(IGenerationStep step, TerrainContext ctx, Action<float> onProgress)
        {
            (float from, float to) = step.ProgressSpan;
            float span = to - from;

            onProgress?.Invoke(from);
            step.Run(ctx, span <= 0f
                ? (Action<float>)null
                : p => onProgress?.Invoke(from + span * p));
            onProgress?.Invoke(to);
        }
    }
}
