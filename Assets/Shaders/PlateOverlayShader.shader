Shader "Custom/PlateOverlayShader"
{
    Properties
    {
        _MainTex ("Plate Texture", 2D) = "white" {}
        _Color ("Color Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        LOD 100

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "UnityCG.cginc"

            #define PI 3.14159265358979

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 localPos : TEXCOORD0;
                UNITY_FOG_COORDS(1)
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;

            float2 EquirectangularUV(float3 localPos)
            {
                float3 p = normalize(localPos);
                float longitude = atan2(p.x, p.z);
                float latitude = asin(p.y);
                float u = longitude / (2.0 * PI) + 0.5;
                float vCoord = latitude / PI + 0.5;
                return float2(u, 1.0 - vCoord);
            }

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.localPos = v.vertex.xyz;
                UNITY_TRANSFER_FOG(o, o.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = EquirectangularUV(i.localPos);
                uv = TRANSFORM_TEX(uv, _MainTex);
                fixed4 col = tex2D(_MainTex, uv);
                col *= _Color;
                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }
            ENDCG
        }
    }
}
