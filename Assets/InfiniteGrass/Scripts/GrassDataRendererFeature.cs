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

        private GraphicsBuffer _grassPositionsBuffer;

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!InfiniteGrassRenderer.instance || !_heightMapMat || !_computeShader)
                return;

            var renderingData = _renderingData;

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

            Vector2 centerPos = new Vector2(Mathf.Floor(Camera.main.transform.position.x / textureUpdateThreshold) * textureUpdateThreshold,
                                            Mathf.Floor(Camera.main.transform.position.z / textureUpdateThreshold) * textureUpdateThreshold);

            Matrix4x4 viewMatrix = Matrix4x4.TRS(new Vector3(centerPos.x, cameraBounds.max.y, centerPos.y), Quaternion.LookRotation(-Vector3.up), new Vector3(1, 1, -1)).inverse;
            Matrix4x4 projectionMatrix = Matrix4x4.Ortho(-(drawDistance + textureUpdateThreshold), drawDistance + textureUpdateThreshold,
                                                        -(drawDistance + textureUpdateThreshold), drawDistance + textureUpdateThreshold,
                                                        0, cameraBounds.size.y);

            TextureHandle heightHandle = renderGraph.ImportTexture(_heightRT);
            TextureHandle depthHandle = renderGraph.ImportTexture(_heightDepthRT);
            TextureHandle maskHandle = renderGraph.ImportTexture(_maskRT);
            TextureHandle colorHandle = renderGraph.ImportTexture(_colorRT);
            TextureHandle slopeHandle = renderGraph.ImportTexture(_slopeRT);

            // Height pass
            {
                var drawSetting = CreateDrawingSettings(_shaderTagsList, ref renderingData, renderingData.cameraData.defaultOpaqueSortFlags);
                _heightMapMat.SetVector(BoundsYMinMax, new Vector2(cameraBounds.min.y, cameraBounds.max.y));
                drawSetting.overrideMaterial = _heightMapMat;
                var filterSetting = new FilteringSettings(RenderQueueRange.all, _heightMapLayer);
                var rendererListDesc = new RendererListParams(renderingData.cullResults, drawSetting, filterSetting);
                var rendererList = renderGraph.CreateRendererList(rendererListDesc);

                using var builder = renderGraph.AddRasterRenderPass<HeightPassData>("Grass Height Pass", out var passData);
                passData.rendererList = rendererList;
                passData.color = heightHandle;
                passData.depth = depthHandle;
                passData.view = viewMatrix;
                passData.proj = projectionMatrix;

                builder.SetRenderAttachment(passData.color, 0);
                builder.SetDepthAttachment(passData.depth, DepthAccess.Write);
                builder.UseRendererList(passData.rendererList);
                builder.SetRenderFunc((HeightPassData data, RasterGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    cmd.SetViewProjectionMatrices(data.view, data.proj);
                    cmd.ClearRenderTarget(true, true, Color.black);
                    cmd.DrawRendererList(data.rendererList);
                });
            }

            // Mask pass
            {
                var drawSetting = CreateDrawingSettings(new ShaderTagId("GrassMask"), ref renderingData, SortingCriteria.CommonTransparent);
                var filterSetting = new FilteringSettings(RenderQueueRange.all);
                var rendererListDesc = new RendererListParams(renderingData.cullResults, drawSetting, filterSetting);
                var rendererList = renderGraph.CreateRendererList(rendererListDesc);

                using var builder = renderGraph.AddRasterRenderPass<MaskPassData>("Grass Mask Pass", out var passData);
                passData.rendererList = rendererList;
                passData.color = maskHandle;
                passData.view = viewMatrix;
                passData.proj = projectionMatrix;

                builder.SetRenderAttachment(passData.color, 0);
                builder.UseRendererList(passData.rendererList);
                builder.SetRenderFunc((MaskPassData data, RasterGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    cmd.SetViewProjectionMatrices(data.view, data.proj);
                    cmd.ClearRenderTarget(true, true, Color.clear);
                    cmd.DrawRendererList(data.rendererList);
                });
            }

            // Color pass
            {
                var drawSetting = CreateDrawingSettings(new ShaderTagId("GrassColor"), ref renderingData, SortingCriteria.CommonTransparent);
                var filterSetting = new FilteringSettings(RenderQueueRange.all);
                var rendererListDesc = new RendererListParams(renderingData.cullResults, drawSetting, filterSetting);
                var rendererList = renderGraph.CreateRendererList(rendererListDesc);

                using var builder = renderGraph.AddRasterRenderPass<ColorPassData>("Grass Color Pass", out var passData);
                passData.rendererList = rendererList;
                passData.color = colorHandle;
                passData.view = viewMatrix;
                passData.proj = projectionMatrix;

                builder.SetRenderAttachment(passData.color, 0);
                builder.UseRendererList(passData.rendererList);
                builder.SetRenderFunc((ColorPassData data, RasterGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    cmd.SetViewProjectionMatrices(data.view, data.proj);
                    cmd.ClearRenderTarget(true, true, Color.clear);
                    cmd.DrawRendererList(data.rendererList);
                });
            }

            // Slope pass
            {
                var drawSetting = CreateDrawingSettings(new ShaderTagId("GrassSlope"), ref renderingData, SortingCriteria.CommonTransparent);
                var filterSetting = new FilteringSettings(RenderQueueRange.all);
                var rendererListDesc = new RendererListParams(renderingData.cullResults, drawSetting, filterSetting);
                var rendererList = renderGraph.CreateRendererList(rendererListDesc);

                using var builder = renderGraph.AddRasterRenderPass<SlopePassData>("Grass Slope Pass", out var passData);
                passData.rendererList = rendererList;
                passData.color = slopeHandle;
                passData.view = viewMatrix;
                passData.proj = projectionMatrix;

                builder.SetRenderAttachment(passData.color, 0);
                builder.UseRendererList(passData.rendererList);
                builder.SetRenderFunc((SlopePassData data, RasterGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    cmd.SetViewProjectionMatrices(data.view, data.proj);
                    cmd.ClearRenderTarget(true, true, Color.clear);
                    cmd.DrawRendererList(data.rendererList);
                });
            }

            _grassPositionsBuffer?.Release();
            _grassPositionsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Append, (int)(1000000 * maxBufferCount), sizeof(float) * 4);
            _grassPositionsBuffer.SetCounterValue(0);
            BufferHandle posHandle = renderGraph.ImportBuffer(_grassPositionsBuffer);

            // Compute pass
            {
                using var builder = renderGraph.AddComputePass<ComputePassData>("Grass Compute Pass", out var passData);
                passData.height = heightHandle;
                passData.mask = maskHandle;
                passData.positions = posHandle;
                passData.positionBuffer = _grassPositionsBuffer;
                passData.cameraVP = Camera.main.projectionMatrix * Camera.main.worldToCameraMatrix;
                passData.centerPos = centerPos;
                passData.bounds = cameraBounds;
                passData.spacing = spacing;
                passData.fullDensity = fullDensityDistance;
                passData.densityExponent = densityFalloffExponent;
                passData.drawDistance = drawDistance;
                passData.textureThreshold = textureUpdateThreshold;

                builder.UseTexture(passData.height);
                builder.UseTexture(passData.mask);
                builder.UseBuffer(passData.positions, AccessFlags.Write);

                builder.SetRenderFunc((ComputePassData data, ComputeGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;

                    cmd.SetGlobalTexture(GrassColorRT, colorHandle); // set color & slope
                    cmd.SetGlobalTexture(GrassSlopeRT, slopeHandle);

                    _computeShader.SetMatrix(VpMatrix, data.cameraVP);
                    _computeShader.SetFloat(FullDensityDistance, data.fullDensity);
                    _computeShader.SetFloat(DensityFalloffExponent, data.densityExponent);
                    _computeShader.SetVector(BoundsMin, data.bounds.min);
                    _computeShader.SetVector(BoundsMax, data.bounds.max);
                    _computeShader.SetVector(CameraPosition, Camera.main.transform.position);
                    _computeShader.SetVector(CenterPos, data.centerPos);
                    _computeShader.SetFloat(DrawDistance, data.drawDistance);
                    _computeShader.SetFloat(TextureUpdateThreshold, data.textureThreshold);
                    _computeShader.SetFloat(Spacing, data.spacing);

                    Vector2Int gridSize = new Vector2Int(Mathf.CeilToInt(data.bounds.size.x / data.spacing), Mathf.CeilToInt(data.bounds.size.z / data.spacing));
                    Vector2Int gridStartIndex = new Vector2Int(Mathf.FloorToInt(data.bounds.min.x / data.spacing), Mathf.FloorToInt(data.bounds.min.z / data.spacing));

                    _computeShader.SetVector(GridStartIndex, (Vector2)gridStartIndex);
                    _computeShader.SetVector(GridSize, (Vector2)gridSize);
                    _computeShader.SetBuffer(0, GrassPositions, data.positionBuffer);
                    _computeShader.SetTexture(0, GrassHeightMapRT, data.height);
                    _computeShader.SetTexture(0, GrassMaskMapRT, data.mask);

                    cmd.DispatchCompute(_computeShader, 0, Mathf.CeilToInt((float)gridSize.x / 8), Mathf.CeilToInt((float)gridSize.y / 8), 1);

                    cmd.SetGlobalBuffer(GrassPositions, data.positionBuffer);
                    cmd.CopyCounterValue(data.positionBuffer, InfiniteGrassRenderer.instance.argsBuffer, 4);

                    if (InfiniteGrassRenderer.instance.previewVisibleGrassCount)
                        cmd.CopyCounterValue(data.positionBuffer, InfiniteGrassRenderer.instance.tBuffer, 0);
                });
            }
        }

        class HeightPassData
        {
            public RendererListHandle rendererList;
            public TextureHandle color;
            public TextureHandle depth;
            public Matrix4x4 view;
            public Matrix4x4 proj;
        }

        class MaskPassData
        {
            public RendererListHandle rendererList;
            public TextureHandle color;
            public Matrix4x4 view;
            public Matrix4x4 proj;
        }

        class ColorPassData
        {
            public RendererListHandle rendererList;
            public TextureHandle color;
            public Matrix4x4 view;
            public Matrix4x4 proj;
        }

        class SlopePassData
        {
            public RendererListHandle rendererList;
            public TextureHandle color;
            public Matrix4x4 view;
            public Matrix4x4 proj;
        }

        class ComputePassData
        {
            public TextureHandle height;
            public TextureHandle mask;
            public BufferHandle positions;
            public GraphicsBuffer positionBuffer;
            public Matrix4x4 cameraVP;
            public Vector2 centerPos;
            public Bounds bounds;
            public float spacing;
            public float fullDensity;
            public float densityExponent;
            public float drawDistance;
            public float textureThreshold;
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


