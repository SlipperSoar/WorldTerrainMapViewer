using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public class MapItem : MonoBehaviour
{
    #region Properties

    [SerializeField] private Button btn;
    [SerializeField] private Text mapName;

    #endregion

    #region Public Methods

    public void Initialize(string mapName, UnityAction onClick)
    {
        this.mapName.text = mapName;
        btn.onClick.AddListener(onClick);
    }

    #endregion
}
