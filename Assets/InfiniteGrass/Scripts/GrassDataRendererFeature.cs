using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class GrassDataRendererFeature : ScriptableRendererFeature
{
    [SerializeField] private LayerMask heightMapLayer;
    [SerializeField] private Material heightMapMat;
    [SerializeField] private ComputeShader computeShader;
    [SerializeField] private ComputeShader hizShader;

    GrassDataPass grassDataPass;

    public override void Create()
    {
        grassDataPass = new GrassDataPass(heightMapLayer, heightMapMat, computeShader, hizShader);
        grassDataPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(grassDataPass);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            grassDataPass.Dispose();
        }
    }

    private class GrassDataPass : ScriptableRenderPass
    {
        private List<ShaderTagId> shaderTagsList = new List<ShaderTagId>();

        private RTHandle heightRT;
        private RTHandle heightDepthRT;
        private RTHandle maskRT;
        private RTHandle colorRT;
        private RTHandle slopeRT;
        private RTHandle hiZRT;
        private int hiZMipCount;
        private Vector2Int hiZSize;
        private int hizInitKernel = -1;
        private int hizDownKernel = -1;

        private LayerMask heightMapLayer;
        private Material heightMapMat;

        private ComputeShader computeShader;
        private ComputeShader hizShader;

        public GrassDataPass(LayerMask heightMapLayer, Material heightMapMat, ComputeShader computeShader, ComputeShader hizShader)
        {
            this.heightMapLayer = heightMapLayer;
            this.computeShader = computeShader;
            this.hizShader = hizShader;
            this.heightMapMat = heightMapMat;

            shaderTagsList.Add(new ShaderTagId("SRPDefaultUnlit"));
            shaderTagsList.Add(new ShaderTagId("UniversalForward"));
            shaderTagsList.Add(new ShaderTagId("UniversalForwardOnly"));
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            int textureSize = 2048;
            RenderingUtils.ReAllocateIfNeeded(ref heightRT, new RenderTextureDescriptor(textureSize, textureSize, RenderTextureFormat.RGFloat, 0), FilterMode.Bilinear);
            RenderingUtils.ReAllocateIfNeeded(ref heightDepthRT, new RenderTextureDescriptor(textureSize, textureSize, RenderTextureFormat.RFloat, 32), FilterMode.Bilinear);
            RenderingUtils.ReAllocateIfNeeded(ref maskRT, new RenderTextureDescriptor(textureSize, textureSize, RenderTextureFormat.RFloat, 0), FilterMode.Bilinear);
            RenderingUtils.ReAllocateIfNeeded(ref colorRT, new RenderTextureDescriptor(textureSize, textureSize, RenderTextureFormat.ARGBFloat, 0), FilterMode.Bilinear);
            RenderingUtils.ReAllocateIfNeeded(ref slopeRT, new RenderTextureDescriptor(textureSize, textureSize, RenderTextureFormat.ARGBFloat, 0), FilterMode.Bilinear);
            if (hizShader != null && hizShader.HasKernel("InitDepth") && hizShader.HasKernel("Downsample"))
            {
                int width = renderingData.cameraData.camera.pixelWidth;
                int height = renderingData.cameraData.camera.pixelHeight;
                hiZSize = new Vector2Int(width, height);
                hiZMipCount = (int)Mathf.Log(Mathf.Max(width, height), 2) + 1;
                RenderTextureDescriptor hizDesc = new RenderTextureDescriptor(width, height, RenderTextureFormat.RFloat, 0)
                {
                    useMipMap = true,
                    autoGenerateMips = false,
                    enableRandomWrite = true,
                    mipCount = hiZMipCount
                };
                RenderingUtils.ReAllocateIfNeeded(ref hiZRT, hizDesc, FilterMode.Point);
                hizInitKernel = hizShader.FindKernel("InitDepth");
                hizDownKernel = hizShader.FindKernel("Downsample");

                if (hizInitKernel < 0 || hizDownKernel < 0)
                {
                    Debug.LogWarning("Hi-Z compute shader kernels not found");
                    hiZRT?.Release();
                    hiZRT = CreateFallbackHiZ();
                    hiZSize = new Vector2Int(1, 1);
                    hiZMipCount = 0;
                    hizInitKernel = -1;
                    hizDownKernel = -1;
                }
            }
            else
            {
                hiZMipCount = 0;
                hiZRT?.Release();
                hiZRT = CreateFallbackHiZ();
                hiZSize = new Vector2Int(1, 1);
                hizInitKernel = -1;
                hizDownKernel = -1;
            }

            ConfigureTarget(heightRT, heightDepthRT);
            ConfigureClear(ClearFlag.All, Color.black);
        }

        ComputeBuffer grassPositionsBuffer;

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            //Now to render the textures we need we have two ways :
            //- Having a second camera in our scene that is looking from above and renders the necessary data (which is expensive)
            //- Manipulating the actuall main camera to render objects from above by changing the view and projection matricies (which is faster and the one I'm using here)
            //I took this technic from Colin Leung (NiloCat) repo
            //You can check it here (more detailed): https://github.com/ColinLeung-NiloCat/UnityURP-MobileDrawMeshInstancedIndirectExample/blob/master/Assets/URPMobileGrassInstancedIndirectDemo/InstancedIndirectGrass/Core/GrassBending/GrassBendingRTPrePass.cs

            if (InfiniteGrassRenderer.instance == null || heightMapMat == null || computeShader == null)
                return;

            CommandBuffer cmd = CommandBufferPool.Get();

            float spacing = InfiniteGrassRenderer.instance.spacing;
            float fullDensityDistance = InfiniteGrassRenderer.instance.fullDensityDistance;
            float drawDistance = InfiniteGrassRenderer.instance.drawDistance;
            float maxBufferCount = InfiniteGrassRenderer.instance.maxBufferCount;
            float densityFalloffExponent = InfiniteGrassRenderer.instance.densityFalloffExponent;
            float textureUpdateThreshold = InfiniteGrassRenderer.instance.textureUpdateThreshold;

            Bounds cameraBounds = CalculateCameraBounds(Camera.main, drawDistance);

            //First thing is to calculate the new position of the camera
            //The "centerPos" refer to the XZ position of the camera while the Y position is the max.y of the calculated bounds
            //You can see that we are moving the camera in steps, cause we want the textures to not get updated until we move a certain threshold
            //if we let the camera move a lot we gonna have instability issues and a lot of flikering so we try to minimize that as much as possible
            Vector2 centerPos = new Vector2(Mathf.Floor(Camera.main.transform.position.x / textureUpdateThreshold) * textureUpdateThreshold, Mathf.Floor(Camera.main.transform.position.z / textureUpdateThreshold) * textureUpdateThreshold);

            Matrix4x4 viewMatrix = Matrix4x4.TRS(new Vector3(centerPos.x, cameraBounds.max.y, centerPos.y), Quaternion.LookRotation(-Vector3.up), new Vector3(1, 1, -1)).inverse;
            Matrix4x4 projectionMatrix = Matrix4x4.Ortho(-(drawDistance + textureUpdateThreshold), drawDistance + textureUpdateThreshold, -(drawDistance + textureUpdateThreshold), drawDistance + textureUpdateThreshold, 0, cameraBounds.size.y);

            cmd.SetViewProjectionMatrices(viewMatrix, projectionMatrix);//Update the camera marticies

            using (new ProfilingScope(cmd, new ProfilingSampler("Grass Height Map RT")))
            {
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                //Replace the material of the objects with the "heightMapLayer" and render them
                var drawSetting = CreateDrawingSettings(shaderTagsList, ref renderingData, renderingData.cameraData.defaultOpaqueSortFlags);
                heightMapMat.SetVector("_BoundsYMinMax", new Vector2(cameraBounds.min.y, cameraBounds.max.y));
                drawSetting.overrideMaterial = heightMapMat;
                var filterSetting = new FilteringSettings(RenderQueueRange.all, heightMapLayer);
                context.DrawRenderers(renderingData.cullResults, ref drawSetting, ref filterSetting);
            }

            cmd.SetRenderTarget(maskRT);//Change the texture we are drawing to
            cmd.ClearRenderTarget(true, true, new Color(0, 0, 0, 0));//Clear it before drawing to it

            using (new ProfilingScope(cmd, new ProfilingSampler("Grass Mask RT")))
            {
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
                 
                var drawSetting = CreateDrawingSettings(new ShaderTagId("GrassMask"), ref renderingData, SortingCriteria.CommonTransparent);
                var filterSetting = new FilteringSettings(RenderQueueRange.all);
                context.DrawRenderers(renderingData.cullResults, ref drawSetting, ref filterSetting);
            }

            cmd.SetRenderTarget(colorRT);
            cmd.ClearRenderTarget(true, true, new Color(0, 0, 0, 0));

            using (new ProfilingScope(cmd, new ProfilingSampler("Grass Color RT")))
            {
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                var drawSetting = CreateDrawingSettings(new ShaderTagId("GrassColor"), ref renderingData, SortingCriteria.CommonTransparent);
                var filterSetting = new FilteringSettings(RenderQueueRange.all);
                context.DrawRenderers(renderingData.cullResults, ref drawSetting, ref filterSetting);
            }

            cmd.SetRenderTarget(slopeRT);
            cmd.ClearRenderTarget(true, true, new Color(0, 0, 0, 0));

            using (new ProfilingScope(cmd, new ProfilingSampler("Grass Slope RT")))
            {
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                var drawSetting = CreateDrawingSettings(new ShaderTagId("GrassSlope"), ref renderingData, SortingCriteria.CommonTransparent);
                var filterSetting = new FilteringSettings(RenderQueueRange.all);
                context.DrawRenderers(renderingData.cullResults, ref drawSetting, ref filterSetting);
            }

            cmd.SetGlobalTexture("_GrassColorRT", colorRT);//Set the COLOR and SLOPE textures as global
            cmd.SetGlobalTexture("_GrassSlopeRT", slopeRT);

            //Finally we reset the camera matricies to the original ones
            cmd.SetViewProjectionMatrices(renderingData.cameraData.camera.worldToCameraMatrix, renderingData.cameraData.camera.projectionMatrix);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            // Generate Hi-Z depth pyramid for occlusion culling
            if (hizShader != null && hiZRT != null && hizInitKernel >= 0 && hizDownKernel >= 0)
            {
                int width = renderingData.cameraData.camera.pixelWidth;
                int height = renderingData.cameraData.camera.pixelHeight;

                cmd.SetComputeTextureParam(hizShader, hizInitKernel, "_CameraDepthTexture", renderingData.cameraData.renderer.cameraDepthTargetHandle);
                cmd.SetComputeTextureParam(hizShader, hizInitKernel, "_HiZTexture", hiZRT, 0);
                cmd.SetComputeIntParams(hizShader, "_Size", new int[] { width, height });
                cmd.DispatchCompute(hizShader, hizInitKernel, Mathf.CeilToInt(width / 8f), Mathf.CeilToInt(height / 8f), 1);

                for (int m = 1; m < hiZMipCount; m++)
                {
                    int w = Mathf.Max(1, width >> m);
                    int h = Mathf.Max(1, height >> m);
                    cmd.SetComputeIntParam(hizShader, "_MipLevel", m);
                    cmd.SetComputeIntParams(hizShader, "_Size", new int[] { w, h });
                    cmd.SetComputeTextureParam(hizShader, hizDownKernel, "_HiZTexture", hiZRT, m);
                    cmd.DispatchCompute(hizShader, hizDownKernel, Mathf.CeilToInt(w / 8f), Mathf.CeilToInt(h / 8f), 1);
                }

                cmd.SetGlobalTexture("_HiZTexture", hiZRT);
                cmd.SetGlobalInt("_HiZMipCount", hiZMipCount);
                cmd.SetGlobalVector("_HiZSize", new Vector4(hiZSize.x, hiZSize.y, 0, 0));
            }
            else
            {
                cmd.SetGlobalInt("_HiZMipCount", 0);
                cmd.SetGlobalVector("_HiZSize", new Vector4(1, 1, 0, 0));
            }

            //After finishing rendering the textures
            //We compute the grass positions buffer
            Vector2Int gridSize = new Vector2Int(Mathf.CeilToInt(cameraBounds.size.x / spacing), Mathf.CeilToInt(cameraBounds.size.z / spacing));
            Vector2Int gridStartIndex = new Vector2Int(Mathf.FloorToInt(cameraBounds.min.x / spacing), Mathf.FloorToInt(cameraBounds.min.z / spacing));

            grassPositionsBuffer?.Release();
            grassPositionsBuffer = new ComputeBuffer((int)(1000000 * maxBufferCount), sizeof(float) * 4, ComputeBufferType.Append);

            cmd.SetComputeMatrixParam(computeShader, "_VPMatrix", Camera.main.projectionMatrix * Camera.main.worldToCameraMatrix);
            cmd.SetComputeFloatParam(computeShader, "_FullDensityDistance", fullDensityDistance);
            cmd.SetComputeFloatParam(computeShader, "_DensityFalloffExponent", densityFalloffExponent);
            cmd.SetComputeVectorParam(computeShader, "_BoundsMin", cameraBounds.min);
            cmd.SetComputeVectorParam(computeShader, "_BoundsMax", cameraBounds.max);
            cmd.SetComputeVectorParam(computeShader, "_CameraPosition", Camera.main.transform.position);
            cmd.SetComputeVectorParam(computeShader, "_CenterPos", centerPos);
            cmd.SetComputeFloatParam(computeShader, "_DrawDistance", drawDistance);
            cmd.SetComputeFloatParam(computeShader, "_TextureUpdateThreshold", textureUpdateThreshold);
            cmd.SetComputeFloatParam(computeShader, "_Spacing", spacing);
            cmd.SetComputeIntParams(computeShader, "_GridStartIndex", new int[] { gridStartIndex.x, gridStartIndex.y });
            cmd.SetComputeIntParams(computeShader, "_GridSize", new int[] { gridSize.x, gridSize.y });
            cmd.SetComputeBufferParam(computeShader, 0, "_GrassPositions", grassPositionsBuffer);
            cmd.SetComputeTextureParam(computeShader, 0, "_GrassHeightMapRT", heightRT);
            cmd.SetComputeTextureParam(computeShader, 0, "_GrassMaskMapRT", maskRT);
            if (hiZRT != null)
            {
                cmd.SetComputeTextureParam(computeShader, 0, "_HiZTexture", hiZRT);
            }
            cmd.SetComputeIntParams(computeShader, "_HiZSize", new int[] { hiZSize.x, hiZSize.y });
            cmd.SetComputeIntParam(computeShader, "_HiZMipCount", hiZMipCount);

            grassPositionsBuffer.SetCounterValue(0);

            cmd.DispatchCompute(computeShader, 0, Mathf.CeilToInt((float)gridSize.x / 8), Mathf.CeilToInt((float)gridSize.y / 8), 1);
            
            //After Dispatching we set the positions buffer as global
            cmd.SetGlobalBuffer("_GrassPositions", grassPositionsBuffer);

            //Finally we copy the counter value to the argsBuffer in the script so that the DrawMeshInstancedIndirect could execute properly
            cmd.CopyCounterValue(grassPositionsBuffer, InfiniteGrassRenderer.instance.argsBuffer, 4);

            if (InfiniteGrassRenderer.instance.previewVisibleGrassCount)
            {
                cmd.CopyCounterValue(grassPositionsBuffer, InfiniteGrassRenderer.instance.tBuffer, 0);
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            CommandBufferPool.Release(cmd);
        }


        Bounds CalculateCameraBounds(Camera camera, float drawDistance)
        {
            Vector3 ntopLeft = camera.ViewportToWorldPoint(new Vector3(0, 1, camera.nearClipPlane));
            Vector3 ntopRight = camera.ViewportToWorldPoint(new Vector3(1, 1, camera.nearClipPlane));
            Vector3 nbottomLeft = camera.ViewportToWorldPoint(new Vector3(0, 0, camera.nearClipPlane));
            Vector3 nbottomRight = camera.ViewportToWorldPoint(new Vector3(1, 0, camera.nearClipPlane));

            Vector3 ftopLeft = camera.ViewportToWorldPoint(new Vector3(0, 1, drawDistance));
            Vector3 ftopRight = camera.ViewportToWorldPoint(new Vector3(1, 1, drawDistance));
            Vector3 fbottomLeft = camera.ViewportToWorldPoint(new Vector3(0, 0, drawDistance));
            Vector3 fbottomRight = camera.ViewportToWorldPoint(new Vector3(1, 0, drawDistance));

            float[] xValues = new float[] { ftopLeft.x, ftopRight.x, ntopLeft.x, ntopRight.x, fbottomLeft.x, fbottomRight.x, nbottomLeft.x, nbottomRight.x };
            float startX = xValues.Max();
            float endX = xValues.Min();

            float[] yValues = new float[] { ftopLeft.y, ftopRight.y, ntopLeft.y, ntopRight.y, fbottomLeft.y, fbottomRight.y, nbottomLeft.y, nbottomRight.y };
            float startY = yValues.Max();
            float endY = yValues.Min();

            float[] zValues = new float[] { ftopLeft.z, ftopRight.z, ntopLeft.z, ntopRight.z, fbottomLeft.z, fbottomRight.z, nbottomLeft.z, nbottomRight.z };
            float startZ = zValues.Max();
            float endZ = zValues.Min();

            Vector3 center = new Vector3((startX + endX) / 2, (startY + endY) / 2, (startZ + endZ) / 2);
            Vector3 size = new Vector3(Mathf.Abs(startX - endX), Mathf.Abs(startY - endY), Mathf.Abs(startZ - endZ));

            Bounds bounds = new Bounds(center, size);
            bounds.Expand(1);
            return bounds;
        }

        RTHandle CreateFallbackHiZ()
        {
            RenderTextureDescriptor desc = new RenderTextureDescriptor(1, 1, RenderTextureFormat.RFloat, 0)
            {
                useMipMap = false,
                autoGenerateMips = false,
                enableRandomWrite = true,
                mipCount = 1
            };
            RTHandle handle = null;
            RenderingUtils.ReAllocateIfNeeded(ref handle, desc, FilterMode.Point);
            return handle;
        }

        public void Dispose()
        {
            heightRT?.Release();
            heightDepthRT?.Release();
            maskRT?.Release();
            colorRT?.Release();
            slopeRT?.Release();
            hiZRT?.Release();
            grassPositionsBuffer?.Release();
        }
    }

}


