using System;

namespace WorldTerrain
{
    /// <summary>
    /// 生成管线中的一个步骤。每个步骤读 <see cref="TerrainContext"/> 里已有的字段，
    /// 填好自己负责的字段，除此之外不做别的事 —— 因此可以单独构造上下文来测试。
    ///
    /// 实现约定：
    ///   - **不要**读取上下文的 <c>Config</c>/<c>Noise</c> 以外的全局状态
    ///   - 进度回调的 p 是**步骤内部的 0-1**，映射到全局进度由管线负责
    ///   - 同一份输入必须给出同一份输出（可复现）
    /// </summary>
    public interface IGenerationStep
    {
        /// <summary>步骤名（用于日志、进度显示与出错定位）。</summary>
        string Name { get; }

        /// <summary>本步骤在总进度条上占的区间（起点、终点），用于映射内部进度。</summary>
        (float from, float to) ProgressSpan { get; }

        /// <summary>
        /// 执行步骤。<paramref name="progress"/> 报告本步骤内部的 0-1 进度，可为 null。
        /// </summary>
        void Run(TerrainContext ctx, Action<float> progress);
    }

    /// <summary>把只关心「做完」的步骤包成 <see cref="IGenerationStep"/>。</summary>
    public abstract class GenerationStep : IGenerationStep
    {
        public abstract string Name { get; }

        public abstract (float from, float to) ProgressSpan { get; }

        protected abstract void Execute(TerrainContext ctx, Action<float> progress);

        public void Run(TerrainContext ctx, Action<float> progress)
        {
            ctx.LastStepName = Name;
            Execute(ctx, progress);
        }

        /// <summary>把步骤内部进度 p（0-1）线性映射到本步骤的全局区间。</summary>
        protected Action<float> Map(Action<float> report)
        {
            if (report == null)
                return null;

            (float from, float to) = ProgressSpan;
            float span = to - from;
            return p => report(from + span * p);
        }
    }
}
