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
    }
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

            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float2 finalUV : TEXCOORD1;
                UNITY_FOG_COORDS(2)
                float4 vertex : SV_POSITION;
                float3 worldNormal : TEXCOORD3;
                float3 worldPos : TEXCOORD4;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            sampler2D _HeightTex;
            float4 _HeightTex_TexelSize;
            float _HeightScale;
            fixed4 _Color;
            fixed4 _Specular;
            float _Glossiness;

            float2 EquirectangularUV(float3 localPos)
            {
                float3 p = normalize(localPos);
                float longitude = atan2(p.x, p.z);
                float latitude = asin(p.y);
                float u = longitude / (2.0 * 3.14159265358979) + 0.5;
                float vCoord = latitude / 3.14159265358979 + 0.5;
                return float2(u, 1.0 - vCoord);
            }

            float3 SphereFromUV(float2 uv)
            {
                float lon = (uv.x - 0.5) * 2.0 * 3.14159265358979;
                float lat = (0.5 - uv.y) * 3.14159265358979;
                float cosLat = cos(lat);
                return float3(cosLat * cos(lon), sin(lat), cosLat * sin(lon));
            }

            float GetDisplacedHeight(float2 uv)
            {
                return tex2Dlod(_HeightTex, float4(uv, 0, 0)).r * 2.0 - 1.0;
            }

            v2f vert (appdata v)
            {
                v2f o;

                float2 heightUV = EquirectangularUV(v.vertex.xyz);
                o.finalUV = heightUV;

                float h = GetDisplacedHeight(heightUV);
                float radius = length(v.vertex.xyz);
                float3 n = normalize(v.vertex.xyz);
                float3 displaced = v.vertex.xyz + n * h * _HeightScale * radius;

                float du = _HeightTex_TexelSize.x * 4.0;
                float dv = _HeightTex_TexelSize.y * 4.0;

                float2 uvU = float2(heightUV.x + du, heightUV.y);
                float2 uvV = float2(heightUV.x, heightUV.y + dv);

                float3 posU = SphereFromUV(uvU);
                float hU = GetDisplacedHeight(uvU);
                float3 nU = normalize(posU);
                float3 displacedU = posU + nU * hU * _HeightScale * length(posU);

                float3 posV = SphereFromUV(uvV);
                float hV = GetDisplacedHeight(uvV);
                float3 nV = normalize(posV);
                float3 displacedV = posV + nV * hV * _HeightScale * length(posV);

                float3 tangentU = displacedU - displaced;
                float3 tangentV = displacedV - displaced;
                float3 newNormal = normalize(cross(tangentV, tangentU));

                o.vertex = UnityObjectToClipPos(float4(displaced, 1.0));
                o.uv = v.uv;
                o.worldNormal = UnityObjectToWorldNormal(newNormal);
                o.worldPos = mul(unity_ObjectToWorld, float4(displaced, 1.0)).xyz;
                UNITY_TRANSFER_FOG(o, o.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 finalUV = TRANSFORM_TEX(i.finalUV, _MainTex);
                fixed4 texColor = tex2D(_MainTex, finalUV);
                fixed4 col = texColor * _Color;
                
                float3 normal = normalize(i.worldNormal);
                float3 lightDir = normalize(_WorldSpaceLightPos0.xyz);
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);
                
                float NdotL = max(0, dot(normal, lightDir));
                
                float3 halfDir = normalize(lightDir + viewDir);
                float NdotH = max(0, dot(normal, halfDir));
                float specular = pow(NdotH, _Glossiness * 100) * _Specular.rgb;
                
                fixed3 ambient = UNITY_LIGHTMODEL_AMBIENT.rgb * col.rgb;
                fixed3 diffuse = col.rgb * _LightColor0.rgb * NdotL;
                fixed3 spec = specular * _LightColor0.rgb;
                
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

            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float2 finalUV : TEXCOORD1;
                UNITY_FOG_COORDS(2)
                float4 vertex : SV_POSITION;
                float3 worldNormal : TEXCOORD3;
                float3 worldPos : TEXCOORD4;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            sampler2D _HeightTex;
            float4 _HeightTex_TexelSize;
            float _HeightScale;
            fixed4 _Color;
            fixed4 _Specular;
            float _Glossiness;

            float2 EquirectangularUV(float3 localPos)
            {
                float3 p = normalize(localPos);
                float longitude = atan2(p.x, p.z);
                float latitude = asin(p.y);
                float u = longitude / (2.0 * 3.14159265358979) + 0.5;
                float vCoord = latitude / 3.14159265358979 + 0.5;
                return float2(u, 1.0 - vCoord);
            }

            float3 SphereFromUV(float2 uv)
            {
                float lon = (uv.x - 0.5) * 2.0 * 3.14159265358979;
                float lat = (0.5 - uv.y) * 3.14159265358979;
                float cosLat = cos(lat);
                return float3(cosLat * cos(lon), sin(lat), cosLat * sin(lon));
            }

            float GetDisplacedHeight(float2 uv)
            {
                return tex2Dlod(_HeightTex, float4(uv, 0, 0)).r * 2.0 - 1.0;
            }

            v2f vert (appdata v)
            {
                v2f o;

                float2 heightUV = EquirectangularUV(v.vertex.xyz);
                o.finalUV = heightUV;

                float h = GetDisplacedHeight(heightUV);
                float radius = length(v.vertex.xyz);
                float3 n = normalize(v.vertex.xyz);
                float3 displaced = v.vertex.xyz + n * h * _HeightScale * radius;

                float du = _HeightTex_TexelSize.x * 4.0;
                float dv = _HeightTex_TexelSize.y * 4.0;

                float2 uvU = float2(heightUV.x + du, heightUV.y);
                float2 uvV = float2(heightUV.x, heightUV.y + dv);

                float3 posU = SphereFromUV(uvU);
                float hU = GetDisplacedHeight(uvU);
                float3 nU = normalize(posU);
                float3 displacedU = posU + nU * hU * _HeightScale * length(posU);

                float3 posV = SphereFromUV(uvV);
                float hV = GetDisplacedHeight(uvV);
                float3 nV = normalize(posV);
                float3 displacedV = posV + nV * hV * _HeightScale * length(posV);

                float3 tangentU = displacedU - displaced;
                float3 tangentV = displacedV - displaced;
                float3 newNormal = normalize(cross(tangentV, tangentU));

                o.vertex = UnityObjectToClipPos(float4(displaced, 1.0));
                o.uv = v.uv;
                o.worldNormal = UnityObjectToWorldNormal(newNormal);
                o.worldPos = mul(unity_ObjectToWorld, float4(displaced, 1.0)).xyz;
                UNITY_TRANSFER_FOG(o, o.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 finalUV = TRANSFORM_TEX(i.finalUV, _MainTex);
                fixed4 texColor = tex2D(_MainTex, finalUV);
                fixed4 col = texColor * _Color;
                
                float3 normal = normalize(i.worldNormal);
                
                #ifdef USING_DIRECTIONAL_LIGHT
                    float3 lightDir = normalize(_WorldSpaceLightPos0.xyz);
                #else
                    float3 lightDir = normalize(_WorldSpaceLightPos0.xyz - i.worldPos * _WorldSpaceLightPos0.w);
                #endif
                
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);
                
                float NdotL = max(0, dot(normal, lightDir));
                
                float3 halfDir = normalize(lightDir + viewDir);
                float NdotH = max(0, dot(normal, halfDir));
                float specular = pow(NdotH, _Glossiness * 100) * _Specular.rgb;
                
                fixed3 diffuse = col.rgb * _LightColor0.rgb * NdotL;
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
