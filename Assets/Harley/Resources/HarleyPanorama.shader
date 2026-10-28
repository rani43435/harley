// A 360 photo (equirectangular) drawn on a big sphere around you. In "Room + world" it shows only
// through your walls (stencil from Harley/Wall Portal), so the walls look like windows onto the view.
Shader "Harley/Panorama"
{
    Properties
    {
        _MainTex ("360 view", 2D) = "gray" {}
        _Yaw ("Turn (degrees)", Float) = 0
        _Exposure ("Brightness", Range(0.3, 2)) = 1
        [Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp ("Stencil compare", Float) = 3
    }
    SubShader
    {
        Tags { "Queue"="Geometry+450" "RenderType"="Opaque" }
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

            sampler2D _MainTex;
            float _Yaw, _Exposure;

            struct appdata { float4 vertex : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = mul((float3x3)unity_ObjectToWorld, v.vertex.xyz);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 d = normalize(i.dir);
                float lon = atan2(d.x, d.z) + radians(_Yaw);
                float lat = asin(clamp(d.y, -1, 1));
                float2 uv = float2(frac(0.5 + lon / (2 * UNITY_PI)), 0.5 + lat / UNITY_PI);
                // explicit gradient: no seam line where longitude wraps
                float2 uvc = float2(frac(uv.x + 0.5), uv.y);
                float2 dx = ddx(uv), dy = ddy(uv), dxc = ddx(uvc), dyc = ddy(uvc);
                if (dot(dxc, dxc) + dot(dyc, dyc) < dot(dx, dx) + dot(dy, dy)) { dx = dxc; dy = dyc; }
                fixed3 c = tex2Dgrad(_MainTex, uv, dx, dy).rgb * _Exposure;
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
}
