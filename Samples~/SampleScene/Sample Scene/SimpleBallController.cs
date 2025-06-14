using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SimpleBallController : MonoBehaviour
{
    public Transform cameraTransform;

    public float movementSpeed = 10;
    public float rotationSpeed = 10;

    private Rigidbody rb;

    private Vector3 cameraOffset;
    void Start()
    {
        cameraOffset = cameraTransform.position - transform.position;

        rb = GetComponent<Rigidbody>();
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F))
        {
            rb.isKinematic = !rb.isKinematic;
        }
    }

    // Update is called once per frame
    void FixedUpdate()
    {
        rb.AddForce(cameraTransform.forward * Input.GetAxis("Vertical") * 0.1f, ForceMode.VelocityChange);
        rb.AddForce(cameraTransform.right * Input.GetAxis("Horizontal") * 0.1f, ForceMode.VelocityChange);

        cameraTransform.position = transform.position + cameraOffset;

        float r = 0;

        if (Input.GetKey(KeyCode.E))
            r = 1;

        if (Input.GetKey(KeyCode.Q))
            r = -1;

        cameraTransform.rotation *= Quaternion.Euler(0, r * rotationSpeed, 0);
    }
}
