Shader "Custom/CloudLayerShader"
{
    Properties
    {
        // 湿度场（等距圆柱投影，R 通道 = 0-1）。云量由它调制。
        _MoistureTex ("Moisture Map (R = humidity)", 2D) = "gray" {}
        // 水汽加成：河网 / 湖泊 / 三角洲（R 通道 = 0-1）
        _WaterTex ("Water Bodies (R)", 2D) = "black" {}

        _Coverage ("云量基准 Coverage", Range(0,1)) = 0.65
        _MoistureInfluence ("湿度影响 Moisture Influence", Range(0,1)) = 0.55
        _WaterInfluence ("水体加成 Water Influence", Range(0,1)) = 0.30
        _Sharpness ("云缘锐度 Sharpness", Range(1,12)) = 5.0

        _NoiseScale ("噪声频率 Noise Scale", Range(0.5,12)) = 3.0
        _DetailScale ("细节频率 Detail Scale", Range(1,30)) = 11.0
        _DetailAmount ("细节强度 Detail Amount", Range(0,1)) = 0.35
        _NoiseFloor ("噪声归一化下界 Noise Floor", Range(0,1)) = 0.35
        _NoiseCeil ("噪声归一化上界 Noise Ceil", Range(0,1)) = 0.70

        _FlowSpeed ("流动速度 Flow Speed", Range(0,2)) = 0.02
        _SeasonShift ("季节南北位移 Season Shift", Range(-0.5,0.5)) = 0.0

        _Color ("云的颜色 Cloud Color", Color) = (1,1,1,1)
        _ShadowColor ("背阳面色 Shadow Color", Color) = (0.42,0.47,0.58,1)
        _Opacity ("最大不透明度 Max Opacity", Range(0,1)) = 0.92
        _SunDirection ("太阳方向（世界空间，内部使用）", Vector) = (1,0,0,0)
        _SilverLining ("银边强度 Silver Lining", Range(0,2)) = 0.65
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" }
        LOD 100

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            // 球壳略大于星球：相机在外侧，只画正面即可；Cull Off 兼容贴近表面观察
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma target 3.0

            #include "UnityCG.cginc"

            #define TWO_PI 6.28318530718

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 localPos : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                UNITY_FOG_COORDS(2)
            };

            sampler2D _MoistureTex;
            sampler2D _WaterTex;

            float _Coverage;
            float _MoistureInfluence;
            float _WaterInfluence;
            float _Sharpness;
            float _NoiseScale;
            float _DetailScale;
            float _DetailAmount;
            float _NoiseFloor;
            float _NoiseCeil;
            float _FlowSpeed;
            float _SeasonShift;
            float4 _Color;
            float4 _ShadowColor;
            float _Opacity;
            float4 _SunDirection;
            float _SilverLining;

            // 注意：_Time 是 UnityCG.cginc 里的内置 uniform（float4，_Time.y = 秒），
            // 不能再自己声明一次 —— 会报 "redefinition of '_Time'"。

            // ── 三维值噪声 + FBM ──
            float Hash13(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            float ValueNoise(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);

                float n000 = Hash13(i + float3(0, 0, 0));
                float n100 = Hash13(i + float3(1, 0, 0));
                float n010 = Hash13(i + float3(0, 1, 0));
                float n110 = Hash13(i + float3(1, 1, 0));
                float n001 = Hash13(i + float3(0, 0, 1));
                float n101 = Hash13(i + float3(1, 0, 1));
                float n011 = Hash13(i + float3(0, 1, 1));
                float n111 = Hash13(i + float3(1, 1, 1));

                float x00 = lerp(n000, n100, f.x);
                float x10 = lerp(n010, n110, f.x);
                float x01 = lerp(n001, n101, f.x);
                float x11 = lerp(n011, n111, f.x);

                return lerp(lerp(x00, x10, f.y), lerp(x01, x11, f.y), f.z);
            }

            float Fbm(float3 p, int octaves)
            {
                float sum = 0.0;
                float amp = 0.5;
                float norm = 0.0;
                for (int k = 0; k < 6; k++)
                {
                    if (k >= octaves) break;
                    sum += ValueNoise(p) * amp;
                    norm += amp;
                    p *= 2.03;
                    amp *= 0.5;
                }
                return sum / max(norm, 1e-5);
            }

            /// <summary>等距圆柱 UV：与 PlateOverlayShader / EarthManager.UVToWorld 同一约定。</summary>
            float2 EquirectangularUV(float3 localPos)
            {
                float3 p = normalize(localPos);
                float longitude = atan2(p.x, p.z);
                float latitude = asin(clamp(p.y, -1.0, 1.0));
                return float2(longitude / TWO_PI + 0.5, 1.0 - (latitude / 3.14159265 + 0.5));
            }

            /// <summary>
            /// 纬向风带（与生成端的盛行风带一致）：
            ///   信风     |lat| &lt; 30°  → 自东向西（-1）
            ///   西风带   30°~60°       → 自西向东（+1）
            ///   极地东风 &gt; 60°        → 自东向西（-1）
            /// 返回 [-1,1] 的东西向流动方向。
            /// </summary>
            float WindBand(float latDeg)
            {
                float a = abs(latDeg);
                float trade = -1.0;
                float westerly = 1.0;
                float polar = -1.0;

                float w = 1.0 - smoothstep(24.0, 34.0, a);          // 信风权重
                float p = smoothstep(24.0, 34.0, a) * (1.0 - smoothstep(54.0, 64.0, a));
                float q = smoothstep(54.0, 64.0, a);                // 极地东风权重
                float s = w + p + q;
                return (trade * w + westerly * p + polar * q) / max(s, 1e-5);
            }

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.localPos = v.vertex.xyz;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = EquirectangularUV(i.localPos);

                // ── 1. 气候数据：湿度场 + 水体加成 ──
                float humidity = tex2D(_MoistureTex, uv).r;
                float water = tex2D(_WaterTex, uv).r;

                // 水汽供给：湿度为主，河湖三角洲叠加
                float moisture = saturate(humidity + water * _WaterInfluence);

                // ── 2. 云量：水汽越多，覆盖率越高（越容易成云）──
                //    干燥区仍保留 1/4 的基准云量，避免沙漠上空完全空白
                float coverage = _Coverage * lerp(0.25, 1.0, moisture);

                // ── 3. 噪声：基础形状 + 细节层 ──
                // 季节位移：南北向平移采样坐标（倾角越大，云带南北摆动越明显）
                float lat = (0.5 - uv.y) * 3.14159265;
                float latDeg = degrees(lat);
                float2 shifted = uv;
                shifted.y -= _SeasonShift;

                // 绕球面的三维采样坐标：经度方向按风带流动
                float flow = WindBand(latDeg) * _FlowSpeed * _Time.y;
                float lon = (shifted.x - 0.5) * TWO_PI + flow;
                float3 sp = float3(cos(lat) * sin(lon), sin(lat), cos(lat) * cos(lon));

                float base = Fbm(sp * _NoiseScale, 6);
                float detail = Fbm(sp * _DetailScale + 17.3, 4);
                float raw = base * (1.0 - _DetailAmount) + detail * _DetailAmount;

                // 归一化到 [0,1]：值噪声 FBM 天然集中在 0.5 附近
                // （实测 p10≈0.35 / p50≈0.52 / p90≈0.62），不归一化就只有最密的尖峰能成云。
                float density = saturate((raw - _NoiseFloor) / max(_NoiseCeil - _NoiseFloor, 1e-3));

                // ── 4. 覆盖率直接控制云占天空的比例 ──
                //    归一化后的 density 中位约 0.49，因此「成云比例 ≈ coverage 的量级」
                float cloud = saturate((density - (1.0 - coverage)) * _Sharpness);

                // 云缘再软化一点，避免硬边
                cloud = cloud * cloud * (3.0 - 2.0 * cloud);

                if (cloud <= 0.001)
                    discard;

                // ── 5. 光照：向阳面亮、背阳面暗，靠近太阳一侧加银边 ──
                float3 n = normalize(mul((float3x3)unity_ObjectToWorld, i.localPos));
                float3 sunDir = normalize(_SunDirection.xyz);
                float sunDot = dot(n, sunDir);

                float lit = saturate(sunDot * 0.5 + 0.5);            // 0 背阳 → 1 向阳
                float3 col = lerp(_ShadowColor.rgb, _Color.rgb, lit);

                // 银边：云缘在太阳方向附近透光发亮
                float rim = pow(saturate(sunDot), 6.0);
                col += _Color.rgb * rim * _SilverLining * (1.0 - cloud);

                // 厚度感：密的地方更不透明、更亮
                float alpha = cloud * _Opacity;
                col *= (0.85 + 0.35 * cloud);

                fixed4 outc = fixed4(col, alpha);
                UNITY_APPLY_FOG(i.fogCoord, outc);
                return outc;
            }
            ENDCG
        }
    }
}
