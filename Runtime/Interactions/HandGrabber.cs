using System;
using System.Collections.Generic;
using Nox.Avatars.Hand;
using Nox.CCK.Interactions;
using Nox.Interactions;
using Nox.XR.Player;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Interactions {
	/// <summary>
	/// Near grab of a physical hand, following the AutoHand pipeline: a targeting tick highlights the object
	/// the palm is aimed at, a press grabs it (blending the object to the holder over a distance-scaled
	/// duration), the fingers wrap around its shape, and a release throws it with the measured hand speed.
	/// <para>
	/// It is a <see cref="GrabberBase"/>, so the grabbable contract of <c>nox.interactions</c> does the
	/// holding: this component only feeds it the contact, the duration, the finger conformer and the optional
	/// joint. The grab point is the hand itself (the tracked node that is pulled towards the controller).
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Interactions/Hand Grabber")]
	[DefaultExecutionOrder(11000)]
	[RequireComponent(typeof(Hand))]
	public class HandGrabber : GrabberBase, IGrabberBody, IPhysicsGrabber {
		[SerializeField, Tooltip("Hand part this grabber belongs to. Found on this GameObject when empty.")]
		private Hand hand;

		[SerializeField, Tooltip("Palm used for targeting and contact distances. Defaults to the hand follow target.")]
		private Transform palm;

		[SerializeField, Min(1f), Tooltip("How many times per second the target is recomputed (AutoHand uses 30).")]
		private float targetRate = 30f;

		[SerializeField, Min(0f), Tooltip("Shortest grab blend, in seconds (contact).")]
		private float minGrabDuration = InteractionConstants.MinGrabDuration;

		[SerializeField, Min(0f), Tooltip("Longest grab blend, in seconds (full reach).")]
		private float maxGrabDuration = InteractionConstants.MaxGrabDuration;

		[SerializeField, Tooltip("Bend the fingers around the object when it is grabbed.")]
		private bool conformFingers = true;

		[SerializeField, Min(0f), Tooltip("Delay between two attempts of the finger sweep, in seconds, while the object is not shaped yet. Grows up to 0.5 s per attempt.")]
		private float conformRetry = 0.05f;

		[SerializeField, Min(4), Tooltip("Sweep resolution used to bend a finger until it touches the object.")]
		private int conformSteps = InteractionConstants.DefaultConformSteps;

		[SerializeField, Tooltip("Add a ConfigurableJoint on top of the hold (dynamic hold only).")]
		private bool bindJoint;

		[SerializeField, Min(0f), Tooltip("Break force of that joint, in newtons.")]
		private float jointBreakForce = InteractionConstants.DefaultJointBreakForce;

		[SerializeField, Min(0f), Tooltip("Distance the physical hand may lag behind its follow point before it releases a heavy object, in metres.")]
		private float physicalReleaseDistance = 0.5f;

		[SerializeField, Min(0f), Tooltip("Gap under which the hand is considered in contact with the object, in metres.")]
		private float contactDistance = 0.02f;

		[SerializeField, Min(0.01f), Tooltip("Time the fingers take to open again after a release, in seconds.")]
		private float relaxDuration = 0.2f;

		[Header("Direct grab")]
		[SerializeField, Tooltip("How the hand brings an object into the hand, when the grabbable has no preference.")]
		private GrabType grabType = GrabType.HandToGrabbable;

		[SerializeField, Min(0f), Tooltip("Heaviest object (kg) the hand pulls back to its follow position. A heavier object comes to the hand instead.")]
		private float maxReachMass = 2f;

		[SerializeField, Min(0f), Tooltip("Time the hand takes to reach the object, in seconds.")]
		private float reachDuration = 0.08f;

		[SerializeField, Min(0f), Tooltip("Time the hand takes to come back to its follow position with the object, in seconds.")]
		private float returnDuration = 0.2f;

		[SerializeField, Min(0f), Tooltip("Maximum distance the hand moves toward the object, in metres.")]
		private float maxReachOffset = 0.15f;

		private enum ReachPhase {
			None,
			Reach,
			Return
		}

		private const float ConformRetryMax = 0.5f;

		private const float ConformSettleFallback = 0.5f;

		private readonly List<Collider> _bodyColliders = new();

		private readonly List<Collider> _handColliders = new();

		private bool _inContact;

		private HandFingerConformer _conformer;
		private ConfigurableJoint _joint;
		private float _nextTarget;
		private bool _relaxing;

		private ReachPhase _phase;
		private Vector3 _reachFrom;
		private Vector3 _reachTo;
		private Vector3 _reachOffset;
		private float _phaseElapsed;
		private float _phaseDuration;
		private IGrabbable _pendingTarget;
		private RaycastHit _pendingContact;
		private float _conformRetryAt;
		private float _conformFallbackAt;
		private int _conformTries;
		private bool _conformDone = true;
		private IGrabbable _conformTarget;

		/// <summary>Hand part this grabber drives.</summary>
		public Hand HandPart {
			get => hand;
			set => hand = value;
		}

		/// <summary>Palm used for the targeting distances.</summary>
		public Transform Palm
			=> palm ? palm : hand ? hand.MoveTo : transform;

		/// <summary>Whether an object is currently held.</summary>
		public bool IsHolding
			=> State == GrabState.Held;

		/// <summary>
		/// Whether this grabber owns the finger pose: the fingers have been shaped around the held object, or
		/// they are opening again after a release. The skeleton pose of the hand stays quiet while this is true.
		/// <para>
		/// An object still travelling to the hand (a far grab, a <c>GrabbableToHand</c> grab) is not held yet:
		/// the skeleton keeps posing the fingers from the real hand until the object is in place.
		/// </para>
		/// </summary>
		public bool IsConformingFingers
			=> conformFingers && (_relaxing || (_conformTarget != null && _conformDone));

		/// <summary>Body colliders a held object ignores (see <see cref="IGrabberBody"/>).</summary>
		public IReadOnlyList<Collider> BodyColliders
			=> _bodyColliders;

		public bool PhysicsJoint
			=> true;

		public event Action<IGrabbable> OnContact;

		/// <summary>Finger conformer of the current avatar hand, or <c>null</c>.</summary>
		public HandFingerConformer Conformer
			=> _conformer;

		/// <summary>
		/// Wires the grabber to a hand: grab point, rig side, reach, layers, highlight and fingers.
		/// Called by the controller when the part registers, and safe to call again after an avatar change.
		/// </summary>
		public void Configure(Hand part, float reach, LayerMask layers, Material highlight, bool conform = true) {
			hand = part;
			conformFingers = conform;

			SetGrabPoint(part ? part.transform : transform);
			SetRig(part ? RigOf(part) : EntityRig.None);
			SetKind(GrabKind.Direct);
			ReachDistance = reach;
			TargetLayers = layers;

			HighlightTargets = true;

			if (highlight)
				HighlightMaterial = highlight;

			RebuildBody();
			Subscribe();
		}

		protected override void Awake() {
			base.Awake();

			SetKind(GrabKind.Direct);

			if (!hand)
				hand = GetComponent<Hand>();

			if (!palm && hand)
				palm = hand.MoveTo;

			if (Rig == EntityRig.None && hand)
				SetRig(RigOf(hand));

			if (TargetLayers == 0)
				TargetLayers = InteractionConstants.GrabbableMask;

			if (ReachDistance <= 0f)
				ReachDistance = InteractionConstants.DefaultReachDistance;
		}

		private void OnEnable()
			=> Subscribe();

		private void OnDisable() {
			Unsubscribe();
			EndReach();

			if (State == GrabState.Held)
				Release(0f);
		}

		private void Subscribe() {
			if (!hand)
				return;

			hand.SourceChanged.RemoveListener(OnHandSourceChanged);
			hand.SourceChanged.AddListener(OnHandSourceChanged);
			OnHandSourceChanged(hand.Source);
		}

		private void Unsubscribe() {
			if (hand)
				hand.SourceChanged.RemoveListener(OnHandSourceChanged);
		}

		private void OnHandSourceChanged(IHand source) {
			_conformer = new HandFingerConformer(source);

			if (_conformTarget != null) {
				_conformDone = false;
				_conformRetryAt = 0f;
			}
		}

		/// <summary>
		/// AutoHand-style targeting tick: recompute the object under the palm a few times per second and
		/// highlight it, so a press immediately has a contact to grab.
		/// </summary>
		private void Update() {
			if (!hand || !hand.PartEnabled || State != GrabState.Free || _phase != ReachPhase.None || _conformTarget != null)
				return;

			if (Time.time < _nextTarget)
				return;

			_nextTarget = Time.time + 1f / Mathf.Max(1f, targetRate);

			if (TryFindTarget(out var grabbable, out var contact))
				SetTarget(grabbable, contact);
			else
				ClearTarget(CurrentTarget);
		}

		/// <summary>
		/// Grabs the current target. The blend duration scales with the distance, like AutoHand's
		/// <c>GetGrabTime</c>: a contact grab is nearly instant, a reach grab takes the longest.
		/// </summary>
		public bool Grab() {
			if (State != GrabState.Free || CurrentTarget == null || _phase != ReachPhase.None) {
				Logger.LogDebug($"[HandGrab] ignored: state={State}, target={(CurrentTarget != null ? CurrentTarget.Target?.name : null)}, phase={_phase}", this, tag: nameof(HandGrabber));
				return false;
			}

			var contact = Contact;
			var target = CurrentTarget;
			var type = ResolveGrabType(target);

			if (type == GrabType.HandToGrabbable && CanReach(target)) {
				BeginReach(target, contact);
				Logger.LogDebug($"[HandGrab] target={target.Target?.name} type=HandToGrabbable -> reach", this, tag: nameof(HandGrabber));
				return true;
			}

			var duration = type == GrabType.InstantGrab ? 0f : DurationTo(contact);
			var grabbed = PerformGrab(target, contact, duration);
			Logger.LogDebug($"[HandGrab] target={target.Target?.name} type={type} duration={duration:0.000} -> {grabbed}", this, tag: nameof(HandGrabber));
			return grabbed;
		}

		/// <summary>Attaches the object to the hand, shapes the fingers and links the joint.</summary>
		private bool PerformGrab(IGrabbable grabbable, in RaycastHit contact, float duration) {
			if (grabbable == null)
				return false;

			RebuildBody();

			if (!TryGrab(grabbable, contact, duration))
				return false;

			_relaxing = false;

			WrapHeld(grabbable);

			if (grabbable is GrabbableBase output && output.Body != null) {
				var physics = output.PhysicsHeld;

				if (physics || (bindJoint && output.Hold.dynamic)) {
					var anchor = contact.collider ? contact.point : output.transform.position;

					if (physics && hand)
						hand.BeginPhysical(output.Hold.followSpeed, output.Body.mass);

					_joint = GrabJointBinder.Bind(this, output, anchor, jointBreakForce, physics);
				}
			}

			return true;
		}

		/// <summary>
		/// Schedules the fingers to wrap the object: the skeleton keeps posing the hand until the object is in
		/// its final position relative to the grabber (see <see cref="HasArrived"/>), then the fingers are
		/// shaped on that very frame. Called by this grabber's own grab and by
		/// <see cref="HandDistanceGrabber"/> after a pull.
		/// </summary>
		public void WrapHeld(IGrabbable grabbable) {
			_conformTarget = grabbable;
			_inContact = false;

			if (!conformFingers || _conformer == null) {
				_conformDone = true;
				return;
			}

			_conformer.Reset();
			_conformRetryAt = 0f;
			_conformFallbackAt = Time.time + ConformSettleFallback;
			_conformTries = 0;
			_conformDone = false;
		}

		/// <summary>Grab type of a grabbable: its own preference wins, else the one of the hand.</summary>
		private GrabType ResolveGrabType(IGrabbable grabbable) {
			if (grabbable is IGrabApproach approach) {
				if (approach.InstantGrab)
					return GrabType.InstantGrab;

				if (approach.GrabType != GrabType.Default)
					return approach.GrabType;
			}

			return grabType == GrabType.Default ? GrabType.HandToGrabbable : grabType;
		}

		/// <summary>
		/// Whether the hand may reach for the object and pull it back. A heavy object is not dragged back:
		/// it comes to the hand instead, like AutoHand when the hand cannot move it.
		/// </summary>
		private bool CanReach(IGrabbable grabbable) {
			if (grabbable is GrabbableBase { IsPhysicsHold: true })
				return true;

			var body = (grabbable as IGrabBody)?.Body;
			return body == null || body.mass <= maxReachMass;
		}

		/// <summary>Starts the hand-to-object phase: the hand moves toward the contact, then grabs there.</summary>
		private void BeginReach(IGrabbable grabbable, in RaycastHit contact) {
			_pendingTarget = grabbable;
			_pendingContact = contact;

			var delta = contact.collider ? contact.point - hand.transform.position : Vector3.zero;
			if (delta.magnitude > maxReachOffset)
				delta = delta.normalized * maxReachOffset;

			_reachFrom = Vector3.zero;
			_reachTo = delta;
			_reachOffset = Vector3.zero;
			_phaseElapsed = 0f;
			_phaseDuration = Mathf.Max(0f, reachDuration);
			_phase = ReachPhase.Reach;

			ClearTarget(grabbable);
		}

		/// <summary>
		/// Releases the held object. The throw comes from the grabbable's own motion tracker, which sampled
		/// this grab point while it was held; <paramref name="throwMultiplier"/> scales it.
		/// </summary>
		public bool Release(float throwMultiplier) {
			if (_phase == ReachPhase.Reach) {
				EndReach();
				return true;
			}

			if (_phase == ReachPhase.Return)
				EndReach();

			if (State != GrabState.Held)
				return false;

			UnbindJoint();

			if (!TryReleaseHeld(throwMultiplier))
				return false;

			if (hand)
				hand.EndPhysical();

			_conformDone = true;

			_relaxing = true;
			return true;
		}

		/// <summary>Re-applies the held finger pose after the animation, and relaxes it after a release.</summary>
		private void LateUpdate() {
			UpdateReach();

			if (_joint && (State != GrabState.Held || _joint.connectedBody == null))
				CleanupHold();

			if (State == GrabState.Held && _joint && hand && hand.Physical && hand.PhysicalStretch > physicalReleaseDistance) {
				Logger.LogDebug($"[HandGrab] released: hand lag {hand.PhysicalStretch:0.000} m", this, tag: nameof(HandGrabber));
				Release(0f);
			}

			if (_conformer == null)
				return;

			if (_conformTarget != null && _conformTarget.State != GrabState.Held) {
				_conformTarget = null;
				_conformDone = true;
				_relaxing = true;
			}

			if (_conformTarget != null) {
				if (conformFingers) {
					var contact = IsInContact(_conformTarget);

					if (contact && !_inContact) {
						_inContact = true;
						OnContact?.Invoke(_conformTarget);
					}
					else if (!contact) {
						_inContact = false;
					}

					var ready = contact
						&& Time.time >= _conformRetryAt
						&& (HasArrived(_conformTarget) || Time.time >= _conformFallbackAt);

					if (!_conformDone && ready) {
						if (_conformer.TryConform(_conformTarget, default, conformSteps)) {
							_conformer.Snap();
							_conformDone = true;
						}
						else {
							_conformRetryAt = Time.time + NextConformDelay();
						}
					}

					if (_conformDone)
						_conformer.ApplyPose();
				}

				return;
			}

			if (!_relaxing)
				return;

			_conformer.Tick(Time.deltaTime / Mathf.Max(0.01f, relaxDuration));

			if (_conformer.IsRelaxed)
				_relaxing = false;
		}

		/// <summary>
		/// Moves the hand toward the object it reaches for, then brings it back to its follow position.
		/// <para>
		/// The object is attached at full reach (no blend), so it follows the hand back. The offset is applied
		/// after <c>XRFollow</c> (this component runs later), so it survives the tracked pose write.
		/// </para>
		/// </summary>
		private void UpdateReach() {
			if (_phase == ReachPhase.None || !hand)
				return;

			if (_phase == ReachPhase.Return && hand.Physical) {
				EndReach();
				return;
			}

			_phaseElapsed += Time.deltaTime;

			var t = _phaseDuration <= 0f ? 1f : Mathf.Clamp01(_phaseElapsed / _phaseDuration);
			_reachOffset = Vector3.Lerp(_reachFrom, _reachTo, t * t * (3f - 2f * t));
			hand.transform.position += _reachOffset;

			if (t < 1f)
				return;

			if (_phase == ReachPhase.Reach) {
				if (!PerformGrab(_pendingTarget, _pendingContact, 0f)) {
					EndReach();
					return;
				}

				_reachFrom = _reachTo;
				_reachTo = Vector3.zero;
				_phaseElapsed = 0f;
				_phaseDuration = Mathf.Max(0f, returnDuration);
				_phase = ReachPhase.Return;
				return;
			}

			EndReach();
		}

		private void EndReach() {
			_phase = ReachPhase.None;
			_reachOffset = Vector3.zero;
			_reachFrom = Vector3.zero;
			_reachTo = Vector3.zero;
			_phaseElapsed = 0f;
			_pendingTarget = null;
			_pendingContact = default;
		}

		private void OnDestroy() {
			CleanupHold();
		}

		private void CleanupHold() {
			UnbindJoint();

			if (hand)
				hand.EndPhysical();
		}

		private void UnbindJoint() {
			if (!_joint)
				return;

			GrabJointBinder.Unbind(_joint);
			_joint = null;
		}

		private void OnJointBreak(float breakForce) {
			_joint = null;

			if (State != GrabState.Held)
				return;

			Logger.LogDebug($"[HandGrab] joint broken at {breakForce:0} N", this, tag: nameof(HandGrabber));
			Release(0f);
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
		/// Whether the object is in its final position relative to the grabber. The hold blends the object
		/// onto the grab point (see <c>GrabbableBase</c>) and <c>HoldFollower</c> writes that position after
		/// this component would run at an early order, so shaping the fingers before the object has settled
		/// sweeps them against a pose it does not have yet. AutoHand never poses a hand before the object is
		/// caught; this is the equivalent gate.
		/// </summary>
		private static bool HasArrived(IGrabbable grabbable)
			=> !(grabbable is GrabbableBase held) || held.PhysicsHeld || held.IsHoldSettled;

		public bool IsInContact(IGrabbable grabbable) {
			if (grabbable == null)
				return false;

			var radius = Mathf.Max(contactDistance, Physics.defaultContactOffset * 2f);
			var origin = Palm.position;

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

		private void RebuildBody() {
			_bodyColliders.Clear();
			_handColliders.Clear();

			if (!hand)
				return;

			hand.GetComponentsInChildren(true, _handColliders);

			var player = hand.Player;
			if (player?.BodyColliders != null) {
				var colliders = player.BodyColliders;
				for (var i = 0; i < colliders.Count; i++)
					if (colliders[i])
						_bodyColliders.Add(colliders[i]);
			}

			for (var i = 0; i < _handColliders.Count; i++) {
				var collider = _handColliders[i];
				if (collider && !_bodyColliders.Contains(collider))
					_bodyColliders.Add(collider);
			}
		}

		private static EntityRig RigOf(Hand part)
			=> part.Side == HandSide.Left ? EntityRig.LeftHand : EntityRig.RightHand;
	}
}
