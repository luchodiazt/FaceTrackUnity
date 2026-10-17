using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FaceTrack.HeadTracking
{
  /// <summary>
  /// Overlay de debug y calibración (IMGUI).
  /// F1 muestra/oculta, F2 activa la simulación con mouse, ↑/↓ ajustan la sensibilidad (o el FOV en Metric),
  /// B cambia de escenario, C recentra la cara, Esc sale del build.
  /// </summary>
  public class HeadTrackingDebugOverlay : MonoBehaviour
  {
    [SerializeField] private OffAxisCamera offAxisCamera;
    [SerializeField] private HeadPoseEstimator headPose;
    [SerializeField] private StageSwitcher stageSwitcher;
    [SerializeField] private HandGestureSource hands;
    [SerializeField] private ProductRotator rotator;
    [SerializeField] private bool visible = true;
    [SerializeField] private float thumbnailWidth = 240f;

    private readonly StringBuilder _text = new StringBuilder();
    private GUIStyle _labelStyle;

    private void Update()
    {
      var keyboard = Keyboard.current;
      if (keyboard == null)
      {
        return;
      }

      if (keyboard.f1Key.wasPressedThisFrame)
      {
        visible = !visible;
      }

      // En el build a pantalla completa no hay barra de ventana
      if (keyboard.escapeKey.wasPressedThisFrame && !Application.isEditor)
      {
        Application.Quit();
      }

      if (headPose != null && keyboard.cKey.wasPressedThisFrame)
      {
        headPose.RecenterNeutral();
      }

      if (headPose != null)
      {
        // ↑/↓: sensibilidad en Reference, FOV de la webcam en Metric
        var step = keyboard.upArrowKey.wasPressedThisFrame ? 1f : keyboard.downArrowKey.wasPressedThisFrame ? -1f : 0f;
        if (step != 0f)
        {
          if (headPose.MappingMode == HeadMappingMode.Reference)
          {
            headPose.MovementScale += step * 0.25f;
          }
          else
          {
            headPose.WebcamHorizontalFov += step;
          }
        }
      }
    }

    private void OnGUI()
    {
      if (!visible)
      {
        return;
      }

      _labelStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true };

      var y = 10f;
      var source = headPose != null ? headPose.Source : null;
      var webCamTexture = source != null ? source.WebCamTexture : null;

      if (webCamTexture != null && webCamTexture.width > 16)
      {
        var height = thumbnailWidth * webCamTexture.height / webCamTexture.width;
        var rect = new Rect(10f, y, thumbnailWidth, height);

        // Se muestra en espejo, como lo ve la persona
        var flipV = webCamTexture.videoVerticallyMirrored;
        GUI.DrawTextureWithTexCoords(rect, webCamTexture, new Rect(1f, flipV ? 1f : 0f, -1f, flipV ? -1f : 1f));

        if (headPose.IsTracking && source.TryGetLatest(out var sample))
        {
          DrawLandmark(rect, sample.rightIris, Color.green);
          DrawLandmark(rect, sample.leftIris, Color.green);
          DrawLandmark(rect, sample.rightEyeOuter, Color.yellow);
          DrawLandmark(rect, sample.leftEyeOuter, Color.yellow);
        }

        if (rotator != null && rotator.HandVisible)
        {
          DrawLandmark(rect, rotator.PinchPoint01, rotator.IsGrabbing ? Color.magenta : Color.white);
        }
        y += height + 6f;
      }

      BuildText(source);
      var textHeight = _labelStyle.CalcHeight(new GUIContent(_text.ToString()), 380f);
      GUI.Box(new Rect(6f, y - 4f, 388f, textHeight + 8f), GUIContent.none);
      GUI.Label(new Rect(10f, y, 380f, textHeight), _text.ToString(), _labelStyle);
    }

    private void BuildText(FaceLandmarkerSource source)
    {
      _text.Clear();

      if (offAxisCamera != null && offAxisCamera.IsSimulating)
      {
        _text.AppendLine("<b>Modo:</b> <color=cyan>simulación (mouse + rueda)</color>");
      }
      else if (source == null || headPose == null)
      {
        _text.AppendLine("<b>Modo:</b> <color=red>sin tracker asignado</color>");
      }
      else if (!source.IsRunning)
      {
        _text.AppendLine("<b>Modo:</b> <color=orange>iniciando webcam… (sin webcam: F2 simula)</color>");
      }
      else
      {
        _text.AppendLine(headPose.IsTracking
          ? "<b>Modo:</b> <color=lime>tracking</color>"
          : "<b>Modo:</b> <color=orange>sin rostro, volviendo al reposo</color>");
      }

      if (source != null && source.IsRunning)
      {
        _text.AppendLine($"Tracker: {source.TrackerFps:0} fps · {source.ImageWidth}x{source.ImageHeight}");
      }

      if (headPose != null)
      {
        var face = headPose.FacePosition01;
        _text.AppendLine($"Mapeo: <b>{headPose.MappingMode}</b> · cara u {face.x:0.00} v {face.y:0.00}");

        if (headPose.MappingMode == HeadMappingMode.Metric)
        {
          var raw = headPose.RawEyePosition;
          _text.AppendLine($"Distancia a la pantalla: <b>{-raw.z * 100f:0} cm</b> (filtrada {-headPose.EyePosition.z * 100f:0} cm)");
          _text.AppendLine($"FOV webcam: {headPose.WebcamHorizontalFov:0}° (↑/↓ para calibrar)");
        }
        else
        {
          _text.AppendLine($"Sensibilidad: <b>{headPose.MovementScale:0.00}</b> (↑/↓) · ojo fijo a {-headPose.EyePosition.z * 100f:0} cm");
          var neutral = headPose.Neutral01;
          _text.AppendLine(headPose.IsCalibrating
            ? "<color=cyan>Calibrando centro… quédate quieto</color>"
            : $"Centro u {neutral.x:0.00} v {neutral.y:0.00} (C recentrar)");
        }
      }

      if (stageSwitcher != null)
      {
        _text.AppendLine($"Escenario: <b>{(stageSwitcher.Stage == StageMode.Free ? "libre" : "caja")}</b> (B)");
      }

      if (hands != null && rotator != null)
      {
        var state = !hands.IsRunning ? "<color=orange>iniciando…</color>"
          : rotator.IsGrabbing ? "<color=magenta>pinza: rotando</color>"
          : rotator.HandVisible ? "abierta (pellizca y arrastra)"
          : "no detectada";
        _text.AppendLine($"Mano: {state} · {hands.TrackerFps:0} fps · giro {rotator.Yaw:0}°");
      }

      if (offAxisCamera != null)
      {
        var eye = offAxisCamera.EyePosition * 100f;
        _text.AppendLine($"Ojo: X {eye.x:0.0}  Y {eye.y:0.0}  Z {eye.z:0.0} cm");

        var windowAspect = (float)Screen.width / Mathf.Max(Screen.height, 1);
        if (Mathf.Abs(windowAspect / offAxisCamera.ScreenAspect - 1f) > 0.02f)
        {
          _text.AppendLine($"<color=orange>Aspecto ventana {windowAspect:0.00} ≠ pantalla física {offAxisCamera.ScreenAspect:0.00}: la imagen se deforma</color>");
        }

        // El efecto asume que la imagen ocupa todo el panel físico (en el editor, Display.main reporta el Game view)
        var monitorWidth = Screen.currentResolution.width;
        if (Screen.width < monitorWidth * 0.9f)
        {
          _text.AppendLine($"<color=orange>Ventana {Screen.width}px de {monitorWidth}px: el efecto se ve reducido. Usa Play Maximized o un build a pantalla completa</color>");
        }
      }

      _text.Append("Pellizca y arrastra: rotar · F1 overlay · F2 simulación · B escenario · C centro · Esc salir");
    }

    // Los landmarks vienen en la imagen cruda (origen arriba-izquierda); la miniatura está en espejo
    private static void DrawLandmark(Rect rect, Vector2 landmark, Color color)
    {
      var x = rect.x + (1f - landmark.x) * rect.width;
      var y = rect.y + landmark.y * rect.height;
      var previous = GUI.color;
      GUI.color = color;
      GUI.DrawTexture(new Rect(x - 2.5f, y - 2.5f, 5f, 5f), Texture2D.whiteTexture);
      GUI.color = previous;
    }
  }
}
