using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class NecronomicusPlayerControllerV1 : MonoBehaviour
{
    public bool debugDisplay;
    //**references**//
    //public
    [SerializeField]
    Transform inputSpace = default;
    //private
    Transform trans;
    Rigidbody rb, connectedRb, lastConnectedRb;

    //**player tuning variables**//
    [Header("Player parameters")]
    [SerializeField, Range(0f, 100f)]
    float gravityForce;
    [SerializeField, Range(0f, 50f)]
    float jumpForce, backflipForce;
    [SerializeField]
    int maxJumpFrames = 20;
    [SerializeField, Range(0f, 50f)]
    float runAcceleration, airAcceleration;
    [SerializeField, Range(0f, 50f)]
    float runSpeed;

    //**snap to ground parameters**//
    [Header("Ground Snapping Parameters")]

    [SerializeField, Range(0f, 90f)]
    float maxSlopeAngle = 25f;
    float minGroundDotProduct;
    [SerializeField, Range(0f, 10f)]
    float stepCheckRadius = 0.5f;
    public float maxStepHeight = 0.2f;
    [SerializeField, Range(0f, 100f)]
    float maxSnapSpeed = 100f;
    [SerializeField, Range(0.01f, 1.5f)]
    float snapRayDistance = 1f;
    [SerializeField]
    LayerMask snapRayMask = -1;

    //local snap data
    RaycastHit[] stepRays =  new RaycastHit[8];

    //**ledge grab parameters**//
    [SerializeField]
    LayerMask ledgeMask = -1;

    RaycastHit[] ledgeRays = new RaycastHit[4];

    //**state variables**//
    int groundContacts, steepContacts;
    bool groundContact => groundContacts > 0;
    bool wallContact => steepContacts > 0;

    [HideInInspector]
    public float currentSpeed, currentAcceleration, currentJumpForce, currentGravity;

    bool jumping, canJump, climbingLedge;

    [HideInInspector]
    public PlayerState currentState = PlayerState.Default;

    //**Timers**//
    //state timers
    [HideInInspector]
    public int stepsSinceGrounded;
    int stepsSinceAirborne;
    int stepsSinceLedgeHang;

    //state counters
    int jumpSteps;

    //input timers
    int stepsSinceStickSnap;

    //**cached data**//
    [HideInInspector]
    public Vector3 connectedVelocity, localVelocity;
    Vector3 connectedWorldPos, connectedLocalPos;
    Quaternion lastConnectedRotation, connectedDeltaRotation;

    Vector3 initialJumpDir, turnVector;

    Vector3 contactNormal, steepNormal;

    Vector3 ledgeGrabPoint;

    //**player input**//
    [Header("Input")]
    [SerializeField, Range(0.01f, 1f)]
    float stickSnapSensitivity = 0.5f;
    public float stickBuffer = 15f;
    public int backflipWindow = 5;

    [HideInInspector]
    public Vector3 inputVector, movementVector = Vector3.zero;
    Vector3 laggyStickInput = Vector3.zero;
    Vector3 flattenedCamForward;
    Quaternion refShift;

    bool jumpInput;

    //input buffer(to be implemented)

    public enum PlayerState {Default, LedgeHanging, None}


    void Start()
    {
        minGroundDotProduct = Mathf.Cos(maxSlopeAngle * Mathf.Deg2Rad);

        trans = this.transform;
        rb = this.GetComponent<Rigidbody>();
    }

    private void Update()
    {
        InputGet();

        if (debugDisplay)
        {
            DebugVisualization();
        }
    }

    void InputGet()
    {
        inputVector.x = Input.GetAxisRaw("Horizontal");
        inputVector.z = Input.GetAxisRaw("Vertical");
        inputVector = Vector3.ClampMagnitude(inputVector, 1f);

        flattenedCamForward = inputSpace ? inputSpace.forward : Vector3.forward;
        flattenedCamForward.y = 0f;
        refShift = Quaternion.FromToRotation(Vector3.forward, flattenedCamForward);

        movementVector = refShift * inputVector;

        laggyStickInput = Vector3.Lerp(laggyStickInput, movementVector, stickBuffer * Time.deltaTime);

        if (groundContact && Vector3.Dot(-laggyStickInput.normalized, movementVector) > stickSnapSensitivity)
        {
            stepsSinceStickSnap = 0;
        }

        if (Input.GetButtonDown("Jump"))
            jumpInput = true;
        if (Input.GetButtonUp("Jump"))
            jumpInput = false;

    }

    void FixedUpdate()
    {
        if (currentState == PlayerState.None)
            return;

        stepsSinceGrounded++;
        stepsSinceAirborne++;
        stepsSinceLedgeHang++;
        stepsSinceStickSnap++;

        //switch function for different movement paradigms(as of writing just hanging or not hanging, but this makes it expandable)
        switch (currentState)
        {
            case PlayerState.Default:
                AdjustVelocity();
                break;
            case PlayerState.LedgeHanging:
                HandleLedges();
                break;
        }

        ClearStates();
    }

    void ClearStates()
    {
        groundContacts = steepContacts = 0;
        contactNormal = steepNormal = connectedVelocity = Vector3.zero;
        lastConnectedRb = connectedRb;
        connectedRb = null;
        currentGravity = gravityForce;
    }

    void GetCurrentMovementValues()
    {
        if (groundContact)
        {
            currentAcceleration = runAcceleration;
        }else
        {
            currentAcceleration = airAcceleration;
        }

        //potential improvement : if on a moving platform and local velocity is under a threshhold, ramp up acceleration to make player stick to moving platform better
        
        currentSpeed = runSpeed;

    }

    void AdjustVelocity()
    {
        if (SnapToStep() || groundContact || CheckSteepContacts() || SnapToGround())
        {
            stepsSinceGrounded = jumpSteps = 0;

            if (groundContacts > 1)
            {
                contactNormal.Normalize();
            }
        }
        else
        {
            contactNormal = Vector3.up;
            stepsSinceAirborne = 0;
            initialJumpDir = trans.forward;
        }

        if (connectedRb)
        {
            if (connectedRb.isKinematic || connectedRb.mass >= rb.mass)
            {
                UpdateConnectedMovement();
            }
        }

        localVelocity = rb.velocity - connectedVelocity;

        GetCurrentMovementValues();
        ProjectVelocity();

        rb.velocity = connectedVelocity + localVelocity;

        CheckJump();

        rb.velocity -= Vector3.up * currentGravity * Time.fixedDeltaTime;

        GetTurnVector();
        if (turnVector != trans.forward)
        {
            transform.rotation = Quaternion.LookRotation(turnVector, Vector3.up);
        }

        if (!groundContact)
        {
            CheckLedge();
        }
    }

    void CheckJump()
    {
        if(!groundContact && !jumpInput)
        {
            canJump = jumpInput = false;
        }else if (groundContact && !jumpInput) {
            canJump = true;
        }

        //choose jump type
        if (jumpInput)
        {
            if (canJump)
            {
                if (stepsSinceStickSnap <= backflipWindow)
                {
                    Backflip();
                }
                else
                {
                    Jump();
                }
            }
            else if (!groundContact && wallContact)
            {
                WallJump();
            }
            else
            {
                //glide
            }
        }
    }

    void Jump()
    {
        if (stepsSinceGrounded < 1)
        {
            initialJumpDir = movementVector.sqrMagnitude > 0.0001 ? movementVector : transform.forward; 
            initialJumpDir.y = 0f;
        }

            if (jumpSteps <= maxJumpFrames)
        {
            currentJumpForce = Mathf.Lerp(gravityForce + (jumpForce * 10f), 0f, (float)jumpSteps / maxJumpFrames);
        }
        else
        {
            currentJumpForce = 0f;
            canJump = jumpInput = false;
        }

        rb.velocity += Vector3.up * currentJumpForce * Time.fixedDeltaTime;

        jumpSteps++;
    }

    void WallJump()
    {
        Debug.Log("wall jump");
        localVelocity.y = 0f;

        Vector3 jumpDirection = initialJumpDir = (steepNormal + Vector3.up).normalized;
        initialJumpDir.y = 0f;
        rb.velocity += jumpDirection * jumpForce;
        jumpInput = canJump = false;
    }

    void Backflip()
    {

        if (stepsSinceGrounded < 1)
        {
            initialJumpDir = movementVector.sqrMagnitude > 0.0001 ? movementVector : transform.forward;
            initialJumpDir.y = 0f;
        }

        canJump = false;
        rb.velocity = (Vector3.up * jumpForce * 2f) + movementVector;
    }

    void GetTurnVector()
    {
        if (movementVector.sqrMagnitude > 0.001f && groundContact)
        {
            turnVector = movementVector;
        }
        else if (!groundContact)
        {
            turnVector = initialJumpDir;
        }
        else
        {
            turnVector = transform.forward;
        }
    }

    void ProjectVelocity()
    {
        Vector3 xAxis = ProjectVector(Vector3.right, contactNormal, false).normalized;
        Vector3 zAxis = ProjectVector(Vector3.forward, contactNormal, false).normalized;

        float currentXvelocity = Vector3.Dot(localVelocity, xAxis);
        float currentZvelocity = Vector3.Dot(localVelocity, zAxis);

        float newXvelocity = Mathf.MoveTowards(currentXvelocity, movementVector.x * currentSpeed, currentAcceleration * Time.fixedDeltaTime);
        float newZvelocity = Mathf.MoveTowards(currentZvelocity, movementVector.z * currentSpeed, currentAcceleration * Time.fixedDeltaTime);

        localVelocity += xAxis * (newXvelocity - currentXvelocity) + zAxis * (newZvelocity - currentZvelocity);
    }

    void UpdateConnectedMovement()
    {
        if (connectedRb == lastConnectedRb)
        {
            Vector3 connectedMvmt = connectedRb.transform.TransformPoint(connectedLocalPos) - connectedWorldPos;
            connectedVelocity = connectedMvmt / Time.deltaTime;
        }
        connectedDeltaRotation = connectedRb.rotation * Quaternion.Inverse(lastConnectedRotation);

        connectedWorldPos = rb.position;
        connectedLocalPos = connectedRb.transform.InverseTransformPoint(connectedWorldPos);

        lastConnectedRotation = connectedRb.rotation;

        transform.Rotate(Vector3.up * connectedDeltaRotation.eulerAngles.y);
    }

    bool SnapToGround()
    {
        if (stepsSinceGrounded > 1 || jumpSteps > 0)
        {
            //Debug.Log("Dont snap because not grounded for too long" + stepsSinceGrounded);
            return false;
        }
        if (!Physics.Raycast(trans.position + (Vector3.up * 0.1f), Vector3.down, out RaycastHit hit, snapRayDistance + 0.1f, snapRayMask))
        {
            //Debug.Log("Dont snap because raycast didnt hit anything");
            return false;
        }
        if (hit.normal.y < minGroundDotProduct)
        {
            //Debug.Log("Dont snap because slope was too steep");
            return false;
        }

        groundContacts = 1;
        contactNormal = hit.normal;

        connectedRb = hit.rigidbody;

        //Debug.Log("snap to ground");

        return true;
    }

    bool SnapToStep()
    {
        if(stepCheckRadius == 0f || connectedRb != null)
        {
            //Debug.Log("about because no ground contact or on a moving platform");
            return false;
        }

        if (!Physics.Raycast(trans.position + (Vector3.up * 0.1f), Vector3.down, maxStepHeight + 0.1f, snapRayMask))
        {
            //Debug.Log("abort because no downwards raycast");
            return false;
        }

        for (int i = 0; i < 8; i++)
        {
            Vector3 rayDir = Quaternion.Euler(Vector3.up * 45f * i) * trans.forward;
            Debug.DrawRay(trans.position + (rayDir * stepCheckRadius) + (Vector3.up * maxStepHeight), Vector3.down * maxStepHeight, Color.blue);
            if (Physics.Raycast(trans.position +  (rayDir * stepCheckRadius) + (Vector3.up * maxStepHeight), Vector3.down, out stepRays[i], maxStepHeight + 0.2f, snapRayMask))
            {
                if (stepRays[i].point.y >= trans.position.y && Vector3.Dot(stepRays[i].normal, Vector3.up) > 0.95f)
                {
                    //Debug.Log("snapping to step");
                    groundContacts = 1;
                    contactNormal = stepRays[i].normal;
                    Vector3 newPos = trans.position;
                    newPos.y = stepRays[i].point.y;
                    rb.velocity = FlattenVector(rb.velocity);
                    currentGravity = 0f;
                    trans.position = newPos;

                    return true;
                }
            } 
        }

        return false;
    }

    bool CheckSteepContacts()
    {
        //this function atrificially registers the player as grounded if it's in a crevice
        if (steepContacts > 1)
        {
            steepNormal.Normalize();
            if (steepNormal.y >= minGroundDotProduct)
            {
                groundContacts = 1;
                contactNormal = steepNormal;
                return true;
            }
        }
        return false;
    }

    void CheckLedge()
    {
        if (stepsSinceLedgeHang < 15 || localVelocity.y >= 0f)
            return;

        if (FlattenVector(localVelocity).sqrMagnitude > 1f)
        {
            if (Physics.Raycast(trans.position + (Vector3.up * 2f), trans.forward, out ledgeRays[0], 1f, ledgeMask))
            {
                Debug.DrawRay(ledgeRays[0].point, ledgeRays[0].normal, Color.red);
                if (Physics.SphereCast(ledgeRays[0].point + (Vector3.up), 0.35f, -Vector3.up, out ledgeRays[1], 1f, ledgeMask))
                {
                    ledgeGrabPoint = ledgeRays[1].point;
                    turnVector = FlattenVector(-ledgeRays[0].normal);
                    rb.velocity = Vector3.zero;
                    Vector3 ySnap = transform.position;
                    ySnap.y = ledgeRays[1].point.y - 2f;
                    transform.position = ySnap;
                    currentState = PlayerState.LedgeHanging;
                }
            }
        }
        else
        {
            //oh gawd
            //so baaaasically, we're just gonna rotate a raycast, this is actually mad stupid
            for (int i = 0; i < 4; i++)
            {
                Vector3 rayDir = Quaternion.Euler(Vector3.up * 90f * i) * trans.forward;
                Debug.DrawRay(trans.position + (Vector3.up * 2f), rayDir, Color.blue);
                if (Physics.Raycast(trans.position + (Vector3.up * 2f), rayDir, out ledgeRays[0], 1f, ledgeMask))
                {
                    if (Physics.SphereCast(ledgeRays[0].point + (Vector3.up), 0.35f, -Vector3.up, out ledgeRays[1], 1f, ledgeMask))
                    {
                        ledgeGrabPoint = ledgeRays[1].point;
                        turnVector = FlattenVector(-ledgeRays[0].normal);
                        rb.velocity = Vector3.zero;
                        Vector3 ySnap = transform.position;
                        ySnap.y = ledgeRays[1].point.y - 2f;
                        transform.position = ySnap;
                        currentState = PlayerState.LedgeHanging;
                        break;
                    }
                }
            }
        }
    }

    void HandleLedges()
    {
        if (climbingLedge || inputVector.z >= 0.25f && ClimbCheck())
        {
            ClimbUp();
        }
        else
        {
            LedgeMovement();
        }
    }

    void LedgeMovement()
    {
        stepsSinceLedgeHang = 0;
        rb.velocity = Vector3.zero;

        if (turnVector != trans.forward)
        {
            transform.rotation = Quaternion.LookRotation(turnVector, Vector3.up);
        }

        if (!Physics.Raycast(trans.position + (Vector3.up * 1.5f), trans.forward, out ledgeRays[1], 1f, ledgeMask))
        {
            Debug.Log("breaking because forward ray didnt hit anything");
            currentState = PlayerState.Default;
        }

        Physics.Raycast(trans.position + (Vector3.up * 1.5f) - (trans.right * 0.5f), trans.forward, out ledgeRays[0], 1f, ledgeMask);
        Physics.Raycast(trans.position + (Vector3.up * 1.5f) + (trans.right * 0.5f), trans.forward, out ledgeRays[2], 1f, ledgeMask);

        Physics.SphereCast(ledgeRays[1].point + (Vector3.up), 0.35f, -Vector3.up, out ledgeRays[3], 1f, ledgeMask);

        Vector3 ledgeNormal = Vector3.zero;
        for (int i = 0; i<3; i++) {
            if (ledgeRays[i].collider != null)
            {
                ledgeNormal += ledgeRays[i].normal;
            }
        }

        ledgeNormal.Normalize();

        ledgeGrabPoint = ledgeRays[1].point;

        if (inputVector.x < 0f && ledgeRays[0].collider != null)
        {
            ledgeGrabPoint = ledgeRays[0].point;
        }
        else if (inputVector.x > 0f && ledgeRays[2].collider != null)
        {
            ledgeGrabPoint = ledgeRays[2].point;
        }

        ledgeGrabPoint.y = ledgeRays[3].point.y;

        Vector3 targetPos = (ledgeGrabPoint - (Vector3.up * 2f)) + (ledgeNormal * 0.6f);

        Debug.DrawRay(targetPos, ledgeNormal, Color.white);

        transform.position = Vector3.Lerp(transform.position, targetPos, 2f * Time.fixedDeltaTime);

        turnVector = Vector3.Lerp(turnVector, FlattenVector(-ledgeNormal), 5f * Time.deltaTime);

        if (inputVector.z < -0.25f)
        {
            currentState = PlayerState.Default;
        }
    }

    void ClimbUp()
    {
        //this is the current teleporty ledge climb, comment all this out if using animated climb up stuff
        localVelocity = Vector3.zero;

        trans.position = ledgeRays[3].point - (ledgeRays[1].normal * 0.5f);
        rb.isKinematic = false;
        climbingLedge = false;
        currentState = PlayerState.Default;


        //so this bit below is from an older script, but this is how you'd do a get-up animation. for this to work, you'll need to either add an animationHandler function to this script
        // or have a reference to the existing AnimationHandler script. Some of the variable names probably need to be renamed as well, but this logic works fine.

        //if (playerAnims.GetCurrentAnimatorStateInfo(0).IsName("climbUp"))
        //{
        //    if (playerAnims.GetCurrentAnimatorStateInfo(0).normalizedTime > 0.9f)
        //    {
        //        trans.position = PlayerModel.position;
        //        PlayerModel.localPosition = Vector3.zero;

        //        climbingLedge = false;
        //        rb.isKinematic = false;
        //    }
        //}
    }

    bool ClimbCheck()
    {
        //you can add a spherecast in front and above the player moving down to check the area that the player will climb to and make sure it's clear.
        //im returning truwe by default because i'm going to design around this limitation during art dev, but in a situation where you have less control over the art or level design, this
        //is a very useful thing to implement
        return climbingLedge = rb.isKinematic = true;
    }

    private void OnCollisionStay(Collision collision)
    {
        EvaluateCollisions(collision);
    }
    private void OnCollisionEnter(Collision collision)
    {
        EvaluateCollisions(collision);
    }

    void EvaluateCollisions(Collision collision)
    {
        for (int i = 0; i < collision.contactCount; i++)
        {
            Vector3 normal = collision.GetContact(i).normal;
            if (normal.y >= minGroundDotProduct)
            {
                groundContacts++;
                contactNormal += normal;
                connectedRb = collision.rigidbody;
                currentGravity = 0f;
            }
            else if (normal.y > -0.01f)
            {
                steepContacts++;
                steepNormal += normal;
                if (groundContacts == 0)
                {
                    connectedRb = collision.rigidbody;
                }
            }
            else
            {
                contactNormal = Vector3.up;
            }
        }
    }

    public Vector3 ProjectVector(Vector3 vector, Vector3 normal, bool preserveMagnitude)
    {
        float mag = 1f;
        Vector3 result;
        if (preserveMagnitude)
        {
            mag = vector.magnitude;
        }
        result = (vector - normal * Vector3.Dot(vector, normal)).normalized;

        if (preserveMagnitude)
        {
            return result * mag;
        }

        return result;

    }

    public Vector3 FlattenVector(Vector3 vector)
    {
        Vector3 outVector = vector;
        outVector.y = 0f;
        return outVector;
    }

    void DebugVisualization()
    {
        //this is just a tidy function to put all your visualization stuff in so you don't have to hunt it down in the rest of the script
        Debug.DrawRay(trans.position, inputVector, Color.red);
        Debug.DrawRay(trans.position, movementVector, Color.blue);
        Debug.DrawRay(trans.position, contactNormal * 5f, Color.white);
    }
}
