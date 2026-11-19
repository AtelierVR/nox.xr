using Nox.Avatars.Hand;
using Nox.CCK.Interactions;
using Nox.Interactions;
using UnityEngine;

namespace Nox.XR.Player {
	/// <summary>
	/// Finger pose of the grab: the fingers are shaped on the object once it is in place, then re-applied after the avatar
	/// animation every frame, and opened again after a release.
	/// <para>
	/// The skeleton pose of the hand (<c>HandSkeletonPose</c>) stays quiet while the hand owns the fingers, see
	/// <see cref="IsConformingFingers"/>.
	/// </para>
	/// </summary>
	public partial class Hand {
		private const float ConformRetryMax = 0.5f;

		private const float ConformSettleFallback = 0.5f;

		[Header("Fingers")]
		[SerializeField, Tooltip("Bend the fingers around the object when it is grabbed.")]
		private bool conformFingers = true;

		[SerializeField, Min(0f), Tooltip("Delay between two attempts of the finger sweep, in seconds, while the object is not shaped yet. Grows up to 0.5 s per attempt.")]
		private float conformRetry = 0.05f;

		[SerializeField, Min(4), Tooltip("Sweep resolution used to bend a finger until it touches the object.")]
		private int conformSteps = InteractionConstants.DefaultConformSteps;

		[SerializeField, Min(0f), Tooltip("Gap under which the hand is considered in contact with the object, in metres.")]
		private float contactDistance = 0.02f;

		[SerializeField, Min(0.01f), Tooltip("Time the fingers take to open again after a release, in seconds.")]
		private float relaxDuration = 0.2f;

		private HandFingerConformer _conformer;

		private HandPhysical _physicalPart;

		private ConfigurableJoint _joint;

		/// <summary>Whether the fingers are opening again after a release (see <see cref="IsConformingFingers"/>).</summary>
		private bool _relaxing;

		/// <summary>Object this hand wraps its fingers around, or <c>null</c>.</summary>
		private IGrabbable _conformTarget;

		private bool _conformDone = true;
		private float _conformRetryAt;
		private float _conformFallbackAt;
		private int _conformTries;
		private Vector3 _conformObjectOffset;
		private bool _conformEager;
		private bool _previewConforming;
		/// <summary>Whether the palm is against the object (edge of <see cref="OnContact"/>).</summary>
		private bool _inContact;

		/// <summary>Raised when the palm touches the object it is wrapping the fingers around.</summary>
		public event System.Action<IGrabbable> OnContact;

		/// <summary>
		/// Whether this hand owns the finger pose: the fingers have been shaped around the held object, or they are opening
		/// again after a release. The skeleton pose of the hand stays quiet while this is true.
		/// <para>
		/// An object still travelling to the hand (a far grab, a <c>GrabbableToHand</c> grab) is not held yet: the
		/// skeleton keeps posing the fingers from the real hand until the object is in place.
		/// </para>
		/// </summary>
		public bool IsConformingFingers
			=> conformFingers && (_relaxing || (_conformTarget != null && _conformDone) || _previewConforming);

		/// <summary>
		/// Avatar hand the fingers are posed on: the physical duplicate of the part when it has one (it carries the pose and
		/// gives it back to the avatar), the avatar hand itself otherwise.
		/// </summary>
		private IHand PoseHand {
			get {
				if (!_physicalPart)
					_physicalPart = GetComponent<HandPhysical>();

				var physical = _physicalPart
					? _physicalPart.Physical
					: null;
				return physical
					? physical
					: Source;
			}
		}

		private void OnGrabSourceChanged(IHand source) {
			var part = GetComponent<HandPhysical>();
			var target = part && part.Physical
				? (IHand)part.Physical
				: source;

			_conformer = new HandFingerConformer(target);

			if (_conformTarget != null) {
				_conformDone = false;
				_conformRetryAt = 0f;
			}
		}

		/// <summary>
		/// Schedules the fingers to wrap the object: the skeleton keeps posing the hand until the object is in
		/// its final position relative to the hand (see <see cref="HasArrived"/>), then the fingers are
		/// shaped on that very frame.
		/// </summary>
		public void WrapHeld(IGrabbable grabbable, Vector3 objectOffset = default) {
			_conformTarget = grabbable;
			_inContact = false;
			_conformObjectOffset = objectOffset;
			_conformEager = true;
			_previewConforming = false;

			_conformer.Reset();
			_conformRetryAt = 0f;
			_conformFallbackAt = Time.time + ConformSettleFallback;
			_conformTries = 0;
			_conformDone = false;
		}

		/// <summary>Drops the finger pose: the fingers open again and the hand targets objects once more.</summary>
		private void EndConform() {
			_conformTarget = null;
			_conformDone = true;
			_inContact = false;
			_conformEager = false;
			_conformObjectOffset = Vector3.zero;
			_relaxing = true;
		}

		/// <summary>
		/// Shapes the fingers on a grabbable the hand only aims at, at the offset the object would take in the
		/// palm.
		/// </summary>
		public void PreviewConform(IGrabbable grabbable, Vector3 objectOffset) {
			if (!conformFingers || _conformer == null || grabbable == null)
				return;

			_previewConforming = true;
			_conformer.TryConform(grabbable, default, conformSteps, objectOffset);
			_conformer.Snap();
		}

		/// <summary>Hands the fingers back to the animation after the hand stops aiming at a grabbable.</summary>
		public void ClearPreviewConform() {
			_previewConforming = false;

			if (_conformTarget == null && _conformer != null)
				_conformer.Reset();
		}

		/// <summary>Re-applies the held finger pose after the animation, and relaxes it after a release.</summary>
		private void UpdateConform() {
			if (_conformer == null)
				return;

			// The target is only ever set by this hand's own hold, so a hand that is no longer holding must let go of
			// the pose - the grabbable can still be Held by another hand at that point.
			if (_conformTarget != null && (_conformTarget.State != GrabState.Held || _grabState != GrabState.Held))
				EndConform();

			if (_conformTarget != null) {
				if (conformFingers)
					Conform();

				return;
			}

			if (!_relaxing)
				return;

			_conformer.Tick(Time.deltaTime / Mathf.Max(0.01f, relaxDuration));

			if (_conformer.IsRelaxed)
				_relaxing = false;
		}

		/// <summary>
		/// Measures the fingers against the object once it is in its final position, then keeps re-applying the shape:
		/// shaping the fingers resizes the palm and finger colliders, which drops their ignore pairs, so the held object
		/// has to ignore them again or the hand is pushed away from what it carries.
		/// </summary>
		private void Conform() {
			var contact = IsInContact(_conformTarget);

			if (contact && !_inContact) {
				_inContact = true;
				OnContact?.Invoke(_conformTarget);
			}
			else if (!contact) {
				_inContact = false;
			}

			var ready = _conformEager
				? Time.time >= _conformRetryAt
				: contact && Time.time >= _conformRetryAt && (HasArrived(_conformTarget) || Time.time >= _conformFallbackAt);

			if (!_conformDone && ready) {
				if (_conformer.TryConform(_conformTarget, default, conformSteps, _conformObjectOffset)) {
					_conformer.Snap();
					_conformDone = true;
					_conformEager = false;
					_conformObjectOffset = Vector3.zero;

					if (_conformTarget is GrabbableBase held)
						held.RefreshIgnores();
				}
				else {
					_conformEager = false;
					_conformObjectOffset = Vector3.zero;
					_conformRetryAt = Time.time + NextConformDelay();
				}
			}

			if (_conformDone)
				_conformer.ApplyPose();
		}

		/// <summary>Whether the palm touches the object, by penetration or by proximity.</summary>
		public bool IsInContact(IGrabbable grabbable) {
			if (grabbable == null)
				return false;

			var radius = Mathf.Max(contactDistance, Physics.defaultContactOffset * 2f);
			var origin = HoldingPoint;

			if (grabbable is IGrabColliders holder && holder.GrabColliders != null) {
				var colliders = holder.GrabColliders;

				for (var i = 0; i < _handColliders.Count; i++) {
					var own = _handColliders[i];
					if (!own)
						continue;

					for (var j = 0; j < colliders.Count; j++) {
						var other = colliders[j];
						if (!other)
							continue;

						if (Physics.ComputePenetration(
								own, own.transform.position, own.transform.rotation,
								other, other.transform.position, other.transform.rotation,
								out _, out _))
							return true;
					}
				}

				for (var j = 0; j < colliders.Count; j++) {
					var other = colliders[j];
					if (!other)
						continue;

					if ((other.ClosestPoint(origin) - origin).sqrMagnitude <= radius * radius)
						return true;
				}

				return false;
			}

			var target = grabbable.Target;
			return target && (target.position - origin).sqrMagnitude <= radius * radius;
		}

		/// <summary>
		/// Delay before the next finger sweep. It backs off while the hand is not shaped: the avatar hand keeps
		/// settling for a moment after the object lands, so an early sweep can miss the object (every finger
		/// then stays open) and the same sweep a little later wraps it properly.
		/// </summary>
		private float NextConformDelay() {
			_conformTries++;
			var delay = Mathf.Max(conformRetry, 0.01f);
			return Mathf.Clamp(delay * _conformTries, delay, ConformRetryMax);
		}

		/// <summary>
		/// Whether the object is in its final position relative to the hand. The hold blends the object onto the grab point
		/// (see <c>GrabbableBase</c>) and <c>HoldFollower</c> writes that position after this component would run at an
		/// early order, so shaping the fingers before the object has settled sweeps them against a pose it does not have
		/// yet. AutoHand never poses a hand before the object is caught; this is the equivalent gate.
		/// </summary>
		private static bool HasArrived(IGrabbable grabbable)
			=> !(grabbable is GrabbableBase held) || held.PhysicsHeld || held.IsHoldSettled;
	}
}
