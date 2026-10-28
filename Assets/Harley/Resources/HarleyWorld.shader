// Harley's world: simple soft lighting (sun + ambient + glow), with a stencil test so in
// "Room + world" it is drawn only through your walls (_StencilComp = Equal), and everywhere
// in the full world (_StencilComp = Always).
Shader "Harley/World"
{
    Properties
    {
        _BaseColor ("Colour", Color) = (1, 1, 1, 1)
        _EmissionColor ("Glow", Color) = (0, 0, 0, 1)
        _SunDir ("Sun direction (towards the sun)", Vector) = (0.3, 0.4, 0.9, 0)
        _SunColor ("Sun colour", Color) = (1, 0.78, 0.7, 1)
        _Ambient ("Ambient", Color) = (0.42, 0.36, 0.55, 1)
        _Shine ("Shine", Range(0, 1)) = 0.2
        [Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp ("Stencil compare", Float) = 8
    }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Pass
        {
            ZWrite On
            Cull Back
            Stencil { Ref 1 Comp [_StencilComp] }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            fixed4 _BaseColor, _EmissionColor, _SunColor, _Ambient;
            float4 _SunDir;
            float _Shine;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 n : TEXCOORD0; float3 wp : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.n = UnityObjectToWorldNormal(v.normal);
                o.wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 n = normalize(i.n);
                float3 l = normalize(_SunDir.xyz);
                float diff = saturate(dot(n, l)) * 0.85 + 0.15;
                float3 v = normalize(_WorldSpaceCameraPos - i.wp);
                float spec = pow(saturate(dot(reflect(-l, n), v)), 24) * _Shine;
                float rim = pow(1 - saturate(dot(n, v)), 3) * 0.15;
                fixed3 c = _BaseColor.rgb * (_Ambient.rgb + _SunColor.rgb * diff) + _SunColor.rgb * spec + rim * _Ambient.rgb + _EmissionColor.rgb;
                // A little haze far away, so the world melts into the sky
                float fog = saturate((distance(i.wp, _WorldSpaceCameraPos) - 30) / 220);
                c = lerp(c, fixed3(0.85, 0.55, 0.65), fog * 0.6);
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
}
