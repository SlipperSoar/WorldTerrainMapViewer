using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class SunlightDirectionController : MonoBehaviour
{
    [SerializeField] private Light sunLight;

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
        // Child at local (-5,0,0) = sunDirection side (where the sun is).
        transform.rotation = Quaternion.LookRotation(lightDirection, Vector3.up) * Quaternion.Euler(0f, -90f, 0f);
    }
}
