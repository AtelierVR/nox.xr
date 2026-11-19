using System.Collections.Generic;
using Nox.Avatars.Hand;
using Nox.CCK.Entities;
using Nox.CCK.Utils;
using UnityEngine;
using UnityEngine.Events;
using Logger = Nox.CCK.Utils.Logger;
using IGrabber = Nox.Interactions.IGrabber;
using IGrabberBody = Nox.Interactions.IGrabberBody;
using IGrabberFollower = Nox.Interactions.IGrabberFollower;
using IGrabberOffset = Nox.Interactions.IGrabberOffset;
using IGrabberPose = Nox.Interactions.IGrabberPose;
using IGrabPull = Nox.Interactions.IGrabPull;
using IGrabberPalm = Nox.Interactions.IGrabberPalm;
using IGrabberTargeting = Nox.Interactions.IGrabberTargeting;
using IPhysicsGrabber = Nox.Interactions.IPhysicsGrabber;

namespace Nox.XR.Player {
	public enum HandSide {
		Left,
		Right
	}

	[AddComponentMenu("Nox/XR/Player/Hand Part")]
	[DefaultExecutionOrder(11000)]
	[RequireComponent(typeof(Rigidbody))]
	public partial class Hand : Part, IGrabber, IGrabberTargeting, IGrabberBody, IPhysicsGrabber, IGrabberFollower, IGrabPull, IGrabberPalm {
		[Header("Rig")]
		[SerializeField]
		private HandSide side = HandSide.Right;

		[SerializeField, Tooltip("Rigidbody of the hand. Found on this GameObject when left empty.")]
		private Rigidbody body;

		[SerializeField, Tooltip("Tracked target the hand follows (a controller node). Created under the rig when empty: " +
								  "the body is never written to it, it is pulled towards it.")]
		private Transform moveTo;

		[SerializeField, Tooltip("Box enclosing the hand, used to prevent clipping. Optional.")]
		private BoxCollider encapsulationBox;

		[Header("Following")]
		[SerializeField, Tooltip("Whether the player drives this hand and measures its velocity.")]
		private bool enableMovement = true;

		[Header("Pivot")]
		[SerializeField, Tooltip("Extra rotation applied on top of the avatar pivot. It turns the hand AROUND its pivot, so the pivot stays exactly on the controller: use it to compensate the orientation convention of a controller model (see the v1 HandOffsetSetup).")]
		private Quaternion adjustRotation = Quaternion.identity;

		public static readonly Quaternion RightSideAdjustRotation = Quaternion.Euler(0f, 270f, 180f);

		public static readonly Quaternion LeftSideAdjustRotation = Quaternion.Euler(0f, 270f, 0f);

		[SerializeField, Tooltip("Extra position offset, in metres, applied in the frame of the tracked pose.")]
		private Vector3 adjustPosition;

		[Header("Physical hold")]
		[SerializeField, Min(0.01f), Tooltip("Lightest the physical hand can be, in kg (fallback when no follower drives its mass).")]
		private float physicalMinMass = 0.25f;

		private bool _physical;
		private float _physicalSpeed;


		protected override EntityRig DefaultRig
			=> side == HandSide.Left
				? EntityRig.LeftHand
				: EntityRig.RightHand;

		public HandSide Side {
			get => side;
			set => side = value;
		}

		public override Rigidbody Body
			=> body ? body : body = GetComponent<Rigidbody>();

		public override float FollowSpeed
			=> _physical ? _physicalSpeed : 0f;

		private Vector3 _lastPosition;
		private Quaternion _lastRotation = Quaternion.identity;

		private Vector3 _linearVelocity;

		private Vector3 _angularVelocity;

		public Vector3 LinearVelocity
			=> _linearVelocity;

		public Vector3 AngularVelocity
			=> _angularVelocity;

		public Transform MoveTo
			=> moveTo ? moveTo : transform;

		public override Transform TrackingSource
			=> TrackingSourceOverride ? TrackingSourceOverride : MoveTo;

		public static Hand Create(Hand template, EntityRig rig, Transform tracking) {
			if (!template || !tracking)
				return null;

			var hand = template.gameObject.Instantiate<Hand>(tracking);
			var handSide = rig == EntityRig.LeftHand ? HandSide.Left : HandSide.Right;

			hand.side = handSide;
			hand.body = hand.GetComponent<Rigidbody>();
			hand.moveTo = null;
			hand.encapsulationBox = null;
			hand.AdjustRotation = handSide == HandSide.Left ? LeftSideAdjustRotation : RightSideAdjustRotation;

			hand.Configure(handSide == HandSide.Left ? "HandLeft" : "HandRight", rig);
			return hand;
		}

		public Vector3 PivotOffset {
			get => _pivotOffset;
			set {
				if (_pivotOffset == value)
					return;

				_pivotOffset = value;
				ApplyRigTarget();
			}
		}

		public Quaternion PivotRotation {
			get => _pivotRotation;
			set {
				if (_pivotRotation == value)
					return;

				_pivotRotation = value;
				ApplyRigTarget();
			}
		}

		private Vector3 _pivotOffset;
		private Quaternion _pivotRotation = Quaternion.identity;

		public Quaternion AdjustRotation {
			get => adjustRotation;
			set {
				if (adjustRotation == value)
					return;

				adjustRotation = value;
				ApplyRigTarget();
			}
		}

		public Vector3 AdjustPosition {
			get => adjustPosition;
			set {
				if (adjustPosition == value)
					return;

				adjustPosition = value;
				ApplyRigTarget();
			}
		}

		public IHand Source {
			get => _source;
			set {
				if (ReferenceEquals(_source, value))
					return;

				_source = value;
				SourceChanged.Invoke(value);
			}
		}

		public UnityEvent<IHand> SourceChanged = new();

		private IHand _source;

		[SerializeField, Tooltip("Node the avatar rig drives: the wrist, a child of the hand. Carried by the template prefab.")]
		private Transform _target;

		private void Awake() {
			if (!_target)
				Logger.LogError($"No rig target on {name}: the rig cannot drive this hand.", this, tag: nameof(Hand));

			ApplyRigTarget();
		}

		public override Transform RigTarget
			=> _target ? _target : MoveTo;

		public bool Physical
			=> _physical;

		/// <summary>Whether one of the fingers is pressing something with its poke.</summary>
		public bool Gripping {
			get {
				var physical = GetPhysical();
				return physical && physical.Gripping;
			}
		}

		public float PhysicalStretch
			=> _physical && Body ? Vector3.Distance(Body.position, FollowPoint.position) : 0f;

		/// <summary>
		/// Turns the hand physical: it is then driven by its follower while it carries a joint-held object, capped at
		/// <paramref name="maxSpeed"/> (the held object's own speed, AutoHand's <c>maxHeldVelocity</c>). The mass is
		/// left to the follower, which derives it from the lag, the way AutoHand's <c>HandFollow.SetMass</c> does.
		/// </summary>
		public void BeginPhysical(float maxSpeed) {
			if (!Body)
				return;

			if (!_physical) {
				_physical = true;
				FollowPoint.SetPositionAndRotation(transform.position, transform.rotation);
			}

			_physicalSpeed = maxSpeed > 0f ? maxSpeed : 0f;
			Body.WakeUp();
		}

		public void EndPhysical() {
			if (!_physical)
				return;

			_physical = false;
			_physicalSpeed = 0f;

			if (!Body)
				return;

			if (PhysicsFollow) {
				Body.WakeUp();
				return;
			}

			Body.mass = physicalMinMass;

			Body.linearVelocity = Vector3.zero;
			Body.angularVelocity = Vector3.zero;
			Body.isKinematic = true;

			SnapToTarget();
		}

		private void ApplyRigTarget() {
			var track = transform;
			if (!track || !_target)
				return;

			var rotation = Quaternion.Inverse(_pivotRotation) * adjustRotation;
			var position = -(rotation * _pivotOffset) + adjustPosition;

			var scale = track.lossyScale;
			_target.localRotation = rotation;
			_target.localPosition = new Vector3(
				Mathf.Abs(scale.x) > float.Epsilon ? position.x / scale.x : position.x,
				Mathf.Abs(scale.y) > float.Epsilon ? position.y / scale.y : position.y,
				Mathf.Abs(scale.z) > float.Epsilon ? position.z / scale.z : position.z);
		}

		public Vector3 MoveToPosition
			=> MoveTo.position;

		public BoxCollider EncapsulationBox {
			get => encapsulationBox;
			set => encapsulationBox = value;
		}

		/// <summary>
		/// Volume enclosing every collider of the hand, once it has been sized: the player depenetrates against
		/// it, so a finger entering a panel is corrected too and no longer only the palm.
		/// </summary>
		private BoxCollider EncapsulationVolume
			=> encapsulationBox && encapsulationBox.size.sqrMagnitude > 0f ? encapsulationBox : null;

		public override Collider EnvironmentCollider
			=> GetPhysical() is { BodyCollider: { } palm }
				? palm
				: base.EnvironmentCollider;

		public override float CollisionRadius
			=> GetHandPhysics() is { } physics ? physics.EnvironmentRadius : base.CollisionRadius;

		public override Vector3 CollisionCenter
			=> GetPhysical() is { BodyCollider: { } palm }
				? palm.bounds.center
				: base.CollisionCenter;

		public override void GetBodyColliders(List<Collider> results) {
			var physical = GetPhysical();

			if (physical) {
				physical.GetBodyColliders(results);
				return;
			}

			base.GetBodyColliders(results);
		}

		private HandPhysics _handPhysics;
		private HandPhysical _physicalHand;

		private HandPhysics GetHandPhysics()
			=> _handPhysics
				? _handPhysics
				 : _handPhysics = gameObject.GetComponent<HandPhysics>();

		private HandPhysical GetPhysical()
			=> _physicalHand
				? _physicalHand
				: _physicalHand = gameObject.GetComponent<HandPhysical>();

		public bool EnableMovement {
			get => enableMovement;
			set => enableMovement = value;
		}

		public override void SetVelocity(Vector3 velocity) {
			_linearVelocity = velocity;

			if (Body && !Body.isKinematic)
				Body.linearVelocity = velocity;
		}

		public override void SetAngularVelocity(Vector3 angularVelocity) {
			_angularVelocity = angularVelocity;

			if (Body && !Body.isKinematic)
				Body.angularVelocity = angularVelocity;
		}

		public override bool CanFollow
			=> true;

		private bool IsBodyCollider(Collider collider) {
			if (!Player)
				return false;

			var own = Player.BodyColliders;
			for (var i = 0; i < own.Count; i++)
				if (own[i] == collider)
					return true;

			return false;
		}

		public override void FollowTurn() {
			if (!PartEnabled || !Body)
				return;

			if (PhysicsFollow || _physical)
				return;

			Body.position = transform.position;
			Body.rotation = transform.rotation;

			SnapToTarget();
		}

		public override void OnPlayerFixedUpdate() {
			if (!enableMovement || !Body || PhysicsFollow)
				return;

			var deltaTime = Time.fixedDeltaTime;
			if (deltaTime <= 0f)
				return;

			var position = transform.position;
			_linearVelocity = (position - _lastPosition) / deltaTime;

			var rotation = transform.rotation;
			var rotationDelta = rotation * Quaternion.Inverse(_lastRotation);
			rotationDelta.ToAngleAxis(out var angle, out var axis);
			if (angle > 180f)
				angle -= 360f;

			_angularVelocity = axis * (angle * Mathf.Deg2Rad / deltaTime);

			if (!Body.isKinematic) {
				Body.linearVelocity = _linearVelocity;
				Body.angularVelocity = _angularVelocity;
			}

			_lastPosition = position;
			_lastRotation = rotation;
		}

		public void SetHandLocation(Vector3 position) {
			if (PhysicsFollow) {
				FollowPoint.position = position;
				Body.position = position;
				Body.linearVelocity = Vector3.zero;
				Body.angularVelocity = Vector3.zero;
				return;
			}

			MoveTo.position = position;
			transform.position = position;
			SnapToTarget();
		}

		public void SnapToTarget() {
			if (!Body)
				return;

			var point = PhysicsFollow && Body ? FollowPoint : MoveTo;

			Body.position = point.position;
			Body.rotation = point.rotation;

			_linearVelocity = Vector3.zero;
			_angularVelocity = Vector3.zero;

			if (!Body.isKinematic) {
				Body.linearVelocity = Vector3.zero;
				Body.angularVelocity = Vector3.zero;
			}

			transform.SetPositionAndRotation(point.position, point.rotation);

			_lastPosition = transform.position;
			_lastRotation = transform.rotation;
		}

		protected override bool SupportsRig(EntityRig value)
			=> value == EntityRig.LeftHand || value == EntityRig.RightHand;
	}
}
