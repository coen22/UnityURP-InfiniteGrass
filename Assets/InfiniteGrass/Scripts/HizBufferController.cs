// https://github.com/gokselgoktas/hi-z-buffer (adapted)
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

[ExecuteAlways]
public class HizBufferController : MonoBehaviour
{
    private Camera _mainCamera;
    private int _lodCount;
    private Vector2 _textureSize;
    private Material _generateBufferMaterial;
    private CameraEvent _lastCameraEvent = CameraEvent.AfterReflections;
    [SerializeField] private RenderTexture _hiZDepthTexture;

    public static HizBufferController instance { get; private set; }
    public Vector2 TextureSize => _textureSize;
    public RenderTexture Texture => _hiZDepthTexture;

    const int MAXIMUM_BUFFER_SIZE = 1024;

    enum Pass
    {
        Blit,
        Reduce
    }

    void OnEnable()
    {
        instance = this;
        RenderPipelineManager.endContextRendering += OnBeginContextRendering;
        _mainCamera = Camera.main;
        _generateBufferMaterial = new Material(Shader.Find("IndirectRendering/HiZ/Buffer"));
        _mainCamera.depthTextureMode = DepthTextureMode.Depth;
    }

    void OnDisable()
    {
        instance = null;
        if (_hiZDepthTexture != null)
        {
            _hiZDepthTexture.Release();
            _hiZDepthTexture = null;
        }
        RenderPipelineManager.endContextRendering -= OnBeginContextRendering;
    }

    void InitializeTexture()
    {
        if (_hiZDepthTexture != null)
            _hiZDepthTexture.Release();

        int size = Mathf.Min(Mathf.NextPowerOfTwo(Mathf.Max(_mainCamera.pixelWidth, _mainCamera.pixelHeight)), MAXIMUM_BUFFER_SIZE);
        _textureSize = new Vector2(size, size);
        _hiZDepthTexture = new RenderTexture(size, size, 0, RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear)
        {
            filterMode = FilterMode.Point,
            useMipMap = true,
            autoGenerateMips = false,
            hideFlags = HideFlags.DontSave
        };
        _hiZDepthTexture.Create();
    }

    void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
    {
        int size = Mathf.Min(Mathf.NextPowerOfTwo(Mathf.Max(_mainCamera.pixelWidth, _mainCamera.pixelHeight)), MAXIMUM_BUFFER_SIZE);
        _textureSize = new Vector2(size, size);
        _lodCount = (int)Mathf.Floor(Mathf.Log(size, 2f));
        if (_lodCount == 0) return;

        if (_hiZDepthTexture == null || _hiZDepthTexture.width != size || _hiZDepthTexture.height != size || _lastCameraEvent != CameraEvent.AfterReflections)
        {
            InitializeTexture();
            _lastCameraEvent = CameraEvent.AfterReflections;
        }

        var prevRT = RenderTexture.active;
        var rt1 = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.RGHalf);
        RenderTexture.active = rt1;
        Graphics.Blit(rt1, _hiZDepthTexture, _generateBufferMaterial, (int)Pass.Blit);
        RenderTexture.ReleaseTemporary(rt1);
        RenderTexture.active = prevRT;

        RenderTexture rt = null;
        RenderTexture lastRt = null;
        for (int i = 0; i < _lodCount; ++i)
        {
            size >>= 1;
            size = Mathf.Max(size, 1);
            rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.RGHalf);
            rt.filterMode = FilterMode.Point;
            prevRT = RenderTexture.active;
            RenderTexture.active = rt;
            if (i == 0)
            {
                Graphics.Blit(null, rt);
                Graphics.Blit(_hiZDepthTexture, rt, _generateBufferMaterial, (int)Pass.Reduce);
            }
            else
            {
                Graphics.Blit(null, rt);
                Graphics.Blit(lastRt, rt, _generateBufferMaterial, (int)Pass.Reduce);
            }
            lastRt = rt;
            Graphics.CopyTexture(rt, 0, 0, _hiZDepthTexture, 0, i + 1);
            RenderTexture.ReleaseTemporary(rt);
            RenderTexture.active = prevRT;
        }
        Shader.SetGlobalTexture("_hiZDepthTexture", _hiZDepthTexture);
    }
}
