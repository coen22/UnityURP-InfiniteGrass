using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class GrassDataRendererFeature : ScriptableRendererFeature
{
    private static readonly int GrassColorRT = Shader.PropertyToID("_GrassColorRT");
    private static readonly int GrassSlopeRT = Shader.PropertyToID("_GrassSlopeRT");
    private static readonly int VpMatrix = Shader.PropertyToID("_VPMatrix");
    private static readonly int FullDensityDistance = Shader.PropertyToID("_FullDensityDistance");
    private static readonly int DensityFalloffExponent = Shader.PropertyToID("_DensityFalloffExponent");
    private static readonly int BoundsMin = Shader.PropertyToID("_BoundsMin");
    private static readonly int BoundsMax = Shader.PropertyToID("_BoundsMax");
    private static readonly int CameraPosition = Shader.PropertyToID("_CameraPosition");
    private static readonly int CenterPos = Shader.PropertyToID("_CenterPos");
    private static readonly int DrawDistance = Shader.PropertyToID("_DrawDistance");
    private static readonly int TextureUpdateThreshold = Shader.PropertyToID("_TextureUpdateThreshold");
    private static readonly int Spacing = Shader.PropertyToID("_Spacing");
    private static readonly int GridStartIndex = Shader.PropertyToID("_GridStartIndex");
    private static readonly int GridSize = Shader.PropertyToID("_GridSize");
    private static readonly int GrassPositions = Shader.PropertyToID("_GrassPositions");
    private static readonly int GrassHeightMapRT = Shader.PropertyToID("_GrassHeightMapRT");
    private static readonly int GrassMaskMapRT = Shader.PropertyToID("_GrassMaskMapRT");
    private static readonly int BoundsYMinMax = Shader.PropertyToID("_BoundsYMinMax");
    
    [SerializeField] private LayerMask heightMapLayer;
    [SerializeField] private Material heightMapMat;
    [SerializeField] private ComputeShader computeShader;

    private GrassDataPass _grassDataPass;

    public override void Create()
    {
        _grassDataPass = new GrassDataPass(heightMapLayer, heightMapMat, computeShader)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        _grassDataPass.Setup(renderingData);
        renderer.EnqueuePass(_grassDataPass);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _grassDataPass.Dispose();
        }
    }

    private class GrassDataPass : ScriptableRenderPass
    {
        private readonly List<ShaderTagId> _shaderTagsList = new List<ShaderTagId>();

        private RTHandle _heightRT;
        private RTHandle _heightDepthRT;
        private RTHandle _maskRT;
        private RTHandle _colorRT;
        private RTHandle _slopeRT;

        private readonly LayerMask _heightMapLayer;
        private readonly Material _heightMapMat;

        private readonly ComputeShader _computeShader;
        private RenderingData _renderingData;

        public GrassDataPass(LayerMask heightMapLayer, Material heightMapMat, ComputeShader computeShader)
        {
            this._heightMapLayer = heightMapLayer;
            this._computeShader = computeShader;
            this._heightMapMat = heightMapMat;

            _shaderTagsList.Add(new ShaderTagId("SRPDefaultUnlit"));
            _shaderTagsList.Add(new ShaderTagId("UniversalForward"));
            _shaderTagsList.Add(new ShaderTagId("UniversalForwardOnly"));
        }

        public void Setup(in RenderingData renderingData)
        {
            _renderingData = renderingData;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            const int textureSize = 2048;

            var heightDesc = new RenderTextureDescriptor(textureSize, textureSize)
            {
                graphicsFormat = GraphicsFormat.R32G32_SFloat
            };
            RenderingUtils.ReAllocateHandleIfNeeded(ref _heightRT, heightDesc, FilterMode.Bilinear);

            var depthDesc = new RenderTextureDescriptor(textureSize, textureSize)
            {
                graphicsFormat = GraphicsFormat.None,
                depthStencilFormat = GraphicsFormat.D32_SFloat
            };
            RenderingUtils.ReAllocateHandleIfNeeded(ref _heightDepthRT, depthDesc, FilterMode.Bilinear);

            var maskDesc = new RenderTextureDescriptor(textureSize, textureSize)
            {
                graphicsFormat = GraphicsFormat.R32_SFloat
            };
            RenderingUtils.ReAllocateHandleIfNeeded(ref _maskRT, maskDesc, FilterMode.Bilinear);

            var colorDesc = new RenderTextureDescriptor(textureSize, textureSize)
            {
                graphicsFormat = GraphicsFormat.R32G32B32A32_SFloat
            };
            RenderingUtils.ReAllocateHandleIfNeeded(ref _colorRT, colorDesc, FilterMode.Bilinear);

            var slopeDesc = new RenderTextureDescriptor(textureSize, textureSize)
            {
                graphicsFormat = GraphicsFormat.R32G32B32A32_SFloat
            };
            RenderingUtils.ReAllocateHandleIfNeeded(ref _slopeRT, slopeDesc, FilterMode.Bilinear);
            
            ConfigureTarget(_heightRT, _heightDepthRT);
            ConfigureClear(ClearFlag.All, Color.black);
        }

        private ComputeBuffer _grassPositionsBuffer;

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!InfiniteGrassRenderer.instance || !_heightMapMat || !_computeShader)
                return;

            TextureHandle heightHandle = renderGraph.ImportTexture(_heightRT);
            TextureHandle depthHandle = renderGraph.ImportTexture(_heightDepthRT);
            TextureHandle maskHandle = renderGraph.ImportTexture(_maskRT);
            TextureHandle colorHandle = renderGraph.ImportTexture(_colorRT);
            TextureHandle slopeHandle = renderGraph.ImportTexture(_slopeRT);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("GrassDataPass", out var passData))
            {
                passData.pass = this;
                passData.height = heightHandle;
                passData.heightDepth = depthHandle;
                passData.mask = maskHandle;
                passData.color = colorHandle;
                passData.slope = slopeHandle;

                builder.UseTexture(passData.height);
                builder.UseTexture(passData.heightDepth);
                builder.UseTexture(passData.mask, AccessFlags.Write);
                builder.UseTexture(passData.color, AccessFlags.Write);
                builder.UseTexture(passData.slope, AccessFlags.Write);

                builder.SetRenderFunc((PassData data, RasterGraphContext ctx) => ExecutePass(data, ctx));
            }
        }

        class PassData
        {
            public GrassDataPass pass;
            public TextureHandle height;
            public TextureHandle heightDepth;
            public TextureHandle mask;
            public TextureHandle color;
            public TextureHandle slope;
        }

        static void ExecutePass(PassData data, RasterGraphContext context)
        {
            data.pass.ExecuteInternal(context, data);
        }

        void ExecuteInternal(RasterGraphContext context, PassData data)
        {
            var renderingData = _renderingData;

            RasterCommandBuffer cmd = context.cmd;

            float spacing = InfiniteGrassRenderer.instance.spacing;
            float fullDensityDistance = InfiniteGrassRenderer.instance.fullDensityDistance;
            float drawDistance = InfiniteGrassRenderer.instance.drawDistance;
            float maxBufferCount = InfiniteGrassRenderer.instance.maxBufferCount;
            float densityFalloffExponent = InfiniteGrassRenderer.instance.densityFalloffExponent;
            float textureUpdateThreshold = InfiniteGrassRenderer.instance.textureUpdateThreshold;

            Bounds cameraBounds = CalculateCameraBounds(Camera.main, drawDistance);

            if (!Camera.main)
            {
                Debug.LogError("No main camera found. Grass data rendering requires a main camera.");
                return;
            }

            Vector2 centerPos = new Vector2(Mathf.Floor(Camera.main.transform.position.x / textureUpdateThreshold) * textureUpdateThreshold, Mathf.Floor(Camera.main.transform.position.z / textureUpdateThreshold) * textureUpdateThreshold);

            Matrix4x4 viewMatrix = Matrix4x4.TRS(new Vector3(centerPos.x, cameraBounds.max.y, centerPos.y), Quaternion.LookRotation(-Vector3.up), new Vector3(1, 1, -1)).inverse;
            Matrix4x4 projectionMatrix = Matrix4x4.Ortho(-(drawDistance + textureUpdateThreshold), drawDistance + textureUpdateThreshold, -(drawDistance + textureUpdateThreshold), drawDistance + textureUpdateThreshold, 0, cameraBounds.size.y);

            cmd.SetViewProjectionMatrices(viewMatrix, projectionMatrix);

            using (new ProfilingScope(cmd, new ProfilingSampler("Grass Height Map RT")))
            {

                var drawSetting = CreateDrawingSettings(_shaderTagsList, ref renderingData, renderingData.cameraData.defaultOpaqueSortFlags);
                _heightMapMat.SetVector(BoundsYMinMax, new Vector2(cameraBounds.min.y, cameraBounds.max.y));
                drawSetting.overrideMaterial = _heightMapMat;
                var filterSetting = new FilteringSettings(RenderQueueRange.all, _heightMapLayer);

                var rendererListDesc = new RendererListParams(renderingData.cullResults, drawSetting, filterSetting);
                var rendererList = context.CreateRendererList(ref rendererListDesc);
                cmd.DrawRendererList(rendererList);
            }

            cmd.SetRenderTarget(data.mask);
            cmd.ClearRenderTarget(true, true, new Color(0, 0, 0, 0));

            using (new ProfilingScope(cmd, new ProfilingSampler("Grass Mask RT")))
            {

                var drawSetting = CreateDrawingSettings(new ShaderTagId("GrassMask"), ref renderingData, SortingCriteria.CommonTransparent);
                var filterSetting = new FilteringSettings(RenderQueueRange.all);

                var rendererListDesc = new RendererListParams(renderingData.cullResults, drawSetting, filterSetting);
                var rendererList = context.CreateRendererList(ref rendererListDesc);
                cmd.DrawRendererList(rendererList);
            }

            cmd.SetRenderTarget(data.color);
            cmd.ClearRenderTarget(true, true, new Color(0, 0, 0, 0));

            using (new ProfilingScope(cmd, new ProfilingSampler("Grass Color RT")))
            {

                var drawSetting = CreateDrawingSettings(new ShaderTagId("GrassColor"), ref renderingData, SortingCriteria.CommonTransparent);
                var filterSetting = new FilteringSettings(RenderQueueRange.all);

                var rendererListDesc = new RendererListParams(renderingData.cullResults, drawSetting, filterSetting);
                var rendererList = context.CreateRendererList(ref rendererListDesc);
                cmd.DrawRendererList(rendererList);
            }

            cmd.SetRenderTarget(data.slope);
            cmd.ClearRenderTarget(true, true, new Color(0, 0, 0, 0));

            using (new ProfilingScope(cmd, new ProfilingSampler("Grass Slope RT")))
            {

                var drawSetting = CreateDrawingSettings(new ShaderTagId("GrassSlope"), ref renderingData, SortingCriteria.CommonTransparent);
                var filterSetting = new FilteringSettings(RenderQueueRange.all);

                var rendererListDesc = new RendererListParams(renderingData.cullResults, drawSetting, filterSetting);
                var rendererList = context.CreateRendererList(ref rendererListDesc);
                cmd.DrawRendererList(rendererList);
            }

            cmd.SetGlobalTexture(GrassColorRT, data.color);
            cmd.SetGlobalTexture(GrassSlopeRT, data.slope);

            cmd.SetViewProjectionMatrices(renderingData.cameraData.camera.worldToCameraMatrix, renderingData.cameraData.camera.projectionMatrix);

            Vector2Int gridSize = new Vector2Int(Mathf.CeilToInt(cameraBounds.size.x / spacing), Mathf.CeilToInt(cameraBounds.size.z / spacing));
            Vector2Int gridStartIndex = new Vector2Int(Mathf.FloorToInt(cameraBounds.min.x / spacing), Mathf.FloorToInt(cameraBounds.min.z / spacing));

            _grassPositionsBuffer?.Release();
            _grassPositionsBuffer = new ComputeBuffer((int)(1000000 * maxBufferCount), sizeof(float) * 4, ComputeBufferType.Append);

            _computeShader.SetMatrix(VpMatrix, Camera.main.projectionMatrix * Camera.main.worldToCameraMatrix);
            _computeShader.SetFloat(FullDensityDistance, fullDensityDistance);
            _computeShader.SetFloat(DensityFalloffExponent, densityFalloffExponent);
            _computeShader.SetVector(BoundsMin, cameraBounds.min);
            _computeShader.SetVector(BoundsMax, cameraBounds.max);
            _computeShader.SetVector(CameraPosition, Camera.main.transform.position);
            _computeShader.SetVector(CenterPos, centerPos);
            _computeShader.SetFloat(DrawDistance, drawDistance);
            _computeShader.SetFloat(TextureUpdateThreshold, textureUpdateThreshold);
            _computeShader.SetFloat(Spacing, spacing);
            _computeShader.SetVector(GridStartIndex, (Vector2)gridStartIndex);
            _computeShader.SetVector(GridSize, (Vector2)gridSize);
            _computeShader.SetBuffer(0, GrassPositions, _grassPositionsBuffer);
            _computeShader.SetTexture(0, GrassHeightMapRT, data.height);
            _computeShader.SetTexture(0, GrassMaskMapRT, data.mask);

            _grassPositionsBuffer.SetCounterValue(0);

            cmd.DispatchCompute(_computeShader, 0, Mathf.CeilToInt((float)gridSize.x / 8), Mathf.CeilToInt((float)gridSize.y / 8), 1);

            cmd.SetGlobalBuffer(GrassPositions, _grassPositionsBuffer);
            cmd.CopyCounterValue(_grassPositionsBuffer, InfiniteGrassRenderer.instance.argsBuffer, 4);

            if (InfiniteGrassRenderer.instance.previewVisibleGrassCount)
            {
                cmd.CopyCounterValue(_grassPositionsBuffer, InfiniteGrassRenderer.instance.tBuffer, 0);
            }

            // RasterCommandBuffer from RenderGraph does not require manual execution or release.
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

        public void Dispose()
        {
            _heightRT?.Release();
            _heightDepthRT?.Release();
            _maskRT?.Release();
            _colorRT?.Release();
            _slopeRT?.Release();
            _grassPositionsBuffer?.Release();
        }
    }

}


