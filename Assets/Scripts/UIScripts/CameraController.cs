using UnityEngine;
using UnityEngine.Rendering.Universal;

public class CameraController : MonoBehaviour
{
    public float scrollSpeed = 20f; // Speed of the camera movement
    public float mouseSpeed = 150f;
    public float smoothCenterSpeed = 15f; // Speed of smooth centering
    public Vector2 minBounds; // Minimum x and y positions for the camera
    public Vector2 maxBounds; // Maximum x and y positions for the camera

    private float moveX;
    private float moveY;
    private float scroll;
    private bool allowCameraMovement = true;

    public Transform target = null;

    private Vector3 centerTarget;
    private bool isSmoothCentering = false;

    void Update()
    {
        if (!allowCameraMovement)
            return;

        if (isSmoothCentering)
        {
            UpdateCameraSmoothing();
        }
        else if (target == null)
        {
            MoveCamera();
        }
        else
        {
            FollowTarget();
        }

        ZoomCamera();
    }

    void FollowTarget()
    {
        Vector3 targetPosition = new Vector3(target.position.x, target.position.y, transform.position.z);
        transform.position = Vector3.Lerp(
            transform.position,
            targetPosition,
            Time.unscaledDeltaTime * scrollSpeed // Immune to time scale
        );
    }

    void MoveCamera()
    {
        // Use raw input for keyboard movement
        moveX = Input.GetAxisRaw("Horizontal");
        moveY = Input.GetAxisRaw("Vertical");

        Vector3 newPosition = transform.position +
            new Vector3(moveX, moveY, 0) * scrollSpeed * Time.unscaledDeltaTime; // Immune to time scale

        newPosition.x = Mathf.Clamp(newPosition.x, minBounds.x, maxBounds.x);
        newPosition.y = Mathf.Clamp(newPosition.y, minBounds.y, maxBounds.y);

        transform.position = newPosition;

        if (Input.GetMouseButton(1)) // Right-click drag
        {
            moveX = -Input.GetAxisRaw("Mouse X"); // Use raw input for mouse movement
            moveY = -Input.GetAxisRaw("Mouse Y"); // Use raw input for mouse movement

            newPosition = transform.position +
                new Vector3(moveX, moveY, 0) * mouseSpeed * Time.unscaledDeltaTime; // Immune to time scale

            newPosition.x = Mathf.Clamp(newPosition.x, minBounds.x, maxBounds.x);
            newPosition.y = Mathf.Clamp(newPosition.y, minBounds.y, maxBounds.y);

            transform.position = newPosition;
        }
    }

    void ZoomCamera()
    {
        scroll = Input.GetAxis("Mouse ScrollWheel");
        if (scroll != 0.0f)
        {
            // Zoom is independent of time scale
            Camera.main.orthographicSize = Mathf.Clamp(
                Camera.main.orthographicSize - (scroll * 8), // Fixed multiplier
                5f,
                20f
            );
        }
    }

    public void CenterOnPosition(Vector3 worldPosition)
    {
        // Start smooth centering on world position while maintaining current zoom
        centerTarget = new Vector3(worldPosition.x, worldPosition.y, transform.position.z);
        
        centerTarget.x = Mathf.Clamp(centerTarget.x, minBounds.x, maxBounds.x);
        centerTarget.y = Mathf.Clamp(centerTarget.y, minBounds.y, maxBounds.y);
        
        isSmoothCentering = true;
    }

    private void UpdateCameraSmoothing()
    {
        Vector3 currentPos = transform.position;
        Vector3 newPos = Vector3.Lerp(currentPos, centerTarget, Time.unscaledDeltaTime * smoothCenterSpeed);
        
        // Check if we've reached the target
        if (Vector3.Distance(newPos, centerTarget) < 0.01f)
        {
            transform.position = centerTarget;
            isSmoothCentering = false;
        }
        else
        {
            transform.position = newPos;
        }
    }

    internal static void LockCamera()
    {
        // This method can be called from other scripts to pause camera movement
        // For example, when opening a settings menu
        CameraController instance = FindFirstObjectByType<CameraController>();
        if (instance != null)
        {
            instance.allowCameraMovement = !instance.allowCameraMovement; // Toggle camera movement
        }
    }
}