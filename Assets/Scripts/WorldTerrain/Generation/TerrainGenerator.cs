using System;

namespace WorldTerrain
{
    /// <summary>
    /// 生成管线的对外门面。不依赖 UnityEngine（可后台线程调用），
    /// 也不持有任何状态 —— 一次调用就是一次完整生成。
    ///
    /// 想「只生成地形、不生成人文」，用 <see cref="GenerateSubset"/> 传步骤名列表即可；
    /// 想换掉某一步，构造自己的 <see cref="GenerationPipeline"/> 传进 <see cref="Generate"/>。
    /// </summary>
    public static class TerrainGenerator
    {
        /// <summary>跑完整管线，返回对外的结果结构。</summary>
        public static WorldGenResult Generate(WorldGenConfig config,
            Action<float> onProgress = null,
            GenerationPipeline pipeline = null)
        {
            return GenerateContext(config, onProgress, pipeline).ToResult();
        }

        /// <summary>跑完整管线，返回带全部中间产物的上下文（测试与诊断用）。</summary>
        public static TerrainContext GenerateContext(WorldGenConfig config,
            Action<float> onProgress = null,
            GenerationPipeline pipeline = null)
        {
            var ctx = new TerrainContext(config);
            (pipeline ?? GenerationPipeline.CreateDefault()).Run(ctx, onProgress);
            return ctx;
        }

        /// <summary>只跑指定步骤（按管线顺序），用于子集生成与单步测试。</summary>
        public static TerrainContext GenerateSubset(WorldGenConfig config,
            string[] stepNames,
            Action<float> onProgress = null,
            GenerationPipeline pipeline = null)
        {
            var ctx = new TerrainContext(config);
            (pipeline ?? GenerationPipeline.CreateDefault())
                .RunSubset(ctx, stepNames, onProgress);
            return ctx;
        }
    }
}
