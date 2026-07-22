using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CameraController : MonoBehaviour
{
    #region Properties

    [SerializeField] private SunLightController sunLightController;

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

    private Quaternion orbitRotation = Quaternion.identity;
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

        // Initialize orbit rotation from current camera offset relative to target.
        orbitRotation = Quaternion.LookRotation(-offset.normalized, Vector3.up);

        UpdateDisplayAngles();
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

            // Incremental quaternion rotation — avoids gimbal lock at poles:
            // Yaw around world up (horizontal orbit, axis always well-defined)
            // Pitch around camera local right (vertical orbit, axis always well-defined)
            Quaternion yaw = Quaternion.AngleAxis(mouseX, Vector3.up);
            Quaternion pitch = Quaternion.AngleAxis(-mouseY, Vector3.right);

            Quaternion candidate = yaw * orbitRotation * pitch;

            // Prevent the camera from flipping past the poles by ensuring
            // the camera's up vector remains in the upper hemisphere
            Vector3 camUp = candidate * Vector3.up;
            if (camUp.y >= 0f)
            {
                orbitRotation = candidate;
            }
            else
            {
                // At pole limit: apply yaw only, skip the pitch that would flip
                orbitRotation = yaw * orbitRotation;
            }

            UpdateDisplayAngles();
        }
    }

    private void UpdateDisplayAngles()
    {
        Vector3 angles = orbitRotation.eulerAngles;
        currentX = angles.y;
        currentY = angles.x > 180f ? angles.x - 360f : angles.x;
    }

    private void UpdateCameraPosition()
    {
        if (target == null)
            return;

        Vector3 direction = new Vector3(0, 0, -distance);
        transform.position = target.position + orbitRotation * direction;
        // Set rotation directly from orbitRotation — avoids LookAt gimbal lock at poles
        transform.rotation = orbitRotation;
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
    /// 赤道视角（世界空间水平视角，与星球倾角无关）
    /// </summary>
    public void SetEquatorView()
    {
        orbitRotation = Quaternion.identity;
        UpdateDisplayAngles();
        UpdateCameraPosition();
    }

    /// <summary>
    /// 北极视角（世界空间顶部俯视，与星球倾角无关）
    /// </summary>
    public void SetNorthPoleView()
    {
        orbitRotation = Quaternion.Euler(90f, 0f, 0f);
        UpdateDisplayAngles();
        UpdateCameraPosition();
    }

    /// <summary>
    /// 南极视角（世界空间底部仰视，与星球倾角无关）
    /// </summary>
    public void SetSouthPoleView()
    {
        orbitRotation = Quaternion.Euler(-90f, 0f, 0f);
        UpdateDisplayAngles();
        UpdateCameraPosition();
    }

    /// <summary>
    /// 垂直俯视
    /// </summary>
    public void SetTopDownView()
    {
        orbitRotation = Quaternion.Euler(90f, 0f, 0f);
        UpdateDisplayAngles();
        UpdateCameraPosition();
    }
}
