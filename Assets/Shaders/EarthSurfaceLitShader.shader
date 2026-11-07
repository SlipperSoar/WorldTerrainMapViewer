Shader "Custom/EarthSurfaceLitShader"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _HeightTex ("Height Map", 2D) = "white" {}
        _HeightScale ("Height Scale", Range(0, 0.3)) = 0.0
        _Color ("Color", Color) = (1,1,1,1)
        _Specular ("Specular", Color) = (0.2,0.2,0.2,1)
        _Glossiness ("Glossiness", Range(0, 1)) = 0.5

        // ── 步骤 2.0：数据契约贴图 + 渲染参数 ──
        _SurfaceTex ("Surface Data (R=h G=water B=temp A=moist)", 2D) = "black" {}
        _DetailTex ("Detail (RG=normal B=ao A=rough)", 2D) = "black" {}
        _SeaLevel ("Sea Level (m)", Float) = 0
        _SnowLineEquator ("Snow Line Equator (m)", Float) = 0
        _SnowLineMid ("Snow Line Mid (m)", Float) = 0
        _SnowLinePole ("Snow Line Pole (m)", Float) = 0
        _TideAmplitude ("Tide Amplitude (m)", Float) = 2
        _TidePeriod ("Tide Period (days)", Float) = 0.5
        _TideTime ("Tide Time (days)", Float) = 0
        _WaterSurfaceOn ("Water Surface Shading", Range(0, 1)) = 1
        _NormalOn ("Detail Normal", Range(0, 1)) = 1
        _FoamStrength ("Foam Strength", Range(0, 2)) = 0.8
        _WaterSpecular ("Water Specular", Range(0, 2)) = 1.2
    }

    // 注：Fresnel 边缘辉光已移到独立的 Custom/AtmosphereGlowShader 球壳
    // （见 AtmosphereGlow.cs）。放在地表 frag 里会贴着凹凸变形的表面，且被云层盖住。
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #pragma multi_compile _ _TERRAIN_DISPLACEMENT_ON
            // local keyword：C# 侧用 material.EnableKeyword/SetKeyword 设置的是 local keyword。
            // 保留供步骤 2 后续的独立水面 pass / 法线贴图使用（当前地表 pass 尚未消费数据图）。
            #pragma multi_compile_local _ _SURFACE_DATA_ON

            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            #define PI 3.14159265358979

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                UNITY_FOG_COORDS(2)
                float4 vertex : SV_POSITION;
                float3 worldNormal : TEXCOORD3;
                float3 worldPos : TEXCOORD4;
                float3 localPos : TEXCOORD5;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _HeightScale;
            fixed4 _Color;
            fixed4 _Specular;
            float _Glossiness;

            // 步骤 2：水面着色 / 法线 / 潮汐的共享函数与数据图 uniform 都在这里。
            // cginc 自己声明 _SurfaceTex / _DetailTex / _HeightTex 及渲染参数，
            // 所以本 pass 不再重复声明，避免重复定义。
            #include "SurfaceShading.cginc"

            float2 EquirectangularUV(float3 localPos)
            {
                float3 p = normalize(localPos);
                float longitude = atan2(p.x, p.z);
                float latitude = asin(p.y);
                float u = longitude / (2.0 * PI) + 0.5;
                float vCoord = latitude / PI + 0.5;
                return float2(u, 1.0 - vCoord);
            }

            float3 SphereFromUV(float2 uv)
            {
                float lon = (uv.x - 0.5) * 2.0 * PI;
                float lat = (0.5 - uv.y) * PI;
                float cosLat = cos(lat);
                return float3(cosLat * sin(lon), sin(lat), cosLat * cos(lon));
            }

            float GetDisplacedHeight(float2 uv)
            {
                // 水面像素钳到海平面 → 返回 0（水平），陆地用原始高度。
                // SurfaceDisplacementR 返回 0..1 的高度图编码值，映射回 [-1,1]。
                return SurfaceDisplacementR(uv) * 2.0 - 1.0;
            }

            v2f vert (appdata v)
            {
                v2f o;
                o.uv = v.uv;
                o.localPos = v.vertex.xyz;

#if defined(_TERRAIN_DISPLACEMENT_ON)
                float2 heightUV = EquirectangularUV(v.vertex.xyz);

                float h = GetDisplacedHeight(heightUV);
                float radius = length(v.vertex.xyz);
                float3 n = normalize(v.vertex.xyz);
                float3 displaced = v.vertex.xyz + n * h * _HeightScale * radius;

                float du = _HeightTex_TexelSize.x * 4.0;
                float dv = _HeightTex_TexelSize.y * 4.0;

                float2 uvU = float2(heightUV.x + du, heightUV.y);
                float2 uvV = float2(heightUV.x, heightUV.y + dv);

                float3 posU = SphereFromUV(uvU) * radius;
                float hU = GetDisplacedHeight(uvU);
                float3 nU = normalize(posU);
                float3 displacedU = posU + nU * hU * _HeightScale * radius;

                float3 posV = SphereFromUV(uvV) * radius;
                float hV = GetDisplacedHeight(uvV);
                float3 nV = normalize(posV);
                float3 displacedV = posV + nV * hV * _HeightScale * radius;

                float3 tangentU = displacedU - displaced;
                float3 tangentV = displacedV - displaced;
                float3 newNormal = normalize(cross(tangentV, tangentU));

                o.vertex = UnityObjectToClipPos(float4(displaced, 1.0));
                o.worldNormal = UnityObjectToWorldNormal(newNormal);
                o.worldPos = mul(unity_ObjectToWorld, float4(displaced, 1.0)).xyz;
#else
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
#endif
                UNITY_TRANSFER_FOG(o, o.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 数据图用**原始等距圆柱 UV**（不套 _MainTex 的 TRANSFORM_TEX）：
                // 数据图与彩色图始终逐像素对齐，tiling/offset 只影响可见彩色图。
                float2 dataUV = EquirectangularUV(i.localPos);
                float2 finalUV = TRANSFORM_TEX(dataUV, _MainTex);
                fixed4 texColor = tex2D(_MainTex, finalUV);
                fixed4 col = texColor * _Color;

                float3 normal = normalize(i.worldNormal);
                float3 lightDir = normalize(_WorldSpaceLightPos0.xyz);
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);

                float ao = 1.0;
                float roughness = _Glossiness;
                bool isWater = false;

#if defined(_SURFACE_DATA_ON)
                fixed4 surf = tex2D(_SurfaceTex, dataUV);
                float heightMeters = DecodeHeightMeters(surf.r);
                isWater = HasWater(surf.g);

                // 潮汐动态海岸线：涨潮时把「低于当前海平面的邻近陆地」也染成水，
                // 顶点位移仍按静态掩膜钳平，几何不抖动，只有颜色随潮位吞吐。
                if (!isWater && _WaterSurfaceOn > 0.5)
                    isWater = heightMeters < SeaLevelMeters();

                if (isWater && _WaterSurfaceOn > 0.5)
                {
                    // 水面是**水平面**：不套 _detail 的法线 / AO / 粗糙度。
                    // _detail 的法线来自高度场梯度，海洋格是海底负值，贴到水平
                    // 海面上会变成错误的波纹。深度只用颜色区分即可。
                    // 因此这里保持 normal 为球体法线、ao=1、roughness 用缺省值。
                    float foam = 0.0;
                    float3 waterCol = WaterColor(surf.g, heightMeters, foam);
                    col.rgb = lerp(col.rgb, waterCol, 0.92);
                    col.rgb = lerp(col.rgb, float3(0.92, 0.96, 1.0),
                                   saturate(foam * _FoamStrength));
                }
                else
                {
                    // 陆地才用法线贴图：球面 TBN 扰动 + AO + 粗糙度
                    fixed4 detail = tex2D(_DetailTex, dataUV);
                    normal = PerturbWorldNormal(normal, i.worldPos, dataUV, detail.rgb);
                    ao = detail.b;
                    roughness = saturate(1.0 - detail.a);
                }
#endif

                float NdotL = max(0, dot(normal, lightDir));
                float3 halfDir = normalize(lightDir + viewDir);
                float NdotH = max(0, dot(normal, halfDir));

                // 水面高光更强、更锐；陆地沿用材质 _Specular/_Glossiness
                float specPower = _Glossiness * 100.0;
                float3 specColor = _Specular.rgb;
#if defined(_SURFACE_DATA_ON)
                if (isWater && _WaterSurfaceOn > 0.5)
                {
                    specPower = lerp(60.0, 220.0, saturate(roughness));
                    specColor *= _WaterSpecular;
                }
#endif
                float3 specular = pow(NdotH, specPower) * specColor;

                fixed3 ambient = UNITY_LIGHTMODEL_AMBIENT.rgb * col.rgb * ao;
                fixed3 diffuse = col.rgb * _LightColor0.rgb * NdotL;
                fixed3 spec = specular * _LightColor0.rgb;

                // Fresnel 辉光由独立的 AtmosphereGlowShader 球壳渲染，不在这里叠加。
                fixed4 finalColor;
                finalColor.rgb = ambient + diffuse + spec;
                finalColor.a = col.a;

                UNITY_APPLY_FOG(i.fogCoord, finalColor);
                return finalColor;
            }
            ENDCG
        }
        
        Pass
        {
            Tags { "LightMode" = "ForwardAdd" }
            Blend One One
            
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdadd
            #pragma multi_compile_fog
            #pragma multi_compile _ _TERRAIN_DISPLACEMENT_ON
            // 与 ForwardBase 保持一致的 local keyword 集合。ForwardAdd 同样要采样数据图
            // 做水面/法线，否则多光源下水陆光照会不一致；但它是叠加 pass，不加辉光/泡沫。
            #pragma multi_compile_local _ _SURFACE_DATA_ON

            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            #define PI 3.14159265358979

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                UNITY_FOG_COORDS(2)
                float4 vertex : SV_POSITION;
                float3 worldNormal : TEXCOORD3;
                float3 worldPos : TEXCOORD4;
                float3 localPos : TEXCOORD5;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _HeightScale;
            fixed4 _Color;
            fixed4 _Specular;
            float _Glossiness;

            // 与 ForwardBase 共用同一份水面/法线/潮汐逻辑与数据图 uniform
            #include "SurfaceShading.cginc"

            float2 EquirectangularUV(float3 localPos)
            {
                float3 p = normalize(localPos);
                float longitude = atan2(p.x, p.z);
                float latitude = asin(p.y);
                float u = longitude / (2.0 * PI) + 0.5;
                float vCoord = latitude / PI + 0.5;
                return float2(u, 1.0 - vCoord);
            }

            float3 SphereFromUV(float2 uv)
            {
                float lon = (uv.x - 0.5) * 2.0 * PI;
                float lat = (0.5 - uv.y) * PI;
                float cosLat = cos(lat);
                return float3(cosLat * sin(lon), sin(lat), cosLat * cos(lon));
            }

            float GetDisplacedHeight(float2 uv)
            {
                // 水面像素钳到海平面 → 返回 0（水平），陆地用原始高度。
                // SurfaceDisplacementR 返回 0..1 的高度图编码值，映射回 [-1,1]。
                return SurfaceDisplacementR(uv) * 2.0 - 1.0;
            }

            v2f vert (appdata v)
            {
                v2f o;
                o.uv = v.uv;
                o.localPos = v.vertex.xyz;

#if defined(_TERRAIN_DISPLACEMENT_ON)
                float2 heightUV = EquirectangularUV(v.vertex.xyz);

                float h = GetDisplacedHeight(heightUV);
                float radius = length(v.vertex.xyz);
                float3 n = normalize(v.vertex.xyz);
                float3 displaced = v.vertex.xyz + n * h * _HeightScale * radius;

                float du = _HeightTex_TexelSize.x * 4.0;
                float dv = _HeightTex_TexelSize.y * 4.0;

                float2 uvU = float2(heightUV.x + du, heightUV.y);
                float2 uvV = float2(heightUV.x, heightUV.y + dv);

                float3 posU = SphereFromUV(uvU) * radius;
                float hU = GetDisplacedHeight(uvU);
                float3 nU = normalize(posU);
                float3 displacedU = posU + nU * hU * _HeightScale * radius;

                float3 posV = SphereFromUV(uvV) * radius;
                float hV = GetDisplacedHeight(uvV);
                float3 nV = normalize(posV);
                float3 displacedV = posV + nV * hV * _HeightScale * radius;

                float3 tangentU = displacedU - displaced;
                float3 tangentV = displacedV - displaced;
                float3 newNormal = normalize(cross(tangentV, tangentU));

                o.vertex = UnityObjectToClipPos(float4(displaced, 1.0));
                o.worldNormal = UnityObjectToWorldNormal(newNormal);
                o.worldPos = mul(unity_ObjectToWorld, float4(displaced, 1.0)).xyz;
#else
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
#endif
                UNITY_TRANSFER_FOG(o, o.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 dataUV = EquirectangularUV(i.localPos);
                float2 finalUV = TRANSFORM_TEX(dataUV, _MainTex);
                fixed4 texColor = tex2D(_MainTex, finalUV);
                fixed4 col = texColor * _Color;

                float3 normal = normalize(i.worldNormal);
                float ao = 1.0;

#if defined(_SURFACE_DATA_ON)
                fixed4 surf = tex2D(_SurfaceTex, dataUV);
                // 与 ForwardBase 用同一套水色（不含泡沫：叠加 pass 里泡沫会双倍发白）。
                // 潮汐动态海岸线同样在这里生效，保证附加光也照到涨潮淹没的浅滩。
                float heightMeters = DecodeHeightMeters(surf.r);
                bool isWater = HasWater(surf.g);
                if (!isWater && _WaterSurfaceOn > 0.5)
                    isWater = heightMeters < SeaLevelMeters();
                if (isWater && _WaterSurfaceOn > 0.5)
                {
                    // 水面水平：不套 _detail 法线/AO，只用颜色区分深度
                    float foam = 0.0;
                    float3 waterCol = WaterColor(surf.g, heightMeters, foam);
                    col.rgb = lerp(col.rgb, waterCol, 0.92);
                }
                else
                {
                    fixed4 detail = tex2D(_DetailTex, dataUV);
                    normal = PerturbWorldNormal(normal, i.worldPos, dataUV, detail.rgb);
                    ao = detail.b;
                }
#endif

                #ifdef USING_DIRECTIONAL_LIGHT
                    float3 lightDir = normalize(_WorldSpaceLightPos0.xyz);
                #else
                    float3 lightDir = normalize(_WorldSpaceLightPos0.xyz - i.worldPos * _WorldSpaceLightPos0.w);
                #endif

                float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);
                float NdotL = max(0, dot(normal, lightDir));
                float3 halfDir = normalize(lightDir + viewDir);
                float NdotH = max(0, dot(normal, halfDir));
                float3 specular = pow(NdotH, _Glossiness * 100) * _Specular.rgb;

                fixed3 diffuse = col.rgb * _LightColor0.rgb * NdotL * ao;
                fixed3 spec = specular * _LightColor0.rgb;

                fixed4 finalColor;
                finalColor.rgb = diffuse + spec;
                finalColor.a = col.a;

                UNITY_APPLY_FOG(i.fogCoord, finalColor);
                return finalColor;
            }
            ENDCG
        }
    }
    FallBack "Diffuse"
}
