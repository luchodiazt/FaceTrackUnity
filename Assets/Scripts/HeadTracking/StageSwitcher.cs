using UnityEngine;
using UnityEngine.InputSystem;

namespace FaceTrack.HeadTracking
{
  public enum StageMode
  {
    /// <summary>Sin paredes: el producto flota sobre un piso que se pierde en la oscuridad y cruza el plano de la pantalla.</summary>
    Free,

    /// <summary>Vitrina con grilla, como el demo off-axis-sneaker.</summary>
    Box,
  }

  /// <summary>
  /// Alterna entre escenarios con la tecla B y recoloca el producto para cada uno.
  /// La elección se guarda para el próximo inicio del build.
  /// </summary>
  public class StageSwitcher : MonoBehaviour
  {
    private const string StageKey = "FaceTrack.stage";

    [SerializeField] private StageMode stage = StageMode.Free;

    [Header("Objetos de cada escenario")]
    [SerializeField] private GameObject[] freeObjects;
    [SerializeField] private GameObject[] boxObjects;

    [Header("Producto")]
    [SerializeField] private Transform product;
    [Tooltip("Base del producto en el escenario libre: cruzando el plano de la pantalla.")]
    [SerializeField] private Vector3 productPositionFree = new Vector3(0f, -0.09f, -0.02f);
    [Tooltip("Base del producto dentro de la vitrina: apoyado en el piso, detrás de la pantalla.")]
    [SerializeField] private Vector3 productPositionBox = new Vector3(0f, -0.1495f, 0.134f);

    public StageMode Stage => stage;

    private void Awake()
    {
      if (!Application.isEditor && PlayerPrefs.HasKey(StageKey))
      {
        stage = (StageMode)PlayerPrefs.GetInt(StageKey);
      }
      Apply();
    }

    private void Update()
    {
      var keyboard = Keyboard.current;
      if (keyboard != null && keyboard.bKey.wasPressedThisFrame)
      {
        stage = stage == StageMode.Free ? StageMode.Box : StageMode.Free;
        PlayerPrefs.SetInt(StageKey, (int)stage);
        Apply();
      }
    }

    private void Apply()
    {
      SetActive(freeObjects, stage == StageMode.Free);
      SetActive(boxObjects, stage == StageMode.Box);

      if (product != null)
      {
        product.position = stage == StageMode.Free ? productPositionFree : productPositionBox;
      }
    }

    private static void SetActive(GameObject[] objects, bool active)
    {
      if (objects == null)
      {
        return;
      }
      foreach (var go in objects)
      {
        if (go != null)
        {
          go.SetActive(active);
        }
      }
    }
  }
}
