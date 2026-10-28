// Harley's world sky: a soft dusk gradient with a glowing sun and twinkling stars.
// Drawn on a big sphere around the player (inside faces), behind everything.
Shader "Harley/Sky"
{
    Properties
    {
        _Top ("Top", Color) = (0.08, 0.06, 0.22, 1)
        _Horizon ("Horizon", Color) = (0.95, 0.55, 0.65, 1)
        _Bottom ("Bottom", Color) = (0.12, 0.08, 0.2, 1)
        _SunDir ("Sun direction", Vector) = (0.3, 0.12, 0.95, 0)
        _SunColor ("Sun", Color) = (1, 0.8, 0.6, 1)
        _Stars ("Stars", Range(0, 1)) = 0.7
        [Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp ("Stencil compare", Float) = 8
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Pass
        {
            Cull Front
            ZWrite Off
            Stencil { Ref 1 Comp [_StencilComp] }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            fixed4 _Top, _Horizon, _Bottom, _SunColor;
            float4 _SunDir;
            float _Stars;

            struct appdata { float4 vertex : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = normalize(mul((float3x3)unity_ObjectToWorld, v.vertex.xyz));
                return o;
            }

            float hash(float3 p) { p = frac(p * 0.3183099 + 0.1); p *= 17.0; return frac(p.x * p.y * p.z * (p.x + p.y + p.z)); }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 d = normalize(i.dir);
                float y = d.y;
                fixed3 col = y > 0 ? lerp(_Horizon.rgb, _Top.rgb, pow(saturate(y), 0.55)) : lerp(_Horizon.rgb, _Bottom.rgb, pow(saturate(-y * 4), 0.6));

                // sun and its glow
                float s = saturate(dot(d, normalize(_SunDir.xyz)));
                col += _SunColor.rgb * (pow(s, 600) * 2.0 + pow(s, 12) * 0.35);

                // stars, only high in the sky
                float3 cell = floor(d * 220);
                float star = step(0.9975, hash(cell)) * saturate(y * 3 - 0.3) * _Stars;
                float tw = 0.6 + 0.4 * sin(_Time.y * 2 + hash(cell + 3) * 40);
                col += star * tw;
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
