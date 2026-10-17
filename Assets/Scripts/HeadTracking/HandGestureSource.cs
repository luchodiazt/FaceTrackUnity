using System.Collections;
using System.Threading;
using Mediapipe.Tasks.Components.Containers;
using Mediapipe.Tasks.Vision.HandLandmarker;
using Mediapipe.Unity;
using Mediapipe.Unity.Experimental;
using UnityEngine;

namespace FaceTrack.HeadTracking
{
  /// <summary>
  /// Landmarks de una mano, en coordenadas normalizadas de la imagen cruda de la webcam
  /// (origen arriba-izquierda, sin espejo).
  /// </summary>
  public struct HandSample
  {
    public Vector2 wrist;      // landmark 0
    public Vector2 thumbTip;   // landmark 4
    public Vector2 indexTip;   // landmark 8
    public Vector2 middleBase; // landmark 9
    public long timestampMillisec;

    public Vector2 PinchPoint => (thumbTip + indexTip) * 0.5f;

    /// <summary>
    /// Distancia pulgar–índice relativa al tamaño de la mano (muñeca → base del medio),
    /// así no depende de qué tan lejos esté la mano. <paramref name="aspect"/> = ancho/alto de la imagen.
    /// </summary>
    public float PinchRatio(float aspect)
    {
      var scale = new Vector2(aspect, 1f);
      var pinch = Vector2.Distance(Vector2.Scale(thumbTip, scale), Vector2.Scale(indexTip, scale));
      var hand = Vector2.Distance(Vector2.Scale(wrist, scale), Vector2.Scale(middleBase, scale));
      return pinch / Mathf.Max(hand, 1e-4f);
    }
  }

  /// <summary>
  /// Corre MediaPipe HandLandmarker (LIVE_STREAM) sobre los mismos frames que usa la cara,
  /// sin abrir la webcam otra vez.
  /// </summary>
  public class HandGestureSource : MonoBehaviour
  {
    private const int WristIndex = 0;
    private const int ThumbTipIndex = 4;
    private const int IndexTipIndex = 8;
    private const int MiddleBaseIndex = 9;

    [SerializeField] private FaceLandmarkerSource frameSource;
    [SerializeField] private string modelFile = "hand_landmarker.bytes";
    [SerializeField, Range(0f, 1f)] private float minHandDetectionConfidence = 0.5f;
    [SerializeField, Range(0f, 1f)] private float minHandPresenceConfidence = 0.5f;
    [SerializeField, Range(0f, 1f)] private float minTrackingConfidence = 0.5f;

    [Tooltip("Procesar 1 de cada N frames: la mano no necesita 30 fps y así queda CPU para la cara.")]
    [SerializeField, Range(1, 4)] private int frameStride = 2;

    public bool IsRunning { get; private set; }
    public float TrackerFps { get; private set; }

    /// <summary>Ancho/alto de la imagen de la webcam.</summary>
    public float ImageAspect => frameSource != null && frameSource.ImageHeight > 0
      ? (float)frameSource.ImageWidth / frameSource.ImageHeight
      : 4f / 3f;

    // El callback de MediaPipe llega en otro hilo: todo lo que comparte pasa por este lock
    private readonly object _lock = new object();
    private HandSample _latest;
    private bool _hasHand;
    private int _resultCount;
    private float _fpsTimer;
    private int _frameCounter;

    private HandLandmarker _landmarker;
    private Mediapipe.Tasks.Vision.Core.ImageProcessingOptions _processingOptions;

    public bool TryGetLatest(out HandSample sample)
    {
      lock (_lock)
      {
        sample = _latest;
        return _hasHand;
      }
    }

    private IEnumerator Start()
    {
      if (frameSource == null)
      {
        Debug.LogError("[HandGestureSource] Falta asignar frameSource.");
        yield break;
      }

      // La cara ya preparó la webcam y el resolver de recursos
      yield return new WaitUntil(() => frameSource.IsRunning);

#if UNITY_EDITOR
      IResourceManager resourceManager = new LocalResourceManager();
#else
      IResourceManager resourceManager = new StreamingAssetsResourceManager();
#endif
      yield return resourceManager.PrepareAssetAsync(modelFile, modelFile, false);

      var options = new HandLandmarkerOptions(
        new Mediapipe.Tasks.Core.BaseOptions(Mediapipe.Tasks.Core.BaseOptions.Delegate.CPU, modelAssetPath: modelFile),
        runningMode: Mediapipe.Tasks.Vision.Core.RunningMode.LIVE_STREAM,
        numHands: 1,
        minHandDetectionConfidence: minHandDetectionConfidence,
        minHandPresenceConfidence: minHandPresenceConfidence,
        minTrackingConfidence: minTrackingConfidence,
        resultCallback: OnHandLandmarkerResult);
      _landmarker = HandLandmarker.CreateFromOptions(options);
      _processingOptions = new Mediapipe.Tasks.Vision.Core.ImageProcessingOptions(rotationDegrees: frameSource.WebCamTexture.videoRotationAngle);

      frameSource.FrameReady += OnFrameReady;
      frameSource.Stopping += Shutdown;
      IsRunning = true;
    }

    private void OnFrameReady(TextureFrame frame, long timestampMillisec)
    {
      if (_landmarker == null || ++_frameCounter % frameStride != 0)
      {
        return;
      }
      _landmarker.DetectAsync(frame.BuildCPUImage(), timestampMillisec, _processingOptions);
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
    private void OnHandLandmarkerResult(HandLandmarkerResult result, Mediapipe.Image image, long timestampMillisec)
    {
      Interlocked.Increment(ref _resultCount);

      var hands = result.handLandmarks;
      var hasHand = hands != null && hands.Count > 0
        && hands[0].landmarks != null && hands[0].landmarks.Count > MiddleBaseIndex;

      lock (_lock)
      {
        _hasHand = hasHand;
        if (!hasHand)
        {
          return;
        }

        var landmarks = hands[0].landmarks;
        _latest = new HandSample
        {
          wrist = ToVector2(landmarks[WristIndex]),
          thumbTip = ToVector2(landmarks[ThumbTipIndex]),
          indexTip = ToVector2(landmarks[IndexTipIndex]),
          middleBase = ToVector2(landmarks[MiddleBaseIndex]),
          timestampMillisec = timestampMillisec,
        };
      }
    }

    private static Vector2 ToVector2(NormalizedLandmark landmark) => new Vector2(landmark.x, landmark.y);

    private void OnDestroy() => Shutdown();

    // Puede llamarse dos veces (Stopping de la fuente y OnDestroy propio)
    private void Shutdown()
    {
      if (frameSource != null)
      {
        frameSource.FrameReady -= OnFrameReady;
        frameSource.Stopping -= Shutdown;
      }
      IsRunning = false;
      _landmarker?.Close();
      _landmarker = null;
    }
  }
}
