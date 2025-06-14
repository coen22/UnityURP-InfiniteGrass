Shader "InfiniteGrass/GrassHeightMapShader"
{
    Properties
    {
    }
    SubShader
    {
        Tags { 
            "RenderType"="Opaque"
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                half4 color : COLOR;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 color : TEXCOORD0;
            };

            float2 _BoundsYMinMax;

            float Remap(float In, float2 InMinMax, float2 OutMinMax)
            {
                return OutMinMax.x + (In - InMinMax.x) * (OutMinMax.y - OutMinMax.x) / (InMinMax.y - InMinMax.x);
            }

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);

                float3 worldPos = mul(unity_ObjectToWorld, v.vertex);

                float rChannel = Remap(worldPos.y, _BoundsYMinMax, float2(0, 1)); //We store here the altitude
                float gChannel = v.color.r; //We store here the mask from the RED in the vertex color

                o.color = float2(rChannel, gChannel);

                return o;
            }

            float2 frag (v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }
    }
}
