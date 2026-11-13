using Nox.Avatars.Hand;
using Nox.CCK.Entities;
using Nox.CCK.Utils;
using UnityEngine;
using UnityEngine.Events;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Player {
	public enum HandSide {
		Left,
		Right
	}

	[AddComponentMenu("Nox/XR/Player/Hand Part")]
	[RequireComponent(typeof(Rigidbody))]
	public class Hand : Part {
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
		[SerializeField, Min(0f), Tooltip("Spring gain pulling the physical hand toward its follow point.")]
		private float physicalStrength = 60f;

		[SerializeField, Min(0f), Tooltip("Maximum linear acceleration of the physical hand, in m/s per fixed step.")]
		private float physicalAcceleration = 20f;
		[SerializeField, Min(0f), Tooltip("Maximum speed of the physical hand when the held object does not impose one.")]
		private float physicalMaxVelocity = 12f;

		[SerializeField, Min(0f), Tooltip("Spring gain driving the rotation of the physical hand.")]
		private float physicalRotationStrength = 100f;

		[SerializeField, Min(0f), Tooltip("Maximum angular acceleration of the physical hand, in rad/s per fixed step.")]
		private float physicalAngularAcceleration = 60f;

		[SerializeField, Min(0.01f), Tooltip("Lightest the physical hand can be, in kg.")]
		private float physicalMinMass = 0.25f;

		[SerializeField, Min(0.01f), Tooltip("Heaviest the physical hand can be, in kg: it takes the mass of the held object up to this cap.")]
		private float physicalMaxMass = 10f;

		private Transform _followPoint;
		private bool _physical;
		private float _physicalSpeed;
		private float _startMass;

		private readonly RaycastHit[] _clipHits = new RaycastHit[16];

		protected override EntityRig DefaultRig
			=> side == HandSide.Left
				? EntityRig.LeftHand
				: EntityRig.RightHand;

		public HandSide Side {
			get => side;
			set => side = value;
		}

		public Rigidbody Body
			=> body ? body : body = GetComponent<Rigidbody>();

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

			if (hand.body)
				hand.body.isKinematic = true;

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

		private void OnDestroy() {
			if (_followPoint)
				_followPoint.gameObject.Destroy();
		}

		public override Transform RigTarget
			=> _target ? _target : MoveTo;

		public bool Physical
			=> _physical;

		public Transform FollowPoint {
			get {
				if (_followPoint)
					return _followPoint;

				var point = new GameObject($"{name} Follow Point");
				_followPoint = point.transform;
				_followPoint.SetParent(transform.parent, false);
				_followPoint.SetPositionAndRotation(transform.position, transform.rotation);
				return _followPoint;
			}
		}

		public float PhysicalStretch
			=> _physical && _followPoint && Body ? Vector3.Distance(Body.position, _followPoint.position) : 0f;

		public void BeginPhysical(float maxSpeed, float heldMass = 0f) {
			if (_physical || !Body)
				return;

			_physical = true;
			_physicalSpeed = maxSpeed > 0f ? maxSpeed : physicalMaxVelocity;
			_startMass = Body.mass;

			FollowPoint.SetPositionAndRotation(transform.position, transform.rotation);

			Body.isKinematic = false;
			Body.useGravity = false;
			Body.interpolation = RigidbodyInterpolation.Interpolate;
			Body.constraints = RigidbodyConstraints.None;
			Body.solverIterations = 100;
			Body.solverVelocityIterations = 100;
			Body.mass = heldMass > 0f ? Mathf.Clamp(heldMass, physicalMinMass, physicalMaxMass) : physicalMinMass;
			Body.linearDamping = 0f;
			Body.angularDamping = 0f;
			Body.linearVelocity = Vector3.zero;
			Body.angularVelocity = Vector3.zero;
			Body.WakeUp();
		}

		public void EndPhysical() {
			if (!_physical)
				return;

			_physical = false;

			if (_followPoint)
				transform.SetPositionAndRotation(_followPoint.position, _followPoint.rotation);

			if (Body) {
				Body.linearVelocity = Vector3.zero;
				Body.angularVelocity = Vector3.zero;
				Body.isKinematic = true;
				Body.mass = _startMass > 0f ? _startMass : Body.mass;
			}

			SnapToTarget();
		}

		public override void SetPosition(Vector3 position) {
			if (!_physical) {
				base.SetPosition(position);
				return;
			}

			SetRawPosition(position);
			FollowPoint.position = position;
		}

		public override void SetRotation(Quaternion rotation) {
			if (!_physical) {
				base.SetRotation(rotation);
				return;
			}

			FollowPoint.rotation = rotation;
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

		public override Collider EnvironmentCollider
			=> GetHandPhysics() is { } physics ? physics.BodyCollider : base.EnvironmentCollider;

		public override float CollisionRadius
			=> GetHandPhysics() is { } physics ? physics.EnvironmentRadius : base.CollisionRadius;

		public override Vector3 CollisionCenter
			=> GetHandPhysics() is { BodyCollider: { } body }
				? body.bounds.center
				: base.CollisionCenter;

		private HandPhysics _handPhysics;

		private HandPhysics GetHandPhysics()
			=> _handPhysics ? _handPhysics : _handPhysics = gameObject.GetComponent<HandPhysics>();

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

		public override void FollowTrackingSpace(Vector3 previousPosition) {
			if (!PartEnabled || _physical)
				return;

			var box = EncapsulationBox;
			if (!box)
				return;

			var delta = transform.position - previousPosition;
			if (delta.magnitude < Physics.defaultContactOffset)
				return;

			var center = box.transform.TransformPoint(box.center) - delta;
			var halfExtents = box.transform.TransformVector(box.size) / 2f;
			var mask = Player ? Player.handPlayerMask : ~0;
			var count = Physics.BoxCastNonAlloc(
				center, halfExtents, delta, _clipHits,
				box.transform.rotation, delta.magnitude * 1.5f, mask
			);

			for (var i = 0; i < count; i++) {
				var hit = _clipHits[i];
				if (hit.collider.isTrigger)
					continue;

				var toHit = hit.point - transform.position;
				transform.position = Vector3.MoveTowards(transform.position, previousPosition, toHit.magnitude);
				break;
			}
		}

		public override void FollowTurn() {
			if (!PartEnabled || !Body)
				return;

			if (_physical)
				return;

			Body.position = transform.position;
			Body.rotation = transform.rotation;

			SnapToTarget();
		}

		public override void OnPlayerFixedUpdate() {
			if (!enableMovement || !Body)
				return;

			if (_physical) {
				DrivePhysical();
				return;
			}

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
			if (_physical) {
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

			var point = _physical && _followPoint ? _followPoint : MoveTo;

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

		private void DrivePhysical() {
			var deltaTime = Time.fixedDeltaTime;
			if (deltaTime <= 0f || !_followPoint)
				return;

			var delta = _followPoint.position - Body.position;
			var desired = Vector3.ClampMagnitude(delta * physicalStrength, _physicalSpeed);

			if (desired.sqrMagnitude > 0.0001f)
				Body.WakeUp();

			Body.linearVelocity = Vector3.MoveTowards(Body.linearVelocity, desired, physicalAcceleration * deltaTime);

			var rotation = _followPoint.rotation * Quaternion.Inverse(Body.rotation);
			rotation.ToAngleAxis(out var angle, out var axis);

			if (float.IsInfinity(axis.x) || angle <= 0f) {
				Body.angularVelocity = Vector3.MoveTowards(Body.angularVelocity, Vector3.zero, physicalAngularAcceleration * deltaTime);
				return;
			}

			if (angle > 180f)
				angle -= 360f;

			var desiredAngular = axis.normalized * (angle * Mathf.Deg2Rad * physicalRotationStrength);
			Body.angularVelocity = Vector3.MoveTowards(Body.angularVelocity, desiredAngular, physicalAngularAcceleration * deltaTime);
		}

		protected override bool SupportsRig(EntityRig value)
			=> value == EntityRig.LeftHand || value == EntityRig.RightHand;
	}
}
