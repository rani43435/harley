// Soft coloured light around a screen (Ambilight). The quad is bigger than the screen; inside the screen's
// rectangle it's empty, outside it fades out. Each side takes the colour of that edge of the picture.
Shader "Harley/Ambient Glow"
{
    Properties
    {
        _ColorL ("Left", Color) = (1,1,1,1)
        _ColorR ("Right", Color) = (1,1,1,1)
        _ColorT ("Top", Color) = (1,1,1,1)
        _ColorB ("Bottom", Color) = (1,1,1,1)
        _Inner ("Screen size in the quad (x, y)", Vector) = (0.6, 0.6, 0, 0)
        _Falloff ("Falloff", Float) = 6
        _Intensity ("Intensity", Float) = 1
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            float4 _ColorL, _ColorR, _ColorT, _ColorB, _Inner;
            float _Falloff, _Intensity;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float2 p = i.uv - 0.5;                 // -0.5..0.5
                float2 inner = _Inner.xy * 0.5;
                float2 d = max(abs(p) - inner, 0);      // distance outside the screen
                float dist = length(d) / max(0.0001, 0.5 - max(inner.x, inner.y));
                float a = exp(-dist * _Falloff) * step(0.0001, dist + 0.0001);
                a *= saturate(1 - dist);                // reach 0 at the quad's edge
                // colour from the nearest side(s)
                float wx = abs(p.x) / max(0.0001, abs(p.x) + abs(p.y));
                float3 cx = lerp(_ColorL.rgb, _ColorR.rgb, saturate(i.uv.x));
                float3 cy = lerp(_ColorB.rgb, _ColorT.rgb, saturate(i.uv.y));
                float3 c = lerp(cy, cx, wx);
                return float4(c, a * _Intensity);
            }
            ENDCG
        }
    }
}
