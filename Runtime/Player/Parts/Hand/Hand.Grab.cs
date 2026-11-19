using Nox.CCK.Interactions;
using Nox.Interactions;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Player {
	/// <summary>
	/// Grab pipeline of the hand, following AutoHand's <c>Hand.GrabObject</c>: a press resolves how the object comes into
	/// the hand (the hand reaches it, or the object flies to the hand), attaches it, wraps the fingers and links the joint
	/// of a physical hold.
	/// </summary>
	public partial class Hand {
		[Header("Grab")]
		[SerializeField, Tooltip("The point along the grab time 0-1 where the hand has been transitioned from whatever pose it was when grabbing to its open hand pose"), Range(0, 1)]
		private float grabOpenHandPoint = 0.5f;

		[SerializeField, Min(0f), Tooltip("Shortest grab blend, in seconds (contact).")]
		private float minGrabDuration = InteractionConstants.MinGrabDuration;

		[SerializeField, Min(0f), Tooltip("Longest grab blend, in seconds (full reach).")]
		private float maxGrabDuration = InteractionConstants.MaxGrabDuration;

		[SerializeField, Tooltip("How the hand brings an object into the hand, when the grabbable has no preference.")]
		private GrabType grabType = GrabType.HandToGrabbable;

		[SerializeField, Min(0f), Tooltip("Heaviest object (kg) the hand brings onto its palm (GrabbableToHand). A heavier object is grabbed where it is: the hand goes to it instead.")]
		private float maxPullMass = 2f;

		[SerializeField, Min(0f), Tooltip("Longest distance the grab preview draws the hand reaching for, in metres.")]
		private float maxReachOffset = 0.25f;

		[Header("Gentle grab")]
		[SerializeField, Min(0f), Tooltip("Speed at which the hand eases back onto the controller after a grab, as AutoHand's gentleGrabSpeed.")]
		private float gentleGrabSpeed = 2f;

		[SerializeField, Tooltip("Ease the hand back onto its controller after a grab instead of leaving it on the object (AutoHand's useGentleGrab).")]
		private bool useGentleGrab = true;

		[SerializeField, Min(0f), Tooltip("Amplifies the return speed of the hand on the controller movement during a gentle grab (AutoHand's velocityGrabHandAmplifier).")]
		private float velocityGrabHandAmplifier = 600f;

		private Vector3 grabPositionOffset = Vector3.zero;
		private Quaternion grabRotationOffset = Quaternion.identity;
		private float _gentleOffset;
		private Vector3 _lastFollowPosition;
		private Quaternion _lastFollowRotation = Quaternion.identity;

		/// <summary>Type of the grab in progress, read by the grabbable while it captures the hold.</summary>
		private GrabType _activeGrabType = GrabType.HandToGrabbable;

		/// <summary>Palm of the hand (AutoHand's <c>palmTransform</c>): the avatar palm, else the follow target.</summary>
		public Transform Palm {
			get {
				var palm = PoseHand?.Palm;
				return palm ? palm : MoveTo;
			}
		}

		/// <summary>Resets the grab offset created on grab for a smoother hand return (AutoHand ResetGrabOffset).</summary>
		public void ResetGrabOffset() {
			var followPoint = MoveTo ? MoveTo : transform;
			grabPositionOffset = transform.position - followPoint.position;
			grabRotationOffset = Quaternion.Inverse(followPoint.rotation) * transform.rotation;
		}

		/// <summary>Grabs the current target. The blend duration scales with the distance, like AutoHand's <c>GetGrabTime</c>:</summary>
		/// a contact grab is nearly instant, a reach grab takes the longest.
		/// <para>
		/// A <c>HandToGrabbable</c> grab is the hand that goes to the object and comes back with it. A
		/// <c>GrabbableToHand</c> grab brings the object onto the palm, unless the object is too heavy to be moved that
		/// way: the hand goes to it then, the way AutoHand falls back on the hand-to-grabbable grab for an object it
		/// cannot bring in.
		/// </para>
		/// </summary>
		public bool Grab() {
			if (_grabState != GrabState.Free || _grabTarget == null) {
				RaiseGrabPressed();
				Logger.LogDebug($"[HandGrab] ignored: state={_grabState}, target={(_grabTarget != null ? _grabTarget.Target?.name : null)}", this, tag: nameof(Hand));
				return false;
			}

			var target = _grabTarget;
			var contact = _grabHit;
			var instant = target is IGrabAttachment { InstantGrab: true };
			var type = ResolveGrabType(target);

			TryPreviewGrab(target, contact, null, null, out var pose);

			var duration = instant ? 0f : DurationTo(contact);
			var result = PerformGrab(target, contact, type, duration, pose.ObjectDelta);
			Logger.LogDebug($"[HandGrab] target={target.Target?.name} type={type} duration={duration:0.000} -> {result}", this, tag: nameof(Hand));
			return result;
		}

		/// <summary>Attaches the object to the hand, shapes the fingers and links the joint.</summary>
		private bool PerformGrab(IGrabbable grabbable, in RaycastHit contact, GrabType type, float duration, Vector3 objectOffset = default) {
			if (grabbable == null)
				return false;

			RebuildBody();

			// read by the grabbable while it captures the hold: whether it brings the object onto the palm
			_activeGrabType = type;

			if (!TryGrab(grabbable, contact, duration))
				return false;

			_relaxing = false;

			WrapHeld(grabbable, objectOffset);

			ResetGrabOffset();

			if (grabbable is GrabbableBase output && output.Body != null && output.PhysicsHeld) {
				// every hand holding a physical object is driven by its follower (it is pulled onto the object), but only the
				// one carrying it binds the joint: two rigid joints on the same object fight each other and throw it away
				BeginPhysical(output.FollowSpeed);
				BindDriverJoint(output, contact);
			}

			return true;
		}

		/// <summary>
		/// Links the joint that carries a physical hold, when this hand is the one carrying the object. The other hands assist:
		/// they hold the same object through their follower offset, without a second joint.
		/// </summary>
		private void BindDriverJoint(GrabbableBase output, in RaycastHit contact) {
			if (_joint || output == null || !output.IsDriver(this))
				return;

			var anchor = contact.collider ? contact.point : output.transform.position;

			_joint = GrabJointBinder.Bind(this, output, anchor, output.JointBreakForce, true);
		}

		/// <summary>
		/// Grab type of a grabbable: its own preference wins, else the one of the hand.
		/// <para>
		/// An object that flies to the hand is grabbed the other way when another hand already holds it, or when it is
		/// not carried on the grab point (AutoHand: <c>HeldCount() &gt; 0 || !parentOnGrab</c>): the second hand goes to
		/// the object, so it lands on it instead of leaving the offset the object had when it stopped on the first hand.
		/// </para>
		/// </summary>
		private GrabType ResolveGrabType(IGrabbable grabbable) {
			var type = grabbable is IGrabAttachment attachment && attachment.GrabType != GrabType.Default
				? attachment.GrabType
				: grabType == GrabType.Default ? GrabType.HandToGrabbable : grabType;

			if (type == GrabType.GrabbableToHand && (IsHeld(grabbable) || !IsCarriedOnGrabPoint(grabbable)))
				return GrabType.HandToGrabbable;

			return type;
		}

		/// <summary>Whether a hand (or a ray) already holds the object.</summary>
		private static bool IsHeld(IGrabbable grabbable)
			=> grabbable != null
			   && (grabbable.State == GrabState.Held || grabbable is IGrabGroup { HeldCount: > 0 });

		/// <summary>
		/// Whether the object is carried on the grab point, the equivalent of AutoHand's <c>parentOnGrab</c>: a weightless
		/// hold is (the object follows the grab point), a joint hold is not (the physics carries it), and pulling such an
		/// object onto the hand would fight its joint.
		/// </summary>
		private static bool IsCarriedOnGrabPoint(IGrabbable grabbable)
			=> grabbable is not GrabbableBase output || output.IgnoreWeight;

		/// <summary>
		/// Whether the hand may bring the object onto its palm: a weightless object is carried, a joint-held one is pulled
		/// back with its joint, and a body too heavy to be moved that way is grabbed where it is.
		/// </summary>
		private bool CanPullToHand(IGrabbable grabbable) {
			if (grabbable is GrabbableBase { IgnoreWeight: false })
				return true;

			var body = (grabbable as IGrabBody)?.Body;
			return body == null || body.mass <= maxPullMass;
		}

		/// <summary>
		/// Releases the held object. The throw comes from the grabbable's own motion tracker, which sampled
		/// this grab point while it was held; <paramref name="throwMultiplier"/> scales it.
		/// </summary>
		public bool Release(float throwMultiplier) {
			if (_grabState != GrabState.Held)
				return false;

			UnbindJoint();

			if (!TryReleaseHeld(throwMultiplier))
				return false;

			EndPhysical();
			EndConform();

			return true;
		}

		private float DurationTo(in RaycastHit contact) {
			var reach = Mathf.Max(ReachDistance, 0.0001f);
			var distance = contact.collider ? Vector3.Distance(Palm.position, contact.point) : 0f;
			var ratio = Mathf.Clamp01(distance / reach);

			return Mathf.Clamp(
				minGrabDuration * 2f + (maxGrabDuration - minGrabDuration) * ratio,
				0f,
				maxGrabDuration
			);
		}

		/// <summary>
		/// Wires the grab pipeline: reach, layers, highlight material and whether the fingers wrap the object.
		/// Called by the controller when the hand registers, and safe to call again after an avatar change.
		/// </summary>
		public void ConfigureGrab(float reach, LayerMask layers, Material highlight, bool conform = true) {
			conformFingers = conform;

			ReachDistance = reach;
			TargetLayers = layers;

			HighlightTargets = true;

			if (highlight)
				HighlightMaterial = highlight;

			RebuildBody();
		}
	}
}
