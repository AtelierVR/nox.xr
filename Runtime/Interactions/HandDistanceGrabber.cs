using System.Collections.Generic;
using Nox.CCK.Interactions;
using Nox.CCK.Utils;
using Nox.Interactions;
using Nox.XR.Player;
using UnityEngine;

namespace Nox.XR.Interactions {
	/// <summary>
	/// Far grab of a physical hand, following the AutoHand distance grabber: a sphere cast from the pointer
	/// targets a remote grabbable, a press selects it, and pulling it grabs it.
	/// <para>
	/// The ray holds the object itself, and it is not a physics grabber: the grabbable is made kinematic and follows
	/// the ray, so the hand stays driven by its tracker and is never dragged by what it took (only a near grab is a
	/// physics hold, see <see cref="Nox.XR.Player.Hand"/>). The object stays where it was caught, at the distance of the ray.
	/// The near grabber has priority when both see an object, which is what <see cref="HandGrabInput"/> implements.
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Interactions/Hand Distance Grabber")]
	[DefaultExecutionOrder(2001)]
	[RequireComponent(typeof(Hand))]
	public class HandDistanceGrabber : GrabberBase, IGrabPull, IGrabberBody {
		[SerializeField, Tooltip("Hand part this grabber belongs to. Found on this GameObject when empty.")]
		private Hand hand;

		[SerializeField, Tooltip("Origin and forward of the ray. Defaults to the hand follow target (the controller).")]
		private Transform pointer;

		[SerializeField, Min(0.1f), Tooltip("Maximum reach of the ray, in metres.")]
		private float maxRange = 5f;

		[SerializeField, Min(0f), Tooltip("Closest the ray grabs, in metres (multiplied by the avatar scale). Closer objects are left to the hand.")]
		private float minRange = 0.25f;

		[SerializeField, Min(0.005f), Tooltip("Radius of the sphere cast, so a thin handle is not missed.")]
		private float sphereRadius = InteractionConstants.DefaultContactRadius;

		[SerializeField, Min(0f), Tooltip("How fast the ray direction catches up with the pointer, in degrees per second. 0 disables smoothing.")]
		private float forwardSmoothing = 5f;

		[Header("Push / pull")]
		[SerializeField, Tooltip("Reel the held object in or out from the motion of the hand along the ray, the way the far grab of the VR template does.")]
		private bool reelByHandMotion = true;

		[SerializeField, Min(0f), Tooltip("Extra speed given to the object at the closest distance: 0.05 means 5 % more than the hand.")]
		private float minAdditionalVelocity = 0.05f;

		[SerializeField, Min(0f), Tooltip("Extra speed given to the object at full reach: 1.5 means the hand travel is multiplied by 2.5.")]
		private float maxAdditionalVelocity = 1.5f;

		[SerializeField, Range(0f, 2f), Tooltip("Makes pushing away easier than pulling: the same hand speed travels more when it pushes.")]
		private float pushBias = 1.25f;

		[SerializeField, Range(0f, 2f), Tooltip("Same as the push bias, for a hand motion that pulls the object back.")]
		private float pullBias = 1f;

		[SerializeField, Min(0f), Tooltip("Hand speed along the ray below which nothing is amplified, in metres per second.")]
		private float motionRampThreshold = 0.3f;

		[SerializeField, Tooltip("Keep reeling once the hand stopped, so a short flick still moves the object.")]
		private bool useMomentum = true;

		[SerializeField, Min(0f), Tooltip("How fast that momentum fades out, per second.")]
		private float momentumDecay = 1.25f;

		private const float AxisAlignment = 0.866f;
		private const float MomentumTolerance = 0.001f;

		private IGrabbable _selected;
		private RaycastHit _selectionHit;
		private Vector3 _smoothForward;
		private bool _hasForward;
		/// <summary>Point of the held object the ray touched, in its own space (-1 when unknown).</summary>
		private Vector3 _heldLocal;
		private bool _hasHeldLocal;
		/// <summary>Point the ray touched, in the space of the stabilized ray origin the line is drawn from.</summary>
		private Vector3 _holdLocal;
		private bool _hasHoldLocal;
		private Transform _tip;
		private Hand _near;
		private HandNearFar _ray;

		private Vector3 _lastOriginPosition;
		private bool _hasOriginPose;
		private Transform _space;
		private float _momentum;
		private float _pivot;
		private float _startDistance;

		/// <summary>Hand part this grabber belongs to.</summary>
		public Hand HandPart {
			get => hand;
			set => hand = value;
		}

		/// <summary>
		/// Colliders of the hand the pulled object has to ignore (see <see cref="IGrabberBody"/>): the ray holds the
		/// object itself, so it is the one that has to keep it out of the hand.
		/// </summary>
		public IReadOnlyList<Collider> BodyColliders {
			get {
				var near = Near;
				if (!near)
					return System.Array.Empty<Collider>();

				near.RebuildBody();
				return near.BodyColliders;
			}
		}

		/// <summary>Whether a remote object is currently selected (a press happened, the pull did not).</summary>
		public bool IsSelecting
			=> _selected != null;

		/// <summary>Whether the ray can grab something right now.</summary>
		public bool HasTarget
			=> CurrentTarget != null;

		/// <summary>
		/// Whether the ray can grab something, and where it meets it: the ray of the near/far draws its hover from
		/// this, so it reacts to a grabbable the toolkit ray does not know about.
		/// </summary>
		public bool TryGetGrabPoint(out Vector3 point) {
			point = default;

			var target = CurrentTarget;
			if (target == null)
				return false;

			var hit = Contact;

			if (hit.collider) {
				point = hit.point;
				return true;
			}

			var node = target.Target;
			if (!node)
				return false;

			point = node.position;
			return true;
		}

		/// <summary>
		/// Never: the ray holds the object where it was caught, at the distance of the ray, like a grabber apart from the
		/// hand.
		/// </summary>
		public bool PullsToPoint
			=> false;

		/// <summary>Origin and forward of the ray: the one the near/far line is drawn with, else the controller.</summary>
		public Transform Pointer {
			get {
				if (pointer)
					return pointer;

				var origin = RayPart ? RayPart.RayOrigin : null;
				if (origin)
					return origin;

				return hand ? hand.MoveTo : transform;
			}
		}

		/// <summary>Part drawing the near/far ray of this hand, which gives the origin of the grab ray too.</summary>
		private HandNearFar RayPart {
			get {
				if (!_ray)
					_ray = GetComponent<HandNearFar>();

				return _ray;
			}
		}

		/// <summary>
		/// What a distant hold hangs on: the tip of the ray, moved with the stabilized origin and the curved line, so the
		/// object sits at the end of the ray instead of at the controller that casts it.
		/// </summary>
		private Transform Tip {
			get {
				if (_tip)
					return _tip;

				_tip = new GameObject($"{name} Ray Tip").transform;
				_tip.SetParent(transform.root, false);

				var origin = Pointer;
				if (origin)
					_tip.SetPositionAndRotation(origin.position, origin.rotation);

				SetGrabPoint(_tip);
				return _tip;
			}
		}

		/// <summary>Puts the tip where the ray points, on the point it touched when there is one.</summary>
		private void PlaceTip() {
			var origin = Pointer;
			var tip    = Tip;

			if (!origin || !tip)
				return;

			var position = _hasHoldLocal
				? origin.TransformPoint(_holdLocal)
				: origin.position + origin.forward * maxRange;

			tip.SetPositionAndRotation(position, origin.rotation);
		}

		/// <summary>
		/// Reels the held object in or out from the motion of the hand, like the far grab of the VR template: the velocity
		/// of the ray origin along its own axis is amplified (more at long range, more when pushing) and added to the
		/// distance, and a short flick keeps travelling through the momentum. A lateral motion is ignored, and the
		/// distance stays between <see cref="MinRange"/> and the reach of the ray.
		/// </summary>
		private void Reel(float deltaTime) {
			var origin = Pointer;

			if (!reelByHandMotion || !_hasHoldLocal || !origin || deltaTime <= 0f) {
				_hasOriginPose = false;
				return;
			}

			var velocity  = OriginVelocity(deltaTime);
			var speed     = velocity.magnitude;
			var forward   = origin.forward;
			var motion    = 0f;

			if (speed > 0.0001f && Mathf.Abs(Vector3.Dot(velocity / speed, forward)) > AxisAlignment)
				motion = Vector3.Dot(velocity, forward);

			var ramp     = motionRampThreshold > 0f ? Mathf.Clamp01(Mathf.Abs(motion) / motionRampThreshold) : 1f;
			var point    = origin.TransformPoint(_holdLocal);
			var distance = Vector3.Dot(point - origin.position, forward);
			var away     = motion > 0f;
			var ratio    = Mathf.Clamp01(Mathf.Abs(distance) / Mathf.Max(_pivot, 0.1f) * (away ? pushBias : pullBias));
			var gain     = Mathf.Lerp(minAdditionalVelocity, maxAdditionalVelocity, ratio);
			var movement = motion * ramp * (1f + gain) * deltaTime;

			TrackMomentum(movement, ramp, deltaTime);

			var step = movement + (useMomentum ? _momentum : 0f);

			if (Mathf.Abs(step) < 0.000001f)
				return;

			var next = Mathf.Clamp(distance + step, MinRange, maxRange);

			if (Mathf.Approximately(next, distance))
				return;

			_holdLocal = origin.InverseTransformPoint(origin.position + forward * next);

			_pivot = next > _startDistance
				? next
				: Mathf.Lerp(_pivot, (_startDistance + next) * 0.5f, deltaTime * gain);
		}

		private void TrackMomentum(float movement, float ramp, float deltaTime) {
			if (!useMomentum) {
				_momentum = 0f;
				return;
			}

			var absoluteMomentum = Mathf.Abs(_momentum);
			var absoluteMovement = Mathf.Abs(movement);
			var aboveRamp        = ramp >= 1f;
			var changed          = (int)Mathf.Sign(_momentum) != (int)Mathf.Sign(movement)
				&& Mathf.Abs(absoluteMomentum - absoluteMovement) > MomentumTolerance;

			if (changed) {
				if (aboveRamp)
					_momentum = movement * 0.5f;
				else if (ramp > 0.25f)
					_momentum = 0f;
			}
			else if (aboveRamp) {
				_momentum = Mathf.Max(absoluteMomentum, absoluteMovement / 2f) * Mathf.Sign(movement);
			}

			if (Mathf.Abs(_momentum) < MomentumTolerance)
				_momentum = 0f;
			else
				_momentum *= 1f - momentumDecay * deltaTime;
		}

		/// <summary>
		/// Motion of the hand, in the frame of the head: walking and turning in the real world move the hand and the
		/// head together, so they are not a motion of the hand and must not push or pull what the ray holds. Locomotion
		/// already moves the whole rig, and the head frame catches what it leaves: only the arm moving relative to the
		/// head reels the object.
		/// </summary>
		private Vector3 OriginVelocity(float deltaTime) {
			var source = hand ? hand.transform : Pointer;
			var space  = Reference;

			if (!source)
				return Vector3.zero;

			var position = space ? space.InverseTransformPoint(source.position) : source.position;
			var velocity = _hasOriginPose ? (position - _lastOriginPosition) / deltaTime : Vector3.zero;

			_lastOriginPosition = position;
			_hasOriginPose      = true;

			return space ? space.TransformDirection(velocity) : velocity;
		}

		/// <summary>
		/// Frame the hand motion is measured in: the head, which the player carries with them in the real world (its
		/// position and its rotation are both taken out). Falls back on the tracking space of the player when there is
		/// no head part.
		/// </summary>
		private Transform Reference {
			get {
				var head = hand && hand.Player ? hand.Player.Head : null;
				if (head)
					return head.View;

				if (!_space && hand)
					_space = hand.transform.parent;

				return _space;
			}
		}

		private float DistanceOf(Transform origin)
			=> origin && _hasHoldLocal
				? Vector3.Dot(origin.TransformPoint(_holdLocal) - origin.position, origin.forward)
				: maxRange;

		/// <summary>The near grabber of the same hand: it is the hand itself, which does the holding (see <see cref="Pull"/>).</summary>
		public Hand Near {
			get {
				if (!_near)
					_near = GetComponent<Hand>();

				return _near;
			}
		}

		/// <summary>Maximum reach of the ray.</summary>
		public float MaxRange {
			get => maxRange;
			set => maxRange = Mathf.Max(0.1f, value);
		}

		/// <summary>
		/// Closest the ray grabs, in metres, already scaled by the avatar scale: a grabber this close to an object would
		/// take what the hand of the same avatar is meant to take.
		/// </summary>
		public float MinRange
			=> Mathf.Min(minRange * AvatarScale, maxRange);

		private static float AvatarScale
			=> Mathf.Max(0.01f, Nox.XR.Calibration.Calibration.CurrentScale);

		/// <summary>Wires the grabber to a hand: ray origin, reach, layers and highlight.</summary>
		public void Configure(Hand part, float reach, LayerMask layers, Material highlight) {
			hand = part;
			SetRig(part ? RigOf(part) : EntityRig.None);
			SetKind(GrabKind.Indirect);
			MaxRange = reach;
			TargetLayers = layers;
			HighlightTargets = true;

			if (highlight)
				HighlightMaterial = highlight;

			// the hold hangs on the tip of the ray, not on the hand
			_ = Tip;
		}

		protected override void Awake() {
			base.Awake();

			SetKind(GrabKind.Indirect);

			if (!hand)
				hand = GetComponent<Hand>();

			if (Rig == EntityRig.None && hand)
				SetRig(RigOf(hand));

			if (TargetLayers == 0)
				TargetLayers = InteractionConstants.GrabbableMask;
		}

		private void Update() {
			if (!hand || !hand.PartEnabled) {
				PublishHover(false);
				PublishHold(null);
				return;
			}

			// the hand itself is carrying something: the ray has nothing left to point at or to take, it only stops
			var near = Near;
			if (near && near.IsHolding) {
				PublishHover(false);
				PublishHold(null);
				Cancel();
				return;
			}

			if (State == GrabState.Held) {
				PublishHover(false);
				Reel(Time.deltaTime);
				PlaceTip();
				RefreshContact();
				PublishHold(HeldGrabbable);
				return;
			}

			PublishHold(null);

			if (IsSelecting) {
				PublishHover(false);
				PlaceTip();
				return;
			}

			Scan();
			PublishHover(true);
			PlaceTip();
		}

		/// <summary>
		/// Tells the ray where it holds the object, on the point it touched and not on its pivot: the line stays
		/// attached to what the user aimed at while the object follows the ray.
		/// </summary>
		private void PublishHold(IGrabbable held) {
			var rayPart = RayPart;
			if (!rayPart)
				return;

			var node = held != null ? held.Target : null;

			if (!node || !_hasHeldLocal) {
				rayPart.HoldPointValid = false;
				return;
			}

			rayPart.HoldPoint      = node.TransformPoint(_heldLocal);
			rayPart.HoldPointValid = true;
		}

		/// <summary>
		/// Tells the near/far ray of the hand whether it points at something the hand can grab, and where: the line
		/// hovers on a grabbable the same way it does on an element, and stops on it.
		/// </summary>
		private void PublishHover(bool canGrab) {
			var rayPart = RayPart;
			if (!rayPart)
				return;

			if (!canGrab || !TryGetGrabPoint(out var point)) {
				rayPart.GrabHover = false;
				return;
			}

			rayPart.GrabPoint = point;
			rayPart.GrabHover = true;
		}

		private void OnDisable() {
			PublishHover(false);
			PublishHold(null);
			_hasHeldLocal = false;
			_hasHoldLocal = false;

			if (State == GrabState.Held)
				Release(0f);
		}

		private void OnDestroy() {
			if (_tip)
				_tip.gameObject.Destroy();

			_tip = null;
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

				if (grabbable != null && grabbable.CanBeGrabbedBy(this) && hit.distance >= MinRange) {
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

			// the hand goes first: an object the palm can take is not the ray's to select
			var near = Near;
			if (near != null && near.CurrentTarget != null)
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

		/// <summary>Pulls the selected object: the ray takes it where it touched it, at the distance of the line.</summary>
		public bool Pull() {
			var selected = _selected;
			if (selected == null)
				return false;

			var hit = _selectionHit;
			_selected = null;
			ClearTarget(selected);

			// the object hangs on the tip of the ray, exactly where the line touched it: the point is kept in the space of
			// the stabilized origin, so it follows the ray (curved and stabilized) and not the controller
			var origin    = Pointer;
			_hasHoldLocal = origin && hit.collider;
			_holdLocal    = _hasHoldLocal ? origin.InverseTransformPoint(hit.point) : Vector3.zero;

			_startDistance = DistanceOf(origin);
			_pivot         = _startDistance;
			_momentum      = 0f;
			_hasOriginPose = false;

			PlaceTip();

			// the ray holds the object itself, and it is not a physics grabber: the grabbable is made kinematic and
			// follows the ray, so the hand is never pulled by what it took. It stays where it was caught.
			if (!TryGrab(selected, hit, 0f))
				return false;

			RememberContact(selected, hit);
			return true;
		}

		/// <summary>Keeps the touched point on the object, so the ray can stay attached there while the object moves.</summary>
		private void RememberContact(IGrabbable grabbable, in RaycastHit hit) {
			var node = grabbable != null ? grabbable.Target : null;

			_hasHeldLocal = node && hit.collider;
			_heldLocal    = _hasHeldLocal ? node.InverseTransformPoint(hit.point) : Vector3.zero;
		}

		/// <summary>
		/// Keeps the point of the held object under the ray tip, which is where the line has to end. It is read back
		/// every frame because the object may place itself on the grab point instead of following it rigidly (a mirror
		/// centred on the cursor): a rigid hold reads the very point it was caught on, so nothing moves.
		/// </summary>
		private void RefreshContact() {
			var held = HeldGrabbable;
			var node = held != null ? held.Target : null;

			if (!node || !_tip)
				return;

			_hasHeldLocal = true;
			_heldLocal    = node.InverseTransformPoint(_tip.position);
		}

		/// <summary>Releases the object this grabber pulled into the hand.</summary>
		public bool Release(float throwMultiplier) {
			if (State != GrabState.Held)
				return false;

			_hasHeldLocal  = false;
			_hasHoldLocal  = false;
			_hasOriginPose = false;
			_momentum      = 0f;
			PublishHold(null);

			return TryReleaseHeld(throwMultiplier);
		}

		private static EntityRig RigOf(Hand part)
			=> part.Side == HandSide.Left ? EntityRig.LeftHand : EntityRig.RightHand;
	}
}
