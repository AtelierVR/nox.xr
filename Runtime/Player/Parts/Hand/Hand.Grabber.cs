using System;
using System.Collections.Generic;
using Nox.CCK.Interactions;
using Nox.Interactions;
using Nox.XR.Runtime.Connectors;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;
using GrabSource = Nox.Interactions.GrabSource;

namespace Nox.XR.Player {
	/// <summary>
	/// Grab contract of the hand: the hand <b>is</b> the grabber, the way AutoHand keeps everything on its <c>Hand</c>, so
	/// the grabbable contract of <c>nox.interactions</c> addresses the hand itself instead of a separate component.
	/// <para>
	/// The state machine is the one of <c>GrabberBase</c> (held / target / state), the targeting is the shared
	/// <see cref="GrabberTargeting"/>, and the grab pipeline lives in <c>Hand.Grab.cs</c>.
	/// </para>
	/// </summary>
	public partial class Hand {
		[Header("Grab")]
		[SerializeField, Min(0f), Tooltip("Reach of the near grab, measured from the grab point, in metres.")]
		private float grabReach = InteractionConstants.DefaultReachDistance;

		[SerializeField, Tooltip("Layers of the grabbables the palm can take. Empty uses the grabbable layer mask.")]
		private LayerMask grabLayers;

		[SerializeField, Tooltip("Layers ignored by the targeting, whatever the grab layers say (AutoHand's ignoreGrabCheckLayers): the palm grabs and highlights through them.")]
		private LayerMask grabIgnoreLayers;

		[SerializeField, Tooltip("Highlight the grabbable the palm is aimed at.")]
		private bool grabHighlight = true;

		[SerializeField, Tooltip("Highlight material used while the palm aims at a grabbable.")]
		private Material grabHighlightMaterial;

		[SerializeField, Min(1f), Tooltip("How many times per second the target is recomputed (AutoHand uses 30).")]
		private float targetRate = 30f;

		private readonly Collider[] _targetingBuffer = new Collider[GrabberTargeting.BufferSize];

		private readonly List<Collider> _grabBodyColliders = new();

		private readonly List<Collider> _handColliders = new();

		private IGrabbable _held;

		private IGrabbable _grabTarget;

		private RaycastHit _grabHit;

		private GrabState _grabState = GrabState.Free;

		private float _nextTarget;

		/// <summary>Object this hand holds, or <c>null</c>.</summary>
		public IGrabbable HeldGrabbable
			=> _held;

		/// <summary>Object the palm is aimed at (highlighted), or <c>null</c>.</summary>
		public IGrabbable CurrentTarget
			=> _grabTarget;

		/// <summary>Contact that produced the current target.</summary>
		public RaycastHit Contact
			=> _grabHit;

		/// <summary>Grab state of this hand.</summary>
		public GrabState State
			=> _grabState;

		/// <summary>Transform the object is attached to: the hand itself (the physical body, not the controller).</summary>
		public Transform GrabPoint
			=> transform;

		/// <summary>
		/// Centre of the palm volume (AutoHand's <c>palmTransform</c>): where an object coming into the hand is aimed, and the
		/// origin the targeting and the contact are measured from. The palm node of the avatar is the wrist, which sits a few
		/// centimetres behind the palm itself.
		/// </summary>
		public Vector3 HoldingPoint
			=> PalmVolume is { } palm ? palm.bounds.center : Palm.position;

		/// <summary>
		/// Point of the palm skin facing <paramref name="worldPoint"/>: an object brought into the hand lands there, against
		/// the palm, instead of being centred inside the palm volume.
		/// <para>
		/// A point inside the palm has no side it faces: the grabber then falls back on the centre of the palm, and the
		/// grabbable separates the object from the palm volume
		/// (<see cref="Nox.CCK.Interactions.GrabbableBase"/> does it with the shortest way out).
		/// </para>
		/// </summary>
		public bool TryGetPalmSurface(Vector3 worldPoint, out Vector3 point) {
			point = HoldingPoint;

			if (PalmVolume is not { } palm || palm.bounds.Contains(worldPoint))
				return false;

			var centre = palm.bounds.center;
			var direction = worldPoint - centre;

			if (direction.sqrMagnitude < InteractionConstants.ContactEpsilon)
				return false;

			var reach = palm.bounds.extents.magnitude * 2f;
			point = palm.ClosestPoint(centre + direction.normalized * reach);
			return true;
		}

		/// <summary>Volume of the palm, the part an object coming into the hand rests against.</summary>
		public Collider PalmVolume
			=> GetPhysical() is { BodyCollider: { } palm }
				? palm
				: null;

		public GrabKind Kind
			=> GrabKind.Direct;

		/// <summary>A hand is a direct source; the name clashes with the avatar hand, so it is explicit.</summary>
		GrabSource IGrabber.Source
			=> GrabSource.Direct;

		/// <summary>Rig slot, in the vocabulary of the interaction contract (the part uses its own enum).</summary>
		EntityRig IGrabber.Rig
			=> Side == HandSide.Left
				? EntityRig.LeftHand
				: EntityRig.RightHand;

		public float ReachDistance {
			get => grabReach;
			set => grabReach = Mathf.Max(0f, value);
		}

		public LayerMask TargetLayers {
			get => grabLayers;
			set => grabLayers = value;
		}

		public LayerMask IgnoreLayers {
			get => grabIgnoreLayers;
			set => grabIgnoreLayers = value;
		}

		public bool HighlightTargets {
			get => grabHighlight;
			set => grabHighlight = value;
		}

		public Material HighlightMaterial {
			get => grabHighlightMaterial;
			set => grabHighlightMaterial = value;
		}

		/// <summary>Colliders of the hand and of the player body a held object has to ignore.</summary>
		public IReadOnlyList<Collider> BodyColliders
			=> _grabBodyColliders;

		public bool PhysicsJoint
			=> true;

		/// <summary>
		/// Whether the grab in progress brings the object onto the palm instead of holding it where it was caught
		/// (<see cref="GrabType.GrabbableToHand"/>): the grabbable then moves the object onto <see cref="HoldingPoint"/>,
		/// keeping its orientation. Set by <c>Grab</c> before the object is attached.
		/// </summary>
		public bool PullsToPoint
			=> _activeGrabType == GrabType.GrabbableToHand;

		/// <summary>Raised when this hand takes an object.</summary>
		public event Action<IGrabbable> Grabbed;

		/// <summary>Raised when this hand lets an object go.</summary>
		public event Action<IGrabbable> Released;

		/// <summary>Raised when the targeted object changes (with <c>null</c> when nothing is aimed at).</summary>
		public event Action<IGrabbable> TargetChanged;

		/// <summary>Whether the hand takes an object right now (a press would grab).</summary>
		public bool IsHolding
			=> _grabState == GrabState.Held;

		/// <summary>
		/// Pose of the controller, which is where the hand is tracked: an object held by several grabbers sits between them.
		/// </summary>
		public bool TryGetTrackedPose(out Vector3 position, out Quaternion rotation) {
			position = Vector3.zero;
			rotation = Quaternion.identity;

			if (!HasTrackedPose)
				return false;

			position = TrackedPosition;
			rotation = TrackedRotation;

			return true;
		}

		/// <summary>
		/// Offsets the hand from its tracked pose: an object held by several grabbers drags each hand onto itself, the way
		/// AutoHand's <c>grabPositionOffset</c> keeps a second hand on what the first one carries.
		/// </summary>
		public void SetOffset(Vector3 position, Quaternion rotation)
			=> SetHoldOffset(position, rotation);

		/// <summary>Drops the offset: the hand follows its controller again.</summary>
		public void ClearOffset()
			=> ClearHoldOffset();

		/// <summary>
		/// Whether the follower carries the hand right now: a hand written to its tracked pose is moved by it too, so an
		/// object held by several grabbers can still drag it onto itself (see <c>Part.HoldOffsetPosition</c>). A hand the
		/// player does not drive has nowhere to put an offset, and an object must not try to pull it.
		/// </summary>
		public bool FollowsBody
			=> PhysicsFollow || Body;

		/// <summary>
		/// Whether this hand may take the object, the way AutoHand's <c>Hand.CanGrab</c> sits in front of the grabbable
		/// one: the hand must be free (a grab already in progress, a hold in place, or a release underway all refuse), and
		/// the object must accept this grabber.
		/// </summary>
		public bool CanGrab(IGrabbable grabbable)
			=> grabbable != null
			   && _held == null
			   && _grabState == GrabState.Free
			   && grabbable.CanBeGrabbedBy(this)
			   && CanReachPanel(grabbable);

		/// <summary>
		/// A panel that only lets itself be taken from one side (see <see cref="AdditionalGrabbable"/>): the palm has to
		/// be on that side, so a menu is never grabbed from the front, through its own screen.
		/// </summary>
		private bool CanReachPanel(IGrabbable grabbable) {
			var target = grabbable.Target;
			if (!target)
				return true;

			var conditions = target.GetComponentsInChildren<AdditionalGrabbable>(true);
			if (conditions.Length == 0)
				return true;

			var palm    = Palm;
			var forward = palm ? palm.forward : transform.forward;

			for (var i = 0; i < conditions.Length; i++)
				if (!conditions[i].CanGrab(HoldingPoint, forward))
					return false;

			return true;
		}

		/// <summary>Takes an object, if this hand is free and the object accepts it.</summary>
		public bool TryGrab(IGrabbable grabbable, in RaycastHit contact, float duration) {
			if (!CanGrab(grabbable))
				return false;

			ClearTarget(grabbable);

			if (!grabbable.TryAddGrabber(this, contact, duration))
				return false;

			_held = grabbable;
			_grabState = GrabState.Held;

			Grabbed?.Invoke(grabbable);
			return true;
		}

		/// <summary>Lets an object go, with a throw scaled by <paramref name="throwMultiplier"/>.</summary>
		public bool TryRelease(IGrabbable grabbable, float throwMultiplier) {
			if (grabbable == null || !ReferenceEquals(_held, grabbable))
				return false;

			if (!grabbable.TryRemoveGrabber(this, throwMultiplier))
				return false;

			_held = null;
			_grabState = GrabState.Free;

			Released?.Invoke(grabbable);
			return true;
		}

		public bool TryRelease(IGrabbable grabbable, float throwMultiplier, Vector3 linear, Vector3 angular) {
			if (!TryRelease(grabbable, throwMultiplier))
				return false;

			if (grabbable is IGrabThrow thrower)
				thrower.ApplyThrow(linear, angular, throwMultiplier);

			return true;
		}

		public bool TryReleaseHeld(float throwMultiplier)
			=> TryRelease(_held, throwMultiplier);

		public bool TryReleaseHeld(float throwMultiplier, Vector3 linear, Vector3 angular)
			=> TryRelease(_held, throwMultiplier, linear, angular);

		/// <summary>Highlights the target of the palm.</summary>
		public void SetTarget(IGrabbable grabbable, in RaycastHit contact) {
			if (ReferenceEquals(_grabTarget, grabbable)) {
				_grabHit = contact;
				return;
			}

			ClearTarget(_grabTarget);

			_grabTarget = grabbable;
			_grabHit = contact;

			if (grabHighlight && grabbable is IGrabHighlight highlight)
				highlight.SetHighlight(this, true, grabHighlightMaterial);

			TargetChanged?.Invoke(_grabTarget);
		}

		/// <summary>Drops the target and its highlight.</summary>
		public void ClearTarget(IGrabbable grabbable) {
			if (grabbable == null || !ReferenceEquals(_grabTarget, grabbable))
				return;

			if (grabbable is IGrabHighlight highlight)
				highlight.SetHighlight(this, false, grabHighlightMaterial);

			_grabTarget = null;
			_grabHit = default;

			TargetChanged?.Invoke(null);
		}

		/// <summary>
		/// Looks for a grabbable under the palm, the way AutoHand's highlighter does. The overlap and the contact are both
		/// measured from the palm, which is where a grabbed object comes onto the hand, so the distance the targeting
		/// reports is the one the object actually travels.
		/// </summary>
		public bool TryFindTarget(out IGrabbable grabbable, out RaycastHit contact)
			=> GrabberTargeting.TryFind(
				this,
				HoldingPoint,
				transform.forward,
				transform.right,
				grabReach,
				(grabLayers == 0 ? InteractionConstants.GrabbableMask : grabLayers) & ~grabIgnoreLayers,
				_targetingBuffer,
				out grabbable,
				out contact
			);

		/// <summary>Recollects the colliders of the hand (and of the player) a held object has to ignore.</summary>
		public void RebuildBody() {
			_grabBodyColliders.Clear();
			_handColliders.Clear();

			GetComponentsInChildren(true, _handColliders);

			var player = Player;
			if (player?.BodyColliders != null) {
				var colliders = player.BodyColliders;
				for (var i = 0; i < colliders.Count; i++)
					if (colliders[i])
						_grabBodyColliders.Add(colliders[i]);
			}

			for (var i = 0; i < _handColliders.Count; i++) {
				var collider = _handColliders[i];
				if (collider && !_grabBodyColliders.Contains(collider))
					_grabBodyColliders.Add(collider);
			}
		}

		/// <summary>AutoHand-style targeting tick: recompute the object under the palm a few times per second.</summary>
		private void UpdateTargeting() {
			if (Time.time < _nextTarget)
				return;

			RefreshTarget();
		}

		/// <summary>
		/// Recomputes the object under the palm now, instead of waiting for the next targeting tick: a press has to
		/// know whether the hand can take something before the ray is allowed to.
		/// </summary>
		/// <returns>Whether the palm has an object it can take.</returns>
		public bool RefreshTarget() {
			if (!PartEnabled || _grabState != GrabState.Free) {
				ClearTarget(CurrentTarget);
				return false;
			}

			_nextTarget = Time.time + 1f / Mathf.Max(1f, targetRate);

			if (!TryFindTarget(out var grabbable, out var contact)) {
				ClearTarget(CurrentTarget);
				return false;
			}

			SetTarget(grabbable, contact);
			return true;
		}

		private void Update()
			=> UpdateTargeting();

		/// <summary>
		/// AutoHand's gentle grab: while holding a weightless object the hand eases back to neutral instead of
		/// snapping, unless <see cref="useGentleGrab"/> is disabled on the hand or the object disables it.
		/// </summary>
		private void UpdateGentleGrab() {
			if (_held == null || _grabState != GrabState.Held)
				return;

			if (_held is GrabbableBase baseGrabbable && !baseGrabbable.IgnoreWeight && useGentleGrab)
				return;

			var grabType = ResolveGrabType(_held);

			if (grabType != GrabType.GrabbableToHand)
				return;

			var deltaDist = Vector3.Distance(MoveTo != null ? MoveTo.position : transform.position, _lastFollowPosition);
			var deltaRot = Quaternion.Angle(MoveTo != null ? MoveTo.rotation : transform.rotation, _lastFollowRotation);

			grabPositionOffset = Vector3.MoveTowards(grabPositionOffset, Vector3.zero, deltaDist * gentleGrabSpeed * Time.deltaTime * 60f);
			grabRotationOffset = Quaternion.RotateTowards(grabRotationOffset, Quaternion.identity, deltaRot * gentleGrabSpeed * Time.deltaTime * 60f);
		}

		/// <summary>
		/// Runs the reach phase, drops a joint that broke on its own, then re-applies (or relaxes) the finger pose after the
		/// avatar animation wrote the bones. Runs late (execution order 11000) so the object is in place first.
		/// </summary>
		private void LateUpdate() {
			if (_joint && (_grabState != GrabState.Held || _joint.connectedBody == null))
				CleanupHold();

			// the carrying hand may have let go while this one still holds the object: the joint passes to this hand
			if (!_joint && _grabState == GrabState.Held && _held is GrabbableBase { PhysicsHeld: true } driver)
				BindDriverJoint(driver, _grabHit);

			UpdatePreviewPose();
			UpdateConform();
			UpdateGentleGrab();
		}

		protected override void OnEnable() {
			base.OnEnable();

			SourceChanged.RemoveListener(OnGrabSourceChanged);
			SourceChanged.AddListener(OnGrabSourceChanged);
			OnGrabSourceChanged(Source);
		}

		protected override void OnDisable() {
			SourceChanged.RemoveListener(OnGrabSourceChanged);

			if (_grabState == GrabState.Held)
				Release(0f);

			base.OnDisable();
		}

		protected override void OnDestroy() {
			CleanupHold();
			base.OnDestroy();
		}

		/// <summary>Unlinks the joint and hands the body back to the tracker.</summary>
		private void CleanupHold() {
			UnbindJoint();
			EndPhysical();
		}

		private void UnbindJoint() {
			if (!_joint)
				return;

			GrabJointBinder.Unbind(_joint);
			_joint = null;
		}

		private void OnJointBreak(float breakForce) {
			_joint = null;

			if (_grabState != GrabState.Held)
				return;

			Logger.LogDebug($"[HandGrab] joint broken at {breakForce:0} N", this, tag: nameof(Hand));
			Release(0f);
		}
	}
}
