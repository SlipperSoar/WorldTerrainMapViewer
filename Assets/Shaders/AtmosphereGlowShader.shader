Shader "Custom/AtmosphereGlowShader"
{
    /// <summary>
    /// 行星大气边缘辉光（Fresnel）：一层**独立于地表**的透明球壳。
    ///
    /// 为什么不再把 Fresnel 算在地表 shader 里：
    ///   - 地表开了凹凸位移后，辉光会跟着顶点变形、几乎看不见；
    ///   - 云层球壳套在星球外面，会把贴在地表的辉光整个盖住。
    /// 本球壳半径比云层还大（由 AtmosphereGlow.cs 控制），渲染顺序在云层之后，
    /// 所以辉光始终是最外一圈，与地形凹凸、云层显隐都无关。
    ///
    /// 关键渲染技巧：
    ///   - Cull Back（只画球壳正面）：正面三角形在相机与星球之间，深度比地表小，
    ///     能通过 ZTest 正常画出来；若用 Cull Front，背面三角形在星球后方会被
    ///     地表深度剔除，辉光就完全看不见了（踩过的坑）。
    ///   - 正面法线朝外：正对镜头处 NdotV≈1 → 1-NdotV≈0 无辉光；轮廓边缘
    ///     NdotV→0 → 辉光最强，形成一圈干净、不遮挡内部的边缘光环。
    ///   - Blend One One（加色）：辉光是「发光」，不遮挡下面的地表/云层。
    ///   - ZWrite Off + Queue Transparent+10：最后画，且不写深度。
    /// </summary>
    Properties
    {
        _FresnelColor ("Glow Color", Color) = (0.45, 0.72, 1.0, 1)
        _FresnelPower ("Edge Falloff", Range(0.5, 8)) = 2.2
        _FresnelStrength ("Glow Strength", Range(0, 3)) = 1.1
        _SunInfluence ("Sun-side Boost", Range(0, 2)) = 0.6
    }

    SubShader
    {
        // 比云层（Queue=Transparent）更靠后，确保辉光盖在云层之外
        Tags { "RenderType"="Transparent" "Queue"="Transparent+10" "IgnoreProjector"="True" }
        LOD 100

        Pass
        {
            Blend One One          // 加色发光，不遮挡
            ZWrite Off
            Cull Back              // 只画正面（在相机与星球之间，不会被深度剔除）
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos       : SV_POSITION;
                float3 worldPos  : TEXCOORD0;
                float3 worldNorm : TEXCOORD1;
            };

            fixed4 _FresnelColor;
            float _FresnelPower, _FresnelStrength, _SunInfluence;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNorm = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 N = normalize(i.worldNorm);       // 正面三角形：法线朝外
                float3 V = normalize(_WorldSpaceCameraPos - i.worldPos);

                // 标准 Fresnel：正对镜头 NdotV≈1 → 无辉光；轮廓边缘 NdotV→0 → 最强。
                float ndv = saturate(dot(N, V));
                float rim = pow(saturate(1.0 - ndv), _FresnelPower);

                // 向阳侧更亮一点，模拟大气散射的方向性（不依赖具体光源数量）
                float3 L = normalize(_WorldSpaceLightPos0.xyz);
                float sun = saturate(dot(N, L));
                float boost = 1.0 + _SunInfluence * sun;

                float intensity = rim * _FresnelStrength * boost;
                // 辉光是「发光」，不做雾衰减；Blend One One 下 rgb 直接叠加，
                // alpha 不参与混合，写 1 即可。
                return fixed4(_FresnelColor.rgb * intensity, 1.0);
            }
            ENDCG
        }
    }
    FallBack Off
}
