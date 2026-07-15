Shader "Hidden/BlendShare/FbxPreviewWire"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _SrcBlend ("Src Blend", Float) = 5
        _DstBlend ("Dst Blend", Float) = 10
        _Cull ("Cull", Float) = 0
        _ZWrite ("ZWrite", Float) = 0
        _ZTest ("ZTest", Float) = 4
        _LineWidth ("Line Width", Float) = 1
        _DepthOnly ("Depth Only", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            Blend [_SrcBlend] [_DstBlend]
            Cull [_Cull]
            ZWrite [_ZWrite]
            ZTest [_ZTest]
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _LineWidth;
            float _DepthOnly;
            struct appdata
            {
                float4 vertex : POSITION;
                float3 barycentric : TEXCOORD0;
                float3 edgeMask : TEXCOORD1;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 barycentric : TEXCOORD0;
                float3 edgeMask : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.barycentric = v.barycentric;
                o.edgeMask = v.edgeMask;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (_DepthOnly > 0.5)
                {
                    return fixed4(0, 0, 0, 0);
                }

                float3 edgeWidth = max(fwidth(i.barycentric) * _LineWidth, 0.000001);
                float3 edgeCoverage = 1.0 - smoothstep(0.0, edgeWidth, i.barycentric);
                float coverage = max(
                    edgeCoverage.x * i.edgeMask.x,
                    max(edgeCoverage.y * i.edgeMask.y, edgeCoverage.z * i.edgeMask.z));
                clip(coverage - 0.001);
                return fixed4(_Color.rgb, _Color.a * coverage);
            }
            ENDCG
        }
    }
}
