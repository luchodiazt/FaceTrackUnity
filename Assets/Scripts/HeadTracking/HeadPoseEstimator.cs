using System;
using UnityEngine;

namespace FaceTrack.HeadTracking
{
  public enum HeadMappingMode
  {
    /// <summary>
    /// Estilizado, basado en el demo off-axis-sneaker: el desplazamiento de la cara respecto a su centro
    /// calibrado mueve el ojo en proporción a la pantalla, con límites suaves y distancia fija.
    /// No depende de dónde esté la cámara.
    /// </summary>
    Reference,

    /// <summary>
    /// Metros reales: distancia entre iris, FOV de la webcam y su posición física respecto a la pantalla.
    /// </summary>
    Metric,
  }

  /// <summary>
  /// Parámetros del modo Reference. Responde lineal cerca del centro y se satura suave en los límites,
  /// así pequeños movimientos se notan pero el frustum nunca se vuelve tan oblicuo que deforme el objeto.
  /// </summary>
  [Serializable]
  public class ReferenceMapping
  {
    [Tooltip("Cuánto se mueve el ojo virtual por unidad de imagen, en tamaños de pantalla. Demo: 1,5; más alto = basta mover menos la cabeza.")]
    [Range(0.5f, 6f)] public float movementScale = 3f;

    [Tooltip("Fracción de la sensibilidad aplicada en vertical: mover la cabeza arriba/abajo es menos natural.")]
    [Range(0f, 1.5f)] public float verticalGain = 0.6f;

    [Tooltip("Distancia fija del ojo a la pantalla (m). Demo: 0,5.")]
    [Range(0.2f, 1.5f)] public float eyeDistance = 0.5f;

    [Tooltip("Altura del ojo en reposo respecto al centro de la pantalla (m). Positivo = el producto se ve un poco desde arriba.")]
    [Range(-0.1f, 0.2f)] public float eyeHeight = 0.03f;

    [Tooltip("Ángulo máximo hacia los lados. Más allá el frustum se vuelve muy oblicuo y el objeto se deforma.")]
    [Range(5f, 60f)] public float maxSideAngle = 25f;

    [Tooltip("Ángulo máximo hacia arriba.")]
    [Range(0f, 45f)] public float maxUpAngle = 20f;

    [Tooltip("Ángulo máximo hacia abajo. Bajo para que nunca se mire desde debajo del piso.")]
    [Range(0f, 30f)] public float maxDownAngle = 6f;

    public Vector3 RestEyePosition => new Vector3(0f, eyeHeight, -eyeDistance);

    /// <summary>
    /// <paramref name="faceOffset"/>: desplazamiento de la cara respecto al centro calibrado,
    /// en unidades de la imagen cruda de la webcam (Y hacia abajo).
    /// </summary>
    public Vector3 EyePosition(Vector2 faceOffset, float screenWidth, float screenHeight)
    {
      // La webcam mira al espectador: la derecha de su imagen es la izquierda de la pantalla
      var x = -faceOffset.x * screenWidth * movementScale;
      var y = -faceOffset.y * screenHeight * movementScale * verticalGain;

      var maxSide = eyeDistance * Mathf.Tan(maxSideAngle * Mathf.Deg2Rad);
      var maxVertical = eyeDistance * Mathf.Tan((y >= 0f ? maxUpAngle : maxDownAngle) * Mathf.Deg2Rad);

      return new Vector3(SoftLimit(x, maxSide), eyeHeight + SoftLimit(y, maxVertical), -eyeDistance);
    }

    // Lineal cerca de 0 y se aplana al acercarse al límite, sin topes bruscos
    private static float SoftLimit(float value, float limit) =>
      limit <= 0f ? 0f : limit * (float)Math.Tanh(value / limit);
  }

  /// <summary>
  /// Convierte los landmarks en la posición del ojo, en el espacio de la pantalla:
  /// origen en el centro del monitor, +X derecha, +Y arriba, +Z hacia dentro de la pantalla.
  /// El espectador queda en Z negativo.
  /// </summary>
  public class HeadPoseEstimator : MonoBehaviour
  {
    private const string MovementScaleKey = "FaceTrack.movementScale";

    [SerializeField] private FaceLandmarkerSource source;

    [Tooltip("De aquí se leen las medidas físicas de la pantalla.")]
    [SerializeField] private OffAxisCamera offAxisCamera;

    [SerializeField] private HeadMappingMode mappingMode = HeadMappingMode.Reference;

    [Header("Modo Reference")]
    [SerializeField] private ReferenceMapping reference = new ReferenceMapping();

    [Tooltip("Segundos promediando la posición de la cara para fijar su centro.")]
    [SerializeField] private float neutralCaptureSeconds = 0.8f;

    [Tooltip("Si no hay nadie durante este tiempo, el próximo rostro recalibra el centro (útil en kiosco).")]
    [SerializeField] private float recenterAfterAbsence = 3f;

    [Header("Modo Metric")]
    [Tooltip("FOV horizontal de la webcam en grados. Calibrar con huincha hasta que la distancia del overlay coincida.")]
    [SerializeField, Range(30f, 120f)] private float webcamHorizontalFov = 70f;

    [Tooltip("Posición del lente de la webcam respecto al centro de la pantalla (m).")]
    [SerializeField] private Vector3 webcamOffset = new Vector3(0f, 0.16f, 0f);

    [Tooltip("Distancia entre pupilas (m). Promedio adulto ≈ 0,063.")]
    [SerializeField, Range(0.05f, 0.075f)] private float interpupillaryDistance = 0.063f;

    [Tooltip("Distancia mínima y máxima aceptada entre el ojo y la pantalla (m).")]
    [SerializeField] private Vector2 distanceRange = new Vector2(0.25f, 2f);

    [Tooltip("Multiplica el desplazamiento lateral del ojo.")]
    [SerializeField, Range(1f, 3f)] private float lateralGain = 1f;

    [Header("Suavizado")]
    [SerializeField] private OneEuroParams lateralSmoothing = new OneEuroParams(1f, 4f, 1f);

    [Tooltip("Solo afecta al modo Metric: ahí la profundidad sale de ~50 px entre iris y es ruidosa.")]
    [SerializeField] private OneEuroParams depthSmoothing = new OneEuroParams(0.5f, 1.5f, 1f);

    [Header("Pérdida de rostro")]
    [Tooltip("Segundos que se mantiene la última pose antes de volver al reposo.")]
    [SerializeField] private float holdSeconds = 0.3f;

    /// <summary>Posición filtrada del ojo, lista para la cámara.</summary>
    public Vector3 EyePosition { get; private set; }

    /// <summary>Última medición sin filtrar.</summary>
    public Vector3 RawEyePosition { get; private set; }

    /// <summary>Centro de la cara en la imagen cruda (0–1, origen arriba-izquierda).</summary>
    public Vector2 FacePosition01 { get; private set; } = new Vector2(0.5f, 0.5f);

    /// <summary>Centro calibrado de la cara (modo Reference).</summary>
    public Vector2 Neutral01 { get; private set; } = new Vector2(0.5f, 0.5f);

    public bool IsCalibrating { get; private set; }

    /// <summary>Mientras es true se ignoran mediciones nuevas y se mantiene la vista (p. ej. al rotar con la mano).</summary>
    public bool HoldPose { get; set; }
    public bool IsTracking { get; private set; }
    public bool IsAvailable => source != null && source.IsRunning;
    public FaceLandmarkerSource Source => source;
    public HeadMappingMode MappingMode => mappingMode;

    public float WebcamHorizontalFov
    {
      get => webcamHorizontalFov;
      set => webcamHorizontalFov = Mathf.Clamp(value, 30f, 120f);
    }

    /// <summary>Sensibilidad del modo Reference. Se guarda para el próximo inicio del build.</summary>
    public float MovementScale
    {
      get => reference.movementScale;
      set
      {
        reference.movementScale = Mathf.Clamp(value, 0.5f, 6f);
        PlayerPrefs.SetFloat(MovementScaleKey, reference.movementScale);
      }
    }

    private float ScreenWidth => offAxisCamera != null ? offAxisCamera.ScreenWidth : 0.531f;
    private float ScreenHeight => offAxisCamera != null ? offAxisCamera.ScreenHeight : 0.299f;

    private readonly OneEuroFilter _filterX = new OneEuroFilter();
    private readonly OneEuroFilter _filterY = new OneEuroFilter();
    private readonly OneEuroFilter _filterZ = new OneEuroFilter();
    private long _lastTimestamp = -1;
    private float _lastSeenTime = float.NegativeInfinity;
    private float _calibrationEndTime;
    private Vector2 _calibrationSum;
    private int _calibrationCount;

    /// <summary>Vuelve a medir el centro de la cara con la pose actual.</summary>
    public void RecenterNeutral() => BeginCalibration(Time.unscaledTime);

    private void Awake()
    {
      // En el build no hay Inspector: se recupera lo ajustado en vivo. En el editor manda el Inspector.
      if (!Application.isEditor)
      {
        reference.movementScale = Mathf.Clamp(PlayerPrefs.GetFloat(MovementScaleKey, reference.movementScale), 0.5f, 6f);
      }

      EyePosition = reference.RestEyePosition;
      RawEyePosition = reference.RestEyePosition;
    }

    private void Update()
    {
      var now = Time.unscaledTime;

      if (HoldPose)
      {
        // Cuenta como presente: al soltar no debe recalibrar el centro por "ausencia"
        _lastSeenTime = now;
      }
      else if (IsAvailable && source.TryGetLatest(out var sample) && sample.timestampMillisec != _lastTimestamp)
      {
        _lastTimestamp = sample.timestampMillisec;
        if (mappingMode == HeadMappingMode.Reference)
        {
          UpdateReference(sample, now);
          _lastSeenTime = now;
        }
        else if (TryEstimateMetric(sample, out var eye))
        {
          RawEyePosition = eye;
          _lastSeenTime = now;
        }
      }

      IsTracking = now - _lastSeenTime <= holdSeconds;
      var target = IsTracking ? RawEyePosition : reference.RestEyePosition;

      // Se filtra cada frame contra la última medición: así también se suavizan los saltos de 30 Hz de la webcam
      var dt = Time.unscaledDeltaTime;
      EyePosition = new Vector3(
        _filterX.Filter(target.x, dt, lateralSmoothing),
        _filterY.Filter(target.y, dt, lateralSmoothing),
        _filterZ.Filter(target.z, dt, depthSmoothing));
    }

    private void UpdateReference(in FaceSample sample, float now)
    {
      // Lagrimales + punta de la nariz: puntos estables que casi no cambian con la expresión
      var face = (sample.rightEyeInner + sample.leftEyeInner + sample.noseTip) / 3f;
      FacePosition01 = face;

      // Primer rostro, o alguien nuevo tras un rato sin nadie: su pose actual pasa a ser el centro
      if (now - _lastSeenTime > recenterAfterAbsence)
      {
        BeginCalibration(now);
      }

      if (IsCalibrating)
      {
        _calibrationSum += face;
        _calibrationCount++;
        if (now >= _calibrationEndTime)
        {
          Neutral01 = _calibrationSum / _calibrationCount;
          IsCalibrating = false;
        }
        RawEyePosition = reference.RestEyePosition;
        return;
      }

      RawEyePosition = reference.EyePosition(face - Neutral01, ScreenWidth, ScreenHeight);
    }

    private void BeginCalibration(float now)
    {
      IsCalibrating = true;
      _calibrationEndTime = now + neutralCaptureSeconds;
      _calibrationSum = Vector2.zero;
      _calibrationCount = 0;
    }

    private bool TryEstimateMetric(in FaceSample sample, out Vector3 eye)
    {
      if (!TryEstimateEyePosition(sample, source.ImageWidth, source.ImageHeight, webcamHorizontalFov, interpupillaryDistance, out eye))
      {
        return false;
      }
      FacePosition01 = (sample.rightIris + sample.leftIris) * 0.5f;
      eye += webcamOffset;
      eye.x *= lateralGain;
      eye.y *= lateralGain;
      eye.z = -Mathf.Clamp(-eye.z, distanceRange.x, distanceRange.y);
      return true;
    }

    /// <summary>
    /// Estima el punto medio entre los ojos respecto al lente de la webcam, en ejes de pantalla (m).
    /// Modelo pinhole con punto principal al centro y la distancia entre pupilas como referencia de escala.
    /// </summary>
    public static bool TryEstimateEyePosition(in FaceSample sample, int imageWidth, int imageHeight,
      float horizontalFovDegrees, float interpupillaryDistance, out Vector3 eye)
    {
      eye = default;
      if (imageWidth <= 0 || imageHeight <= 0)
      {
        return false;
      }

      var imageSize = new Vector2(imageWidth, imageHeight);
      var rightIris = Vector2.Scale(sample.rightIris, imageSize);
      var leftIris = Vector2.Scale(sample.leftIris, imageSize);
      var irisDistancePx = Vector2.Distance(rightIris, leftIris);
      if (irisDistancePx < 1f)
      {
        return false;
      }

      var focalPx = imageWidth * 0.5f / Mathf.Tan(horizontalFovDegrees * 0.5f * Mathf.Deg2Rad);
      var depth = focalPx * interpupillaryDistance / irisDistancePx;

      var mid = (rightIris + leftIris) * 0.5f;
      var cameraX = (mid.x - imageWidth * 0.5f) * depth / focalPx;  // hacia la derecha de la imagen cruda
      var cameraY = (mid.y - imageHeight * 0.5f) * depth / focalPx; // hacia abajo

      // La webcam mira al espectador: la derecha de su imagen es la izquierda de la pantalla
      eye = new Vector3(-cameraX, -cameraY, -depth);
      return true;
    }
  }
}
