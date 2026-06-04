using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class EarthManager : MonoBehaviour
{
    #region Properties

    public static EarthManager Instance { get; private set; }

    [SerializeField] private Renderer earthRenderer;

    #endregion

    void Awake()
    {
        Instance = this;
    }

    #region Public Methods

    public void SetEarthSurface(Texture2D texture)
    {
        if (earthRenderer != null)
        {
            Material material = earthRenderer.material;
            material.SetTexture("_MainTex", texture);

            Debug.Log($"Successfully loaded texture: {texture}");
            Debug.Log($"Texture size: {texture.width}x{texture.height}");
        }
        else
        {
            Debug.LogError("Earth Renderer is not assigned!");
        }
    }

    #endregion
}