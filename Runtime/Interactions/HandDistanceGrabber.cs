using Nox.CCK.Interactions;
using Nox.Interactions;
using Nox.XR.Player;
using UnityEngine;

namespace Nox.XR.Interactions {
	/// <summary>
	/// Far grab of a physical hand, following the AutoHand distance grabber: a sphere cast from the pointer
	/// targets a remote grabbable, a press selects it, and pulling it grabs it - the grabbable itself blends
	/// from its position to the hand over <see cref="pullDuration"/>, so the object flies in instead of
	/// snapping.
	/// <para>
	/// It shares the <see cref="GrabberBase"/> contract with <see cref="HandGrabber"/>; the near grabber has
	/// priority when both see an object, which is what <see cref="HandGrabInput"/> implements.
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Interactions/Hand Distance Grabber")]
	[DefaultExecutionOrder(2001)]
	[RequireComponent(typeof(Hand))]
	public class HandDistanceGrabber : GrabberBase, IGrabPull {
		[SerializeField, Tooltip("Hand part this grabber belongs to. Found on this GameObject when empty.")]
		private Hand hand;

		[SerializeField, Tooltip("Origin and forward of the ray. Defaults to the hand follow target (the controller).")]
		private Transform pointer;

		[SerializeField, Min(0.1f), Tooltip("Maximum reach of the ray, in metres.")]
		private float maxRange = 5f;

		[SerializeField, Min(0.005f), Tooltip("Radius of the sphere cast, so a thin handle is not missed.")]
		private float sphereRadius = InteractionConstants.DefaultContactRadius;

		[SerializeField, Min(0f), Tooltip("Time the object takes to fly to the hand once grabbed, in seconds.")]
		private float pullDuration = 0.2f;

		[SerializeField, Min(0f), Tooltip("How fast the ray direction catches up with the pointer, in degrees per second. 0 disables smoothing.")]
		private float forwardSmoothing = 5f;

		private IGrabbable _selected;
		private RaycastHit _selectionHit;
		private Vector3 _smoothForward;
		private bool _hasForward;

		/// <summary>Hand part this grabber belongs to.</summary>
		public Hand HandPart {
			get => hand;
			set => hand = value;
		}

		/// <summary>Whether a remote object is currently selected (a press happened, the pull did not).</summary>
		public bool IsSelecting
			=> _selected != null;

		/// <summary>
		/// Always true: a pull brings the object onto the grab point over <see cref="pullDuration"/>, whatever
		/// the grab type authored on the grabbable (see <see cref="IGrabPull"/>).
		/// </summary>
		public bool PullsToPoint
			=> true;

		/// <summary>Origin and forward of the ray.</summary>
		public Transform Pointer
			=> pointer ? pointer : hand ? hand.MoveTo : transform;

		/// <summary>Maximum reach of the ray.</summary>
		public float MaxRange {
			get => maxRange;
			set => maxRange = Mathf.Max(0.1f, value);
		}

		/// <summary>Wires the grabber to a hand: ray origin, reach, layers and highlight.</summary>
		public void Configure(Hand part, float reach, LayerMask layers, Material highlight) {
			hand = part;
			SetGrabPoint(part ? part.transform : transform);
			SetRig(part ? RigOf(part) : EntityRig.None);
			SetKind(GrabKind.Indirect);
			MaxRange = reach;
			TargetLayers = layers;
			HighlightTargets = true;

			if (highlight)
				HighlightMaterial = highlight;
		}

		protected override void Awake() {
			base.Awake();

			SetKind(GrabKind.Indirect);

			if (!hand)
				hand = GetComponent<Hand>();

			if (!pointer && hand)
				pointer = hand.MoveTo;

			if (Rig == EntityRig.None && hand)
				SetRig(RigOf(hand));

			if (TargetLayers == 0)
				TargetLayers = InteractionConstants.GrabbableMask;
		}

		private void Update() {
			if (!hand || !hand.PartEnabled || State != GrabState.Free || IsSelecting)
				return;

			Scan();
		}

		/// <summary>
		/// Sphere-casts along the (smoothed) pointer and targets the grabbable under it. A miss clears the
		/// highlight so no stale target is ever grabbed.
		/// </summary>
		private void Scan() {
			var origin = Pointer;
			if (!origin)
				return;

			var forward = Forward(origin, Time.deltaTime);

			if (Physics.SphereCast(origin.position, sphereRadius, forward, out var hit, maxRange, TargetLayers, QueryTriggerInteraction.Ignore)) {
				var grabbable = hit.collider ? hit.collider.GetComponentInParent<IGrabbable>() : null;

				if (grabbable != null && grabbable.CanBeGrabbedBy(this)) {
					SetTarget(grabbable, hit);
					return;
				}
			}

			ClearTarget(CurrentTarget);
		}

		private Vector3 Forward(Transform origin, float deltaTime) {
			var forward = origin.forward;

			if (forwardSmoothing <= 0f || !_hasForward) {
				_smoothForward = forward;
				_hasForward = true;
				return forward;
			}

			var angle = Vector3.Angle(_smoothForward, forward);
			_smoothForward = Vector3.RotateTowards(
				_smoothForward,
				forward,
				deltaTime * forwardSmoothing + deltaTime * forwardSmoothing * angle,
				1000f
			);
			return _smoothForward;
		}

		/// <summary>Freezes the current target as the selection, the way AutoHand locks the contact point.</summary>
		public bool Select() {
			if (_selected != null || CurrentTarget == null)
				return false;

			_selected = CurrentTarget;
			_selectionHit = Contact;
			return true;
		}

		/// <summary>Cancels a selection that was never pulled.</summary>
		public void Cancel() {
			var selected = _selected;
			_selected = null;
			_selectionHit = default;

			if (selected != null)
				ClearTarget(selected);
		}

		/// <summary>
		/// Pulls the selected object into the hand: the grab blends it from where it is to the hand over
		/// <see cref="pullDuration"/>, which is the AutoHand <c>GrabbableToHand</c> behaviour.
		/// </summary>
		public bool Pull() {
			var selected = _selected;
			if (selected == null)
				return false;

			var hit = _selectionHit;
			_selected = null;
			ClearTarget(selected);

			if (!TryGrab(selected, hit, pullDuration))
				return false;

			if (TryGetComponent<HandGrabber>(out var near))
				near.WrapHeld(selected);

			return true;
		}

		/// <summary>Releases the object this grabber pulled into the hand.</summary>
		public bool Release(float throwMultiplier) {
			if (State != GrabState.Held)
				return false;

			return TryReleaseHeld(throwMultiplier);
		}

		private static EntityRig RigOf(Hand part)
			=> part.Side == HandSide.Left ? EntityRig.LeftHand : EntityRig.RightHand;
	}
}
