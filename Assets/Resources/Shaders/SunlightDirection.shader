Shader "Custom/SunlightDirection"
{
    Properties
    {
        _MainTex ("Arrow Texture", 2D) = "white" {}
        _Color ("Tint Color", Color) = (1, 0.82, 0.3, 0.6)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        LOD 100

        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, i.uv);
                // Use texture alpha channel for arrow shape (transparent = invisible, opaque = arrow)
                fixed alpha = tex.a * _Color.a;
                return fixed4(_Color.rgb, alpha);
            }
            ENDCG
        }
    }
}
