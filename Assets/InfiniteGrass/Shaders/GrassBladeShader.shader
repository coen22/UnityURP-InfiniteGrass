Shader "InfiniteGrass/GrassBladeShader"
{

    Properties
    {
        _Color("Color", Color) = (0,0,0,1)
        _AOColor("AO Color", Color) = (0.5,0.5,0.5)

        [Header(Grass Shape)][Space]
        _GrassWidth("Grass Width", Float) = 1
        _GrassHeight("Grass Height", Float) = 1
        _GrassWidthRandomness("Grass Width Randomness", Range(0, 1)) = 0.25
        _GrassHeightRandomness("Grass Height Randomness", Range(0, 1)) = 0.5

        _GrassCurving("Grass Curving", Float) = 0.1
        [Space]
        _ExpandDistantGrassWidth("Expand Distant Grass Width", Float) = 1
        _ExpandDistantGrassRange("Expand Distant Grass Range", Vector) = (50, 200, 0, 0)

        [Header(Wind)][Space]
        _WindTexture("Wind Texture", 2D) = "white" {}
        _WindScroll("Wind Scroll", Vector) = (1, 1, 0, 0)
        _WindStrength("Wind Strength", Float) = 1

        [Header(Lighting)][Space]
        _RandomNormal("Random Normal", Range(0, 1)) = 0.1
        _MaxSubdivision("Max Subdivision", Float) = 5
        _SubdivisionDistance("Subdivision Distance", Float) = 100
        _SubdivisionHeightBoost("Subdivision Height Boost", Float) = 0
        _SubdivisionBumpWidth("Subdivision Bump Width", Float) = 20
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue"="Geometry"}

        Pass
        {
            Cull Back
            ZTest Less
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS   : POSITION;
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                half3 color        : COLOR;
                half3 normalWS    : TEXCOORD1;
            };

            CBUFFER_START(UnityPerMaterial)
                half3 _Color;
                half3 _AOColor;

                float _GrassWidth;
                float _GrassHeight;
                float _GrassCurving;
                float _GrassWidthRandomness;
                float _GrassHeightRandomness;

                float _ExpandDistantGrassWidth;
                float2 _ExpandDistantGrassRange;

                float4 _WindTexture_ST;
                float _WindStrength;
                float2 _WindScroll;

                half _RandomNormal;

                float2 _CenterPos;
                float  _PrevCameraY;

                float _DrawDistance;
                float _TextureUpdateThreshold;
                float _MaxSubdivision;
                float _SubdivisionDistance;
                float _SubdivisionHeightBoost;
                float _SubdivisionBumpWidth;

                StructuredBuffer<float4> _GrassPositions;

            CBUFFER_END

            sampler2D _WindTexture;

            sampler2D _GrassColorRT;
            sampler2D _GrassSlopeRT;

            half3 ApplySingleDirectLight(Light light, half3 N, half3 V, half3 albedo, half mask, half positionY)
            {
                half3 H = normalize(light.direction + V);

                half directDiffuse = dot(N, light.direction) * 0.5 + 0.5;

                float directSpecular = saturate(dot(N,H));
                directSpecular *= directSpecular;
                directSpecular *= directSpecular;
                directSpecular *= directSpecular;
                directSpecular *= directSpecular;

                directSpecular *= positionY * 0.12;

                half3 lighting = light.color * (light.shadowAttenuation * light.distanceAttenuation);
                half3 result = (albedo * directDiffuse + directSpecular * (1-mask)) * lighting;

                return result; 
            }

            uint murmurHash3(float input) {
                uint h = abs(input);
                h ^= h >> 16;
                h *= 0x85ebca6b;
                h ^= h >> 13;
                h *= 0xc2b2ae3d;
                h ^= h >> 16;
                return h;
            }

            float random(float input) {
                return murmurHash3(input) / 4294967295.0;
            }

            float srandom(float input) {
                return (murmurHash3(input) / 4294967295.0) * 2 - 1;
            }

            float Remap(float In, float2 InMinMax, float2 OutMinMax)
            {
                return OutMinMax.x + (In - InMinMax.x) * (OutMinMax.y - OutMinMax.x) / (InMinMax.y - InMinMax.x);
            }

            float3 CalculateLighting(float3 albedo, float3 positionWS, float3 N, float3 V, float mask, float positionY){

                half3 result = SampleSH(0) * albedo;

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(positionWS));
                result += ApplySingleDirectLight(mainLight, N, V, albedo, mask, positionY);

                int additionalLightsCount = GetAdditionalLightsCount();
                for (int i = 0; i < additionalLightsCount; ++i)
                {
                    Light light = GetAdditionalLight(i, positionWS);
                    result += ApplySingleDirectLight(light, N, V, albedo, mask, positionY);
                }

                return result;
            }

            Varyings vert(Attributes IN, uint instanceID : SV_InstanceID)
            {
                Varyings OUT;

                float4 positionData = _GrassPositions[instanceID];
                float3 pivot = positionData.xyz;
                float distanceFromCamera = positionData.w;
                float oldDiff = pivot.y - _PrevCameraY;
                float newDiff = pivot.y - _WorldSpaceCameraPos.y;
                float newDistSq = distanceFromCamera * distanceFromCamera - oldDiff * oldDiff + newDiff * newDiff;
                distanceFromCamera = sqrt(max(newDistSq, 0.0));

                float2 uv = (pivot.xz - _CenterPos) / (_DrawDistance + _TextureUpdateThreshold);
                uv = uv * 0.5 + 0.5;

                float lodSubdiv = floor(_MaxSubdivision * saturate(1 - distanceFromCamera / _SubdivisionDistance));
                float step = 1.0 / (lodSubdiv + 1);
                float quantizedY = round(IN.positionOS.y / step) * step;

                float grassWidth = _GrassWidth * (1 - random(pivot.x * 950 + pivot.z * 10) * _GrassWidthRandomness);

                //distanceFromCamera is provided per instance from the compute shader
                //Expand the grass width based on the distance from camera
                grassWidth += saturate(Remap(distanceFromCamera, float2(_ExpandDistantGrassRange.x, _ExpandDistantGrassRange.y), float2(0, 1))) * _ExpandDistantGrassWidth;
                grassWidth *= (1 - quantizedY);

                //Grass Height
                float grassHeight = _GrassHeight * (1 - random(pivot.x * 230 + pivot.z * 10) * _GrassHeightRandomness);
                float diff = abs(distanceFromCamera - _SubdivisionDistance);
                float bumpFactor = saturate(1 - diff / _SubdivisionBumpWidth);
                grassHeight *= (1 + _SubdivisionHeightBoost * bumpFactor);
                
                //Billboard Logic
                float3 cameraTransformRightWS = UNITY_MATRIX_V[0].xyz;
                float3 cameraTransformUpWS = UNITY_MATRIX_V[1].xyz;
                float3 cameraTransformForwardWS = -UNITY_MATRIX_V[2].xyz;

                float4 slope = tex2Dlod(_GrassSlopeRT, float4(uv, 0, 0));
                float xSlope = slope.r * 2 - 1;
                float zSlope = slope.g * 2 - 1;

                float3 slopeDirection = normalize(float3(xSlope, 1 - (max(abs(xSlope), abs(zSlope)) * 0.5), zSlope));//Direction reconstructed from the slope texture
                float3 bladeDirection = normalize(lerp(float3(0, 1, 0), slopeDirection, slope.a));//The original direction is upward

                half3 windTex = tex2Dlod(_WindTexture, float4(TRANSFORM_TEX(pivot.xz, _WindTexture) + _WindScroll * _Time.y,0,0));
                float2 wind = (windTex.rg * 2 - 1) * _WindStrength * (1-slope.a);

                bladeDirection.xz += wind * quantizedY;//Adding wind and multiplying with the Y position to affect the tip only

                bladeDirection = normalize(bladeDirection);
                
                float3 rightTangent = normalize(cross(bladeDirection, cameraTransformForwardWS));//The direction we gonna stretch the blade

                float3 positionOS = bladeDirection * quantizedY * grassHeight
                                    + rightTangent * IN.positionOS.x * grassWidth;//This insures that the blade is always facing the camera

                positionOS.xz += (quantizedY * quantizedY) * float2(srandom(pivot.x * 851 + pivot.z * 10), srandom(pivot.z * 647 + pivot.x * 10)) * _GrassCurving;
                //Adds a bit of curving to grass blade

                //posOS -> posWS
                float3 positionWS = positionOS + pivot;
                
                //posWS -> posCS
                OUT.positionCS = TransformWorldToHClip(positionWS);
                
                half3 albedo = lerp(_AOColor, _Color, quantizedY);

                float4 color = tex2Dlod(_GrassColorRT, float4(uv, 0, 0));
                albedo = lerp(albedo, color.rgb, color.a);

                //Lighting Stuff
                half3 N = normalize(bladeDirection + cameraTransformForwardWS * -0.5 + _RandomNormal * half3(srandom(pivot.x * 314 + pivot.z * 10), 0, srandom(pivot.z * 677 + pivot.x * 10)));
                //The normal vector is just the blade direction tilted a bit towards the camera with a bit of randomness
                half3 V = normalize(_WorldSpaceCameraPos - positionWS);

                float3 lighting = CalculateLighting(albedo, positionWS, N, V, color.a, quantizedY);
                //I'm also passing the Alpha Channel of the Color Map cause I dont want the blades that are affected with color to receive specular light
                //The main use of the color map for me is burning the grass and the burned grass should not receive specular light

                float fogFactor = ComputeFogFactor(OUT.positionCS.z);
                OUT.color.rgb = MixFog(lighting, fogFactor);
                OUT.normalWS = N;

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                Light ml = GetMainLight();
                half NdotL = dot(normalize(IN.normalWS), ml.direction);
                if (NdotL < -0.5)
                    discard;
                return half4(IN.color.rgb,1);
            }
            ENDHLSL
        }
    }
}