using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Windows 播放器构建入口，供命令行（-executeMethod）与发布脚本调用。
///
/// 用法：
///   Tuanjie.exe -batchmode -nographics -quit -projectPath &lt;仓库&gt; \
///       -executeMethod WindowsBuild.Build -logFile -
///
/// 输出目录可用环境变量 <c>WTMV_BUILD_DIR</c> 覆盖（默认为 Build，与历史产物位置一致）。
/// 构建失败时以退出码 1 结束，成功时以 0 结束并打印 BUILD_OK 与产物大小，
/// 便于发布脚本用退出码与日志判定结果。
/// </summary>
public static class WindowsBuild
{
    /// <summary>构建产物目录（相对仓库根），与历史产物位置保持一致。</summary>
    public const string DefaultOutputDir = "Build";

    /// <summary>可执行文件名。</summary>
    public const string ExeName = "WorldTerrainMapViewer.exe";

    /// <summary>环境变量：覆盖输出目录。</summary>
    public const string OutputDirEnvVar = "WTMV_BUILD_DIR";

    /// <summary>命令行入口。</summary>
    public static void Build()
    {
        int exitCode = Run();
        // 批处理模式下必须显式退出，否则 -quit 的退出码不反映构建结果
        EditorApplication.Exit(exitCode);
    }

    private static int Run()
    {
        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
        string outDir = Environment.GetEnvironmentVariable(OutputDirEnvVar);
        if (string.IsNullOrEmpty(outDir))
            outDir = DefaultOutputDir;
        if (!Path.IsPathRooted(outDir))
            outDir = Path.Combine(projectRoot, outDir);

        string exePath = Path.Combine(outDir, ExeName);

        string[] scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled && !string.IsNullOrEmpty(s.path))
            .Select(s => s.path)
            .ToArray();

        if (scenes.Length == 0)
        {
            Debug.LogError("[Build] EditorBuildSettings 里没有启用的场景，无法构建");
            return 1;
        }

        Debug.Log($"[Build] 目标 {BuildTarget.StandaloneWindows64}");
        Debug.Log($"[Build] 输出 {exePath}");
        foreach (string scene in scenes)
            Debug.Log($"[Build] 场景 {scene}");

        Directory.CreateDirectory(outDir);

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = exePath,
            target = BuildTarget.StandaloneWindows64,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        if (summary.result != BuildResult.Succeeded)
        {
            Debug.LogError($"[Build] 失败：{summary.result}，" +
                           $"错误 {summary.totalErrors} 个，警告 {summary.totalWarnings} 个");
            return 1;
        }

        // 汇总产物大小，方便发布脚本与日志核对
        long totalBytes = 0;
        int fileCount = 0;
        foreach (string file in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories))
        {
            totalBytes += new FileInfo(file).Length;
            fileCount++;
        }

        Debug.Log($"[Build] 成功：{fileCount} 个文件，" +
                  $"{totalBytes / 1024.0 / 1024.0:F1} MB，耗时 {summary.totalTime.TotalSeconds:F0} s");
        Debug.Log($"BUILD_OK {exePath} {totalBytes}");
        return 0;
    }
}
