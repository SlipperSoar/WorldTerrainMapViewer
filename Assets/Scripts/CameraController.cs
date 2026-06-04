using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CameraController : MonoBehaviour
{
    #region Properties

    [SerializeField] private Transform target;
    [SerializeField] private float distance = 10f;
    [SerializeField] private float minDistance = 2f;
    [SerializeField] private float maxDistance = 50f;
    [SerializeField] private float scrollSpeed = 5f;

    [SerializeField] private float rotationSpeed = 200f;
    [SerializeField] private float currentX = 0f;
    [SerializeField] private float currentY = 0f;
    [SerializeField] private float minY = -80f;
    [SerializeField] private float maxY = 80f;

    private Vector3 offset;

    #endregion

    void Start()
    {
        if (target == null)
        {
            Debug.LogWarning("CameraController: No target assigned!");
            return;
        }

        offset = transform.position - target.position;

        Vector3 angles = Quaternion.LookRotation(offset).eulerAngles;
        currentX = angles.y;
        currentY = angles.x;

        UpdateCameraPosition();
    }

    void LateUpdate()
    {
        if (target == null)
            return;

        HandleScrollWheel();
        HandleRightClickOrbit();
        UpdateCameraPosition();
    }

    private void HandleScrollWheel()
    {
        float scroll = Input.GetAxis("Mouse ScrollWheel");

        if (scroll != 0)
        {
            distance -= scroll * scrollSpeed;
            distance = Mathf.Clamp(distance, minDistance, maxDistance);
        }
    }

    private void HandleRightClickOrbit()
    {
        if (Input.GetMouseButton(1))
        {
            float mouseX = Input.GetAxis("Mouse X") * rotationSpeed * Time.deltaTime;
            float mouseY = Input.GetAxis("Mouse Y") * rotationSpeed * Time.deltaTime;

            currentX += mouseX;
            currentY -= mouseY;

            currentY = Mathf.Clamp(currentY, minY, maxY);
        }
    }

    private void UpdateCameraPosition()
    {
        if (target == null)
            return;

        Quaternion rotation = Quaternion.Euler(currentY, currentX, 0);
        Vector3 direction = new Vector3(0, 0, -distance);

        transform.position = target.position + rotation * direction;
        transform.LookAt(target);
    }

    public float GetHorizontalRotation()
    {
        return currentX % 360f;
    }

    public float GetVerticalRotation()
    {
        return currentY;
    }

    /// <summary>
    /// 赤道视角
    /// </summary>
    public void SetEquatorView()
    {
        currentY = 0f;
        UpdateCameraPosition();
    }

    /// <summary>
    /// 北极视角
    /// </summary>
    public void SetNorthPoleView()
    {
        currentY = maxY;
        UpdateCameraPosition();
    }

    /// <summary>
    /// 南极视角
    /// </summary>
    public void SetSouthPoleView()
    {
        currentY = minY;
        UpdateCameraPosition();
    }

    /// <summary>
    /// 垂直俯视
    /// </summary>
    public void SetTopDownView()
    {
        currentY = 90f;
        UpdateCameraPosition();
    }
}