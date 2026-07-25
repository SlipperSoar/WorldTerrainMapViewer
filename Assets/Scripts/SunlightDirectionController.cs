using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class SunlightDirectionController : MonoBehaviour
{
    [SerializeField] private Light sunLight;
    [SerializeField] private bool showArrow = true;

    private GameObject _arrowObject;
    private bool _cachedShowArrow;

#if UNITY_EDITOR
    private void OnEnable()
    {
        if (!Application.isPlaying)
            EditorApplication.update += EditorUpdate;
    }

    private void OnDisable()
    {
        if (!Application.isPlaying)
            EditorApplication.update -= EditorUpdate;
    }

    private void EditorUpdate()
    {
        if (Application.isPlaying) return;
        UpdateArrow();
    }
#endif

    private void LateUpdate()
    {
        UpdateArrow();
    }

    private void UpdateArrow()
    {
        if (sunLight == null) return;

        Vector3 lightDirection = sunLight.transform.forward;

        // Arrow points along local +X = lightDirection (toward Earth / sunlit side).
        // Child at local (-15,0,0) = sunDirection side (where the sun is).
        transform.rotation = Quaternion.LookRotation(lightDirection, Vector3.up) * Quaternion.Euler(0f, -90f, 0f);

        if (_cachedShowArrow != showArrow)
        {
            _cachedShowArrow = showArrow;
            if (_arrowObject == null)
                _arrowObject = transform.GetChild(0).gameObject;
            _arrowObject.SetActive(showArrow);
        }
    }

    public void SetArrowVisible(bool visible)
    {
        showArrow = visible;
    }
}
