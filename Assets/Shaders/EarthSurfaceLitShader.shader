Shader "Custom/EarthSurfaceLitShader"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
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
                UNITY_FOG_COORDS(1)
                float4 vertex : SV_POSITION;
                float3 localPos : TEXCOORD2;
                float3 worldNormal : TEXCOORD3;
                float3 worldPos : TEXCOORD4;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed4 _Specular;
            float _Glossiness;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.localPos = v.vertex.xyz;
                o.uv = v.uv;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                UNITY_TRANSFER_FOG(o,o.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 pos = normalize(i.localPos);
                
                float longitude = atan2(pos.x, pos.z);
                float latitude = asin(pos.y);
                
                float u = longitude / (2.0 * 3.14159265358979) + 0.5;
                float v_coord = latitude / 3.14159265358979 + 0.5;
                
                float2 finalUV = float2(u, 1.0 - v_coord);
                finalUV = TRANSFORM_TEX(finalUV, _MainTex);
                
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
                UNITY_FOG_COORDS(1)
                float4 vertex : SV_POSITION;
                float3 localPos : TEXCOORD2;
                float3 worldNormal : TEXCOORD3;
                float3 worldPos : TEXCOORD4;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed4 _Specular;
            float _Glossiness;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.localPos = v.vertex.xyz;
                o.uv = v.uv;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                UNITY_TRANSFER_FOG(o,o.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 pos = normalize(i.localPos);
                
                float longitude = atan2(pos.x, pos.z);
                float latitude = asin(pos.y);
                
                float u = longitude / (2.0 * 3.14159265358979) + 0.5;
                float v_coord = latitude / 3.14159265358979 + 0.5;
                
                float2 finalUV = float2(u, 1.0 - v_coord);
                finalUV = TRANSFORM_TEX(finalUV, _MainTex);
                
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
