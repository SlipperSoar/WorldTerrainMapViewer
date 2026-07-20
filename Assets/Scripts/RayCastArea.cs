using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System;

public class RayCastArea : Image
{
    #region Events

    public event Action onPointerEnter;
    public event Action onPointerExit;

    #endregion

    #region Properties

    [SerializeField] private bool showDebugInfo = false;

    private bool isPointerIn = false;

    #endregion

    protected override void Awake()
    {
        base.Awake();

        color = new Color(0, 0, 0, 0);
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
    }

    private void Update()
    {
        var mousePos = Input.mousePosition;
        if (RectTransformUtility.RectangleContainsScreenPoint(rectTransform, mousePos))
        {
            if (!isPointerIn)
            {
                OnPointerEnter();
                isPointerIn = true;
            }
        }
        else
        {
            if (isPointerIn)
            {
                OnPointerExit();
                isPointerIn = false;
            }
        }
    }

    public void OnPointerEnter()
    {
        if (showDebugInfo)
        {
            Debug.Log($"Mouse entered: {gameObject.name}");
        }

        onPointerEnter?.Invoke();
    }

    public void OnPointerExit()
    {
        if (showDebugInfo)
        {
            Debug.Log($"Mouse exited: {gameObject.name}");
        }

        onPointerExit?.Invoke();
    }

    public void AddListener(Action onEnter = null, Action onExit = null)
    {
        if (onEnter != null) onPointerEnter += onEnter;
        if (onExit != null) onPointerExit += onExit;
    }

    public void RemoveListener(Action onEnter = null, Action onExit = null)
    {
        if (onEnter != null) onPointerEnter -= onEnter;
        if (onExit != null) onPointerExit -= onExit;
    }

    protected override void OnDestroy()
    {
        onPointerEnter = null;
        onPointerExit = null;

        base.OnDestroy();
    }
}