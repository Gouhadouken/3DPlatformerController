using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class AnimationHandler : MonoBehaviour
{
    [SerializeField]
    Animator animator;
    [SerializeField]
    NecronomicusPlayerControllerV1 pController;

    float xzVelocity, yVelocity;
    bool onLedge, onGround, skid;

    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        GetAnimationParameters();
        SetAnimationParamters();
    }

    void GetAnimationParameters()
    {
        onLedge = pController.currentState == NecronomicusPlayerControllerV1.PlayerState.LedgeHanging ? true : false;
        onGround = pController.stepsSinceGrounded > 0 ? false : true;

        xzVelocity = onLedge ? Mathf.Abs(pController.inputVector.x) : pController.localVelocity.magnitude / pController.currentSpeed;
        yVelocity = pController.localVelocity.y;

        skid = onGround && Vector3.Dot(pController.movementVector, new Vector3(pController.localVelocity.x, 0f, pController.localVelocity.z)) < -0.5f ? true : false;
    }
    void SetAnimationParamters()
    {
        animator.SetFloat("xzVelocity", xzVelocity);
        animator.SetFloat("yVelocity", yVelocity);

        animator.SetBool("onLedge", onLedge);
        animator.SetBool("onGround", onGround);
        animator.SetBool("skid", skid);
    }
}
