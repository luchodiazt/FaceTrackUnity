using UnityEngine;

namespace FaceTrack.HeadTracking
{
  /// <summary>
  /// Gira el producto como un tornamesa: se pellizca (pulgar + índice) frente a la cámara y se arrastra
  /// a los lados. Al soltar sigue girando con inercia y frena solo.
  /// </summary>
  public class ProductRotator : MonoBehaviour
  {
    [SerializeField] private HandGestureSource hands;

    [Tooltip("Lo que gira; su pivote debe estar bajo el centro del producto.")]
    [SerializeField] private Transform target;

    [Tooltip("Opcional: congela el tracking de cabeza mientras se rota, porque la mano frente a la cara mete ruido.")]
    [SerializeField] private HeadPoseEstimator headPose;

    [Header("Gesto")]
    [Tooltip("Grados de giro por un ancho completo de imagen recorrido por la mano.")]
    [SerializeField] private float degreesPerImageWidth = 360f;

    [Tooltip("La pinza se cierra cuando pulgar–índice baja de esta fracción del tamaño de la mano…")]
    [SerializeField, Range(0.1f, 1f)] private float pinchClose = 0.35f;

    [Tooltip("…y se abre cuando supera esta otra (histéresis para que no parpadee).")]
    [SerializeField, Range(0.1f, 1.5f)] private float pinchOpen = 0.5f;

    [Header("Movimiento")]
    [Tooltip("Rapidez con que el giro mostrado alcanza al de la mano (1/s). Filtra el temblor de los landmarks.")]
    [SerializeField] private float followSpeed = 18f;

    [Tooltip("Qué tan rápido frena el giro al soltar (1/s). Más alto = menos inercia.")]
    [SerializeField] private float inertiaDamping = 2.5f;

    [SerializeField] private float maxAngularSpeed = 540f;

    public bool HandVisible { get; private set; }
    public bool IsGrabbing { get; private set; }
    public Vector2 PinchPoint01 { get; private set; }
    public float Yaw => _shownYaw;

    private Quaternion _baseRotation;
    private float _yaw;
    private float _shownYaw;
    private float _angularVelocity;
    private Vector2 _lastPinch;
    private long _lastTimestamp = -1;

    private void Awake()
    {
      if (target != null)
      {
        _baseRotation = target.localRotation;
      }
    }

    private void Update()
    {
      if (target == null)
      {
        return;
      }

      var dt = Time.unscaledDeltaTime;
      ReadHand();

      if (!IsGrabbing)
      {
        _yaw += _angularVelocity * dt;
        _angularVelocity *= Mathf.Exp(-inertiaDamping * dt);
      }

      _shownYaw = Mathf.Lerp(_shownYaw, _yaw, 1f - Mathf.Exp(-followSpeed * dt));
      target.localRotation = Quaternion.Euler(0f, _shownYaw, 0f) * _baseRotation;

      if (headPose != null)
      {
        headPose.HoldPose = IsGrabbing;
      }
    }

    private void ReadHand()
    {
      if (hands == null || !hands.TryGetLatest(out var sample))
      {
        HandVisible = false;
        IsGrabbing = false;
        return;
      }

      HandVisible = true;
      if (sample.timestampMillisec == _lastTimestamp)
      {
        return;
      }

      var elapsed = _lastTimestamp < 0 ? 0f : (sample.timestampMillisec - _lastTimestamp) / 1000f;
      _lastTimestamp = sample.timestampMillisec;

      var ratio = sample.PinchRatio(hands.ImageAspect);
      var wasGrabbing = IsGrabbing;
      IsGrabbing = wasGrabbing ? ratio < pinchOpen : ratio < pinchClose;

      var pinch = sample.PinchPoint;
      PinchPoint01 = pinch;

      if (IsGrabbing && wasGrabbing)
      {
        // La imagen cruda no está en espejo: mano a la derecha de la persona = u baja.
        // Ese arrastre debe llevar la cara cercana del producto a la derecha, que en Unity es yaw negativo.
        var deltaYaw = (pinch.x - _lastPinch.x) * degreesPerImageWidth;
        _yaw += deltaYaw;
        if (elapsed > 0f)
        {
          var speed = Mathf.Clamp(deltaYaw / elapsed, -maxAngularSpeed, maxAngularSpeed);
          _angularVelocity = Mathf.Lerp(_angularVelocity, speed, 0.5f);
        }
      }
      else if (IsGrabbing)
      {
        // Recién pellizcó: se detiene la inercia que traía
        _angularVelocity = 0f;
      }

      _lastPinch = pinch;
    }

    private void OnDisable()
    {
      if (headPose != null)
      {
        headPose.HoldPose = false;
      }
    }
  }
}
