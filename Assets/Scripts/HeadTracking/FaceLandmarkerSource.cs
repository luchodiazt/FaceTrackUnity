using System.Collections;
using System.Diagnostics;
using System.Threading;
using Mediapipe.Tasks.Components.Containers;
using Mediapipe.Tasks.Vision.FaceLandmarker;
using Mediapipe.Unity;
using Mediapipe.Unity.Experimental;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace FaceTrack.HeadTracking
{
  /// <summary>
  /// Landmarks de una detección, en coordenadas normalizadas de la imagen cruda de la webcam
  /// (origen arriba-izquierda, sin espejo).
  /// </summary>
  public struct FaceSample
  {
    public Vector2 rightIris;     // landmark 468, iris del ojo derecho de la persona
    public Vector2 leftIris;      // landmark 473
    public Vector2 rightEyeOuter; // landmark 33
    public Vector2 leftEyeOuter;  // landmark 263
    public Vector2 rightEyeInner; // landmark 133
    public Vector2 leftEyeInner;  // landmark 362
    public Vector2 noseTip;       // landmark 1
    public long timestampMillisec;
  }

  /// <summary>
  /// Captura la webcam y corre MediaPipe FaceLandmarker en modo LIVE_STREAM.
  /// Runner mínimo: no depende del Bootstrap ni de la UI de los samples del plugin.
  /// </summary>
  public class FaceLandmarkerSource : MonoBehaviour
  {
    private const int RightIrisIndex = 468;
    private const int LeftIrisIndex = 473;
    private const int RightEyeOuterIndex = 33;
    private const int LeftEyeOuterIndex = 263;
    private const int RightEyeInnerIndex = 133;
    private const int LeftEyeInnerIndex = 362;
    private const int NoseTipIndex = 1;

    [Header("Webcam")]
    [Tooltip("Vacío = primera webcam encontrada.")]
    [SerializeField] private string deviceName = "";
    [SerializeField] private int requestedWidth = 640;
    [SerializeField] private int requestedHeight = 480;
    [SerializeField] private int requestedFps = 30;

    [Header("Face Landmarker")]
    [SerializeField] private string modelFile = "face_landmarker_v2.bytes";
    [SerializeField, Range(0f, 1f)] private float minFaceDetectionConfidence = 0.5f;
    [SerializeField, Range(0f, 1f)] private float minFacePresenceConfidence = 0.5f;
    [SerializeField, Range(0f, 1f)] private float minTrackingConfidence = 0.5f;

    public WebCamTexture WebCamTexture => _webCamTexture;
    public bool IsRunning { get; private set; }
    public float TrackerFps { get; private set; }
    public int ImageWidth => _webCamTexture != null ? _webCamTexture.width : 0;
    public int ImageHeight => _webCamTexture != null ? _webCamTexture.height : 0;

    public bool HasFace
    {
      get { lock (_lock) { return _hasFace; } }
    }

    /// <summary>
    /// Cada frame nuevo de la webcam (hilo principal), antes de devolverlo al pool, con su timestamp.
    /// Sirve para correr otros modelos sobre la misma imagen sin abrir la cámara dos veces.
    /// Las imágenes de <see cref="TextureFrame.BuildCPUImage"/> no copian los píxeles: es seguro porque el pool
    /// es FIFO de 10 frames y el frame recién se reutiliza ~300 ms después.
    /// </summary>
    public event System.Action<TextureFrame, long> FrameReady;

    /// <summary>Justo antes de liberar los frames: quien use <see cref="FrameReady"/> debe cerrar su modelo aquí.</summary>
    public event System.Action Stopping;

    // El callback de MediaPipe llega en otro hilo: todo lo que comparte pasa por este lock
    private readonly object _lock = new object();
    private FaceSample _latest;
    private bool _hasFace;
    private int _resultCount;
    private float _fpsTimer;

    private WebCamTexture _webCamTexture;
    private FaceLandmarker _landmarker;
    private TextureFramePool _textureFramePool;
    private Stopwatch _stopwatch;
    private long _lastTimestamp = -1;

    public bool TryGetLatest(out FaceSample sample)
    {
      lock (_lock)
      {
        sample = _latest;
        return _hasFace;
      }
    }

    private IEnumerator Start()
    {
      if (WebCamTexture.devices.Length == 0)
      {
        Debug.LogError("[FaceLandmarkerSource] No se encontró ninguna webcam.");
        yield break;
      }

      _webCamTexture = new WebCamTexture(FindDeviceName(), requestedWidth, requestedHeight, requestedFps);
      _webCamTexture.Play();

      // La textura no trae dimensiones reales hasta que llega el primer frame
      yield return new WaitUntil(() => _webCamTexture.width > 16);

#if UNITY_EDITOR
      IResourceManager resourceManager = new LocalResourceManager();
#else
      IResourceManager resourceManager = new StreamingAssetsResourceManager();
#endif
      yield return resourceManager.PrepareAssetAsync(modelFile, modelFile, false);

      var options = new FaceLandmarkerOptions(
        new Mediapipe.Tasks.Core.BaseOptions(Mediapipe.Tasks.Core.BaseOptions.Delegate.CPU, modelAssetPath: modelFile),
        runningMode: Mediapipe.Tasks.Vision.Core.RunningMode.LIVE_STREAM,
        numFaces: 1,
        minFaceDetectionConfidence: minFaceDetectionConfidence,
        minFacePresenceConfidence: minFacePresenceConfidence,
        minTrackingConfidence: minTrackingConfidence,
        resultCallback: OnFaceLandmarkerResult);
      _landmarker = FaceLandmarker.CreateFromOptions(options);

      _textureFramePool = new TextureFramePool(_webCamTexture.width, _webCamTexture.height, TextureFormat.RGBA32, 10);
      _stopwatch = Stopwatch.StartNew();
      IsRunning = true;
      Debug.Log($"[FaceLandmarkerSource] Webcam {_webCamTexture.deviceName} {_webCamTexture.width}x{_webCamTexture.height}");

      var processingOptions = new Mediapipe.Tasks.Vision.Core.ImageProcessingOptions(rotationDegrees: _webCamTexture.videoRotationAngle);
      AsyncGPUReadbackRequest req = default;
      var waitUntilReqDone = new WaitUntil(() => req.done);

      while (true)
      {
        // Solo se procesan frames nuevos de la webcam
        if (!_webCamTexture.didUpdateThisFrame || !_textureFramePool.TryGetTextureFrame(out var textureFrame))
        {
          yield return null;
          continue;
        }

        // MediaPipe espera origen arriba-izquierda y las texturas de Unity son abajo-izquierda:
        // se voltea en vertical, salvo que el driver ya entregue la imagen volteada
        req = textureFrame.ReadTextureAsync(_webCamTexture, false, !_webCamTexture.videoVerticallyMirrored);
        yield return waitUntilReqDone;

        if (req.hasError)
        {
          textureFrame.Release();
          Debug.LogWarning("[FaceLandmarkerSource] Falló la lectura del frame de la webcam.");
          yield return null;
          continue;
        }

        // LIVE_STREAM exige timestamps estrictamente crecientes
        var timestamp = System.Math.Max(_stopwatch.ElapsedMilliseconds, _lastTimestamp + 1);
        _lastTimestamp = timestamp;

        var image = textureFrame.BuildCPUImage();
        FrameReady?.Invoke(textureFrame, timestamp);
        textureFrame.Release();

        _landmarker.DetectAsync(image, timestamp, processingOptions);
      }
    }

    private void Update()
    {
      _fpsTimer += Time.unscaledDeltaTime;
      if (_fpsTimer >= 1f)
      {
        TrackerFps = Interlocked.Exchange(ref _resultCount, 0) / _fpsTimer;
        _fpsTimer = 0f;
      }
    }

    // Corre en un hilo de MediaPipe. El resultado se reutiliza entre llamadas, así que se copia lo necesario.
    private void OnFaceLandmarkerResult(FaceLandmarkerResult result, Mediapipe.Image image, long timestampMillisec)
    {
      Interlocked.Increment(ref _resultCount);

      var faces = result.faceLandmarks;
      var hasFace = faces != null && faces.Count > 0
        && faces[0].landmarks != null && faces[0].landmarks.Count > LeftIrisIndex;

      lock (_lock)
      {
        _hasFace = hasFace;
        if (!hasFace)
        {
          return;
        }

        var landmarks = faces[0].landmarks;
        _latest = new FaceSample
        {
          rightIris = ToVector2(landmarks[RightIrisIndex]),
          leftIris = ToVector2(landmarks[LeftIrisIndex]),
          rightEyeOuter = ToVector2(landmarks[RightEyeOuterIndex]),
          leftEyeOuter = ToVector2(landmarks[LeftEyeOuterIndex]),
          rightEyeInner = ToVector2(landmarks[RightEyeInnerIndex]),
          leftEyeInner = ToVector2(landmarks[LeftEyeInnerIndex]),
          noseTip = ToVector2(landmarks[NoseTipIndex]),
          timestampMillisec = timestampMillisec,
        };
      }
    }

    private static Vector2 ToVector2(NormalizedLandmark landmark) => new Vector2(landmark.x, landmark.y);

    private string FindDeviceName()
    {
      if (!string.IsNullOrEmpty(deviceName))
      {
        foreach (var device in WebCamTexture.devices)
        {
          if (device.name == deviceName)
          {
            return device.name;
          }
        }
        Debug.LogWarning($"[FaceLandmarkerSource] Webcam '{deviceName}' no encontrada, se usa la primera.");
      }
      return WebCamTexture.devices[0].name;
    }

    private void OnDestroy()
    {
      StopAllCoroutines();
      IsRunning = false;
      Stopping?.Invoke();

      _landmarker?.Close();
      _landmarker = null;

      _textureFramePool?.Dispose();
      _textureFramePool = null;

      if (_webCamTexture != null)
      {
        _webCamTexture.Stop();
        Destroy(_webCamTexture);
        _webCamTexture = null;
      }
    }
  }
}
