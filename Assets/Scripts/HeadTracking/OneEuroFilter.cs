using System;
using UnityEngine;

namespace FaceTrack.HeadTracking
{
  /// <summary>
  /// Parámetros del filtro One Euro (Casiez et al., 2012).
  /// minCutoff baja = menos jitter en reposo; beta alta = menos lag en movimientos rápidos.
  /// </summary>
  [Serializable]
  public class OneEuroParams
  {
    [Tooltip("Frecuencia de corte mínima (Hz). Más baja = más suave en reposo.")]
    public float minCutoff = 1f;

    [Tooltip("Cuánto sube el corte con la velocidad. Más alta = menos lag al moverse.")]
    public float beta = 1f;

    [Tooltip("Frecuencia de corte para la derivada (Hz).")]
    public float dCutoff = 1f;

    public OneEuroParams() { }

    public OneEuroParams(float minCutoff, float beta, float dCutoff)
    {
      this.minCutoff = minCutoff;
      this.beta = beta;
      this.dCutoff = dCutoff;
    }
  }

  /// <summary>
  /// Filtro One Euro para un valor escalar. Filtra fuerte cuando la señal está quieta
  /// y se abre cuando se mueve rápido, así corta el jitter sin agregar lag.
  /// </summary>
  public class OneEuroFilter
  {
    private bool _initialized;
    private float _value;
    private float _derivative;

    public float Filter(float value, float deltaTime, OneEuroParams p)
    {
      if (!_initialized || deltaTime <= 0f)
      {
        _initialized = true;
        _value = value;
        _derivative = 0f;
        return value;
      }

      var rawDerivative = (value - _value) / deltaTime;
      _derivative = Mathf.Lerp(_derivative, rawDerivative, Alpha(p.dCutoff, deltaTime));

      var cutoff = p.minCutoff + p.beta * Mathf.Abs(_derivative);
      _value = Mathf.Lerp(_value, value, Alpha(cutoff, deltaTime));
      return _value;
    }

    public void Reset() => _initialized = false;

    private static float Alpha(float cutoff, float deltaTime)
    {
      var tau = 1f / (2f * Mathf.PI * Mathf.Max(cutoff, 1e-4f));
      return 1f / (1f + tau / deltaTime);
    }
  }
}
