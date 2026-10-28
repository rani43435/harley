// "Room + world": drawn on your real walls before anything else. Invisible itself, it marks those
// pixels (stencil = 1) so Harley's world shows only through the walls. Floor, furniture, ceiling,
// doors and everything else stay your real room (passthrough).
Shader "Harley/Wall Portal"
{
    SubShader
    {
        Tags { "Queue"="Geometry-50" "RenderType"="Opaque" }
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Off
            ColorMask 0
            Stencil { Ref 1 Comp Always Pass Replace }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                return 0;
            }
            ENDCG
        }
    }
}
