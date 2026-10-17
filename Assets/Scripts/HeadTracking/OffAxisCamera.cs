using UnityEngine;
using UnityEngine.InputSystem;

namespace FaceTrack.HeadTracking
{
  /// <summary>
  /// Cámara off-axis: la pantalla física se comporta como una ventana.
  /// Va como hija del ScreenRig, cuyo plano XY local es el monitor (1 unidad = 1 m, escala 1).
  /// El contenido con Z positivo queda "detrás" de la pantalla; con Z negativo, asoma hacia el espectador.
  /// </summary>
  [ExecuteAlways]
  [RequireComponent(typeof(Camera))]
  public class OffAxisCamera : MonoBehaviour
  {
    [SerializeField] private HeadPoseEstimator headPose;

    [Header("Pantalla física (m)")]
    [Tooltip("Ancho visible del panel. 24\" 16:9 ≈ 0,531; 27\" ≈ 0,598.")]
    [SerializeField] private float screenWidth = 0.531f;
    [Tooltip("Alto visible del panel. 24\" 16:9 ≈ 0,299; 27\" ≈ 0,336.")]
    [SerializeField] private float screenHeight = 0.299f;

    [Header("Clipping")]
    [SerializeField] private float nearClip = 0.05f;
    [SerializeField] private float farClip = 20f;

    [Header("Simulación")]
    [Tooltip("El mouse mueve la cabeza (X/Y) y la rueda cambia la distancia. Para probar sin webcam. Tecla F2.")]
    [SerializeField] private bool simulateWithMouse;
    [Tooltip("Posición del ojo usada fuera de Play Mode, para encuadrar la escena.")]
    [SerializeField] private Vector3 editorEyePosition = new Vector3(0f, 0f, -0.5f);

    public float ScreenWidth => screenWidth;
    public float ScreenHeight => screenHeight;
    public float ScreenAspect => screenWidth / screenHeight;
    public bool IsSimulating => simulateWithMouse;
    public HeadPoseEstimator HeadPose => headPose;
    public Vector3 EyePosition { get; private set; }

    private Camera _camera;
    private float _simulatedDistance = 0.6f;

    private void OnEnable()
    {
      _camera = GetComponent<Camera>();

      // Tras un domain reload la matriz vuelve a la perspectiva normal hasta el próximo LateUpdate
      EyePosition = editorEyePosition;
      Apply(EyePosition);
    }

    private void OnDisable()
    {
      if (_camera != null)
      {
        _camera.ResetProjectionMatrix();
      }
    }

    private void LateUpdate()
    {
      if (Application.isPlaying)
      {
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.f2Key.wasPressedThisFrame)
        {
          simulateWithMouse = !simulateWithMouse;
        }

        if (simulateWithMouse)
        {
          EyePosition = ReadSimulatedEye();
        }
        else if (headPose != null)
        {
          EyePosition = headPose.EyePosition;
        }
        else
        {
          EyePosition = editorEyePosition;
        }
      }
      else
      {
        EyePosition = editorEyePosition;
      }

      Apply(EyePosition);
    }

    private void Apply(Vector3 eye)
    {
      // El ojo siempre queda delante del plano de la pantalla
      var distance = Mathf.Max(-eye.z, nearClip + 0.001f);
      var localPosition = new Vector3(eye.x, eye.y, -distance);

      // Solo se escribe si cambió, para no ensuciar la escena en modo edición
      if (transform.localPosition != localPosition)
      {
        transform.localPosition = localPosition;
      }
      if (transform.localRotation != Quaternion.identity)
      {
        transform.localRotation = Quaternion.identity;
      }

      // Frustum asimétrico: los bordes del near plane son la proyección de los bordes de la pantalla
      var scale = nearClip / distance;
      var left = (-screenWidth * 0.5f - eye.x) * scale;
      var right = (screenWidth * 0.5f - eye.x) * scale;
      var bottom = (-screenHeight * 0.5f - eye.y) * scale;
      var top = (screenHeight * 0.5f - eye.y) * scale;

      _camera.nearClipPlane = nearClip;
      _camera.farClipPlane = farClip;
      _camera.projectionMatrix = Matrix4x4.Frustum(left, right, bottom, top, nearClip, farClip);
    }

    private Vector3 ReadSimulatedEye()
    {
      var mouse = Mouse.current;
      if (mouse == null)
      {
        return editorEyePosition;
      }

      var scroll = mouse.scroll.ReadValue().y;
      if (Mathf.Abs(scroll) > 0.01f)
      {
        _simulatedDistance = Mathf.Clamp(_simulatedDistance - Mathf.Sign(scroll) * 0.05f, 0.25f, 2f);
      }

      var position = mouse.position.ReadValue();
      var normalizedX = Mathf.Clamp(position.x / Mathf.Max(Screen.width, 1) * 2f - 1f, -1f, 1f);
      var normalizedY = Mathf.Clamp(position.y / Mathf.Max(Screen.height, 1) * 2f - 1f, -1f, 1f);
      return new Vector3(normalizedX * screenWidth * 0.75f, normalizedY * screenHeight * 0.75f, -_simulatedDistance);
    }

    private void OnDrawGizmos()
    {
      var rig = transform.parent;
      if (rig == null)
      {
        return;
      }

      var halfWidth = screenWidth * 0.5f;
      var halfHeight = screenHeight * 0.5f;
      var corners = new[]
      {
        rig.TransformPoint(new Vector3(-halfWidth, -halfHeight, 0f)),
        rig.TransformPoint(new Vector3(halfWidth, -halfHeight, 0f)),
        rig.TransformPoint(new Vector3(halfWidth, halfHeight, 0f)),
        rig.TransformPoint(new Vector3(-halfWidth, halfHeight, 0f)),
      };

      // Rectángulo de la pantalla física
      Gizmos.color = Color.cyan;
      for (var i = 0; i < 4; i++)
      {
        Gizmos.DrawLine(corners[i], corners[(i + 1) % 4]);
      }

      // Rayos del ojo a las esquinas
      Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.6f);
      foreach (var corner in corners)
      {
        Gizmos.DrawLine(transform.position, corner);
      }
    }
  }
}
