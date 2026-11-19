using System;
using Nox.CCK.Entities;
using Nox.CCK.Utils;
using Nox.XR.Runtime.Tracking;
using Nox.XR.Trackers;
using UnityEngine;

namespace Nox.XR.Player {
	/// <summary>
	/// Feeds a <see cref="Part"/> from the loader-owned tracker providers.
	/// <para>
	/// The part only says which <see cref="EntityRig"/> it occupies; the tracker layer resolves the
	/// matching pose, velocity and angular velocity from the active loader/bindings/calibration.
	/// When the source disappears the part is disabled, then re-enabled when it comes back.
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Player/XR Follow")]
	[DefaultExecutionOrder(1000)]
	public class XRFollow : MonoBehaviour {
		[SerializeField, Tooltip("Part driven by this follower. Found on this GameObject when empty.")]
		private Part part;

		[SerializeField, Tooltip("Follow the node. Disable to freeze this part without disabling it.")]
		private bool follow = true;

		[SerializeField, Tooltip("Free tracker serial when the part has no EntityRig. Empty uses FreeTrackerIndex.")]
		private string freeTrackerId;

		[SerializeField, Tooltip("Index among free (unclaimed) trackers when the part has no EntityRig.")]
		private int freeTrackerIndex;

		[SerializeField, Tooltip("Disable the part while the tracked source is missing (untracked/disconnected).")]
		private bool disableWhenMissing = true;

		[SerializeField, Tooltip("Keep the rigidbody and the colliders of the part enabled, even while the part is disabled (source missing). The body is then simply held in place.")]
		private bool keepPhysicsActive = true;

		[SerializeField, Tooltip("Apply the tracked pose in Update as well, so the avatar rig (solved in LateUpdate) reads the pose of the current frame. LateUpdate re-applies it after the player moved its tracking space, so the view is still written last.")]
		private bool applyInUpdate = true;

		[SerializeField, Tooltip("Apply the pose once more right before the view camera renders (head only), the last chance to write the view before it is drawn.")]
		private bool applyBeforeRender = true;

		/// <summary>
		/// Settings of the physics follower, the equivalents of AutoHand's <c>HandFollow</c>: the body is pulled
		/// towards its follow point by a velocity servo and a drag that grows as it gets there.
		/// </summary>
		[Serializable]
		public struct Physics {
			[Min(0f), Tooltip("Spring gain pulling the body towards its follow point.")]
			public float positionStrength;

			[Min(0f), Tooltip("Smallest velocity change per fixed step, in m/s: it grows with the distance to the follow point.")]
			public float minVelocityChange;

			[Min(0f), Tooltip("How much the velocity change grows with the distance to the follow point.")]
			public float minVelocityDistance;

			[Min(0f), Tooltip("Maximum velocity of the body, in m/s.")]
			public float maxVelocity;

			[Min(0.01f), Tooltip("Speed the spring may ask for on top of the tracked one, in m/s: what a part presses with " +
								 "when it is held by the world. Above the thickness of a collider per step the contact cannot hold it.")]
			public float maxPushVelocity;

			[Min(0f), Tooltip("Drag of the body away from its follow point.")]
			public float drag;

			[Min(1f), Tooltip("Drag multiplier applied on the follow point: it is what stops the body from bouncing.")]
			public float dragDamper;

			[Min(0.001f), Tooltip("Distance under which the drag damper fades in, in metres.")]
			public float dragDamperDistance;

			[Min(0f), Tooltip("Spring gain driving the rotation of the body.")]
			public float rotationStrength;

			[Min(0f), Tooltip("Angular drag of the body away from its follow rotation.")]
			public float angularDrag;

			[Min(1f), Tooltip("Angular drag multiplier applied on the follow rotation.")]
			public float angularDragDamper;

			[Min(0.001f), Tooltip("Angle under which the angular drag damper fades in, in degrees.")]
			public float angularDragDamperAngle;

			[Min(0f), Tooltip("Drag added while a fingertip presses something: it holds the small movements of the hand " +
							   "without stopping a slide.")]
			public float gripDrag;

			[Min(0.001f), Tooltip("Speed above which the grip drag fades out, in m/s: a hand that slides keeps its own drag.")]
			public float gripReleaseSpeed;

			[Min(0.001f), Tooltip("Time the grip drag takes to reach its full value, in seconds.")]
			public float gripBlendTime;

			[Min(0f), Tooltip("Shortest distance the smoothed follow point covers towards the tracked pose in one step, " +
							  "in metres (AutoHand maxMoveToDistance).")]
			public float maxMoveToDistance;

			[Min(0f), Tooltip("Smallest angle the smoothed follow point covers towards the tracked rotation in one step, " +
							  "in degrees (AutoHand maxMoveToAngle).")]
			public float maxMoveToAngle;

			[Min(0.001f), Tooltip("Lightest the body gets when it sits on its follow point, in kg (AutoHand minMass).")]
			public float minMass;

			[Min(0.001f), Tooltip("Heaviest the body gets when it lags behind its follow point, in kg (AutoHand maxMass).")]
			public float maxMass;

			[Min(0.001f), Tooltip("Distance at which the distance part of the mass reaches its maximum, in metres " +
								  "(AutoHand distanceMassMaxDistance).")]
			public float distanceMassMaxDistance;

			[Min(0f), Tooltip("Share of the mass driven by the distance to the follow point (AutoHand distanceMassDifference).")]
			public float distanceMassDifference;

			[Min(0.001f), Tooltip("Angle at which the angle part of the mass reaches its maximum, in degrees " +
								  "(AutoHand angleMassMaxAngle).")]
			public float angleMassMaxAngle;

			[Min(0f), Tooltip("Share of the mass driven by the angle to the follow rotation (AutoHand angleMassDifference).")]
			public float angleMassDifference;

			[Min(0f), Tooltip("Distance the body may lag behind its follow point before it is pulled back onto it, " +
							  "in metres (AutoHand maxFollowDistance).")]
			public float maxFollowDistance;

			/// <summary>Values of the AutoHand hand, at its reference fixed step.</summary>
			public static Physics Default
				=> new() {
					positionStrength = 60f,
					minVelocityChange = 1f,
					minVelocityDistance = 5f,
					maxVelocity = 12f,
					maxPushVelocity = 2f,
					drag = 20f,
					dragDamper = 3f,
					dragDamperDistance = 0.025f,
					rotationStrength = 100f,
					angularDrag = 20f,
					angularDragDamper = 5f,
					angularDragDamperAngle = 3f,
					gripDrag = 40f,
					gripReleaseSpeed = 0.35f,
					gripBlendTime = 0.06f,
					maxMoveToDistance = 0.1f,
					maxMoveToAngle = 45f,
					minMass = 0.25f,
					maxMass = 10f,
					distanceMassMaxDistance = 0.5f,
					distanceMassDifference = 10f,
					angleMassMaxAngle = 45f,
					angleMassDifference = 10f,
					maxFollowDistance = 0.5f
				};

			/// <summary>
			/// Replaces the values a prefab authored before they existed left at zero: Unity writes the default value of
			/// the type on an existing asset, it never runs the C# initializer. Without this a stale prefab would divide
			/// by a null distance and give the body a mass of zero.
			/// </summary>
			public Physics Sanitized() {
				var fallback = Default;

				if (maxMoveToDistance <= 0f) maxMoveToDistance = fallback.maxMoveToDistance;
				if (maxMoveToAngle <= 0f) maxMoveToAngle = fallback.maxMoveToAngle;
				if (maxPushVelocity <= 0f) maxPushVelocity = fallback.maxPushVelocity;
				if (minMass <= 0f) minMass = fallback.minMass;
				if (maxMass <= 0f) maxMass = fallback.maxMass;
				if (distanceMassMaxDistance <= 0f) distanceMassMaxDistance = fallback.distanceMassMaxDistance;
				if (angleMassMaxAngle <= 0f) angleMassMaxAngle = fallback.angleMassMaxAngle;
				if (maxFollowDistance <= 0f) maxFollowDistance = fallback.maxFollowDistance;

				return this;
			}
		}

		[SerializeField] private Physics physics = Physics.Default;

		private const float REFERENCE_STEP = 0.011111f;

		private bool _disabledByMissing;
		private Rigidbody _body;
		private Collider[] _colliders;
		private float _grip;
		private bool _seated;

		// Tracked pose the servo aims at, in world space: the body of a part does not travel with the tracking space
		// (see KinematicLocalFollower), so everything the servo compares is in the same space.
		private Vector3 _targetPosition;
		private Quaternion _targetRotation = Quaternion.identity;

		// Smoothed follow point (AutoHand "move to"), in world space too.
		private Vector3 _moveToPosition;
		private Quaternion _moveToRotation = Quaternion.identity;
		public Part Part {
			get => part;
			set => part = value;
		}

		public bool Follow {
			get => follow;
			set => follow = value;
		}

		public string FreeTrackerId {
			get => freeTrackerId;
			set => freeTrackerId = value;
		}

		public int FreeTrackerIndex {
			get => freeTrackerIndex;
			set => freeTrackerIndex = value;
		}

		public bool DisableWhenMissing {
			get => disableWhenMissing;
			set => disableWhenMissing = value;
		}

		/// <summary>Whether the rigidbody and colliders are kept enabled even when the part is disabled.</summary>
		public bool KeepPhysicsActive {
			get => keepPhysicsActive;
			set => keepPhysicsActive = value;
		}

		/// <summary>Whether the pose is applied in <c>Update</c> as well as in <c>LateUpdate</c>.</summary>
		public bool ApplyInUpdate {
			get => applyInUpdate;
			set => applyInUpdate = value;
		}

		/// <summary>Whether the pose is applied again right before the view camera renders.</summary>
		public bool ApplyBeforeRender {
			get => applyBeforeRender;
			set {
				if (applyBeforeRender == value)
					return;

				applyBeforeRender = value;

				if (!isActiveAndEnabled)
					return;

				SubscribeRender(false);
				SubscribeRender(true);
			}
		}

		private Vector3 BodyPosition
			=> _body.position;

		private Quaternion BodyRotation
			=> _body.rotation;

		private void Reset() => part = GetComponent<Part>();

		private void OnValidate() {
			physics = physics.Sanitized();

			if (!part)
				part = GetComponent<Part>();
		}

		private void Awake() {
			if (!part)
				part = GetComponent<Part>();

			physics = physics.Sanitized();

			_body = part
				? part.Body
				: null;

			part.PhysicsFollow = _body != null;

			if (!_body)
				return;

			_colliders = part.GetComponentsInChildren<Collider>(true);

			// The tracking space carries the body, but it must not be simulated by it: KinematicLocalFollower places the
			// body on the moved local pose before the servo runs (execution order below this one), and writes what the
			// physics did back on the transform, so what the body touches can still stop it.
			_body.gameObject.GetOrAddComponent<KinematicLocalFollower>();

			// The body is carried with its parent every frame: an interpolated body would be redrawn between
			// two physics poses and show a gap with the tracker when the parent rotates.
			_body.interpolation = RigidbodyInterpolation.None;
			_body.useGravity = false;
			_body.isKinematic = false;
			_body.constraints = RigidbodyConstraints.None;
			_body.solverIterations = 100;
			_body.solverVelocityIterations = 100;

			// A body driven by the follower is simulated every step: one that falls asleep is not, while the tracking
			// space keeps carrying it, so it walks through the world with no contact to stop it.
			_body.sleepThreshold = 0f;
		}

		/// <summary>
		/// Pulls the body towards the follow point the way AutoHand's <c>HandFollow</c> does: a velocity servo
		/// plus a drag that grows as the body reaches its target, so it settles instead of bouncing around what it
		/// touches. The contacts are then solved by the physics, which is what keeps the part out of the world.
		/// <para>
		/// The tracked pose is resolved again here, at the physics step: driving from the pose written in
		/// <c>Update</c> would add a whole frame of latency to a hand.
		/// </para>
		/// </summary>
		private void FixedUpdate() {
			if (_body)
				EnsurePhysicsActive();

			if (!follow || !part || !part.PartEnabled || !part.PhysicsFollow || !_body)
				return;

			var deltaTime = Time.fixedDeltaTime;
			if (deltaTime <= 0f)
				return;

			var posed = ApplyNow();

			if (!_seated) {
				if (!posed)
					return;

				_seated = true;

				if (Vector3.Distance(BodyPosition, _targetPosition) > 0.1f) {
					Seat();
					return;
				}
			}

			SetTargetMotion(deltaTime);
			SetMoveTo();
			CheckMaxDistance();
			SetMass();
			Drive(deltaTime);
		}

		/// <summary>Keeps the rigidbody simulated and the colliders enabled, whatever disabled the part.</summary>
		private void EnsurePhysicsActive() {
			if (!keepPhysicsActive)
				return;

			if (_body.isKinematic)
				_body.isKinematic = false;

			if (_colliders == null)
				return;

			for (var i = 0; i < _colliders.Length; i++) {
				var collider = _colliders[i];

				if (collider && !collider.enabled)
					collider.enabled = true;
			}
		}

		private Vector3 _targetVelocity;
		private Vector3 _targetAngularVelocity;
		private Vector3 _lastTargetPosition;
		private Quaternion _lastTargetRotation = Quaternion.identity;
		private Matrix4x4 _lastRootMatrix = Matrix4x4.identity;
		private bool _hasTargetPose;

		private Transform Root
			=> transform.parent;

		/// <summary>
		/// Velocity of the tracked pose inside the tracking space. The servo only answers the error it sees, so a hand the
		/// player throws has to trail behind its tracker by exactly the error needed to reach the tracked speed. Feeding that
		/// speed to the body removes the lag, while the contacts keep their say: a body pushed into the world is stopped by
		/// the solver, whatever velocity it was given.
		/// <para>
		/// Measured relative to the tracking space: the body is already carried by its parent (see KinematicLocalFollower),
		/// so a move of the player is not a move of the tracker, and feeding it would move the body twice.
		/// </para>
		/// </summary>
		private void SetTargetMotion(float deltaTime) {
			var position = _targetPosition;
			var rotation = _targetRotation;
			var root = Root;

			if (!_hasTargetPose || deltaTime <= 0f) {
				RememberTarget(position, rotation, root);
				_targetVelocity        = Vector3.zero;
				_targetAngularVelocity = Vector3.zero;
				return;
			}

			var carried = root ? root.localToWorldMatrix * _lastRootMatrix.inverse : Matrix4x4.identity;

			_targetVelocity = (position - carried.MultiplyPoint3x4(_lastTargetPosition)) / deltaTime;

			(rotation * Quaternion.Inverse(carried.rotation * _lastTargetRotation)).ToAngleAxis(out var angle, out var axis);

			if (float.IsInfinity(axis.x) || Mathf.Approximately(angle, 0f)) {
				_targetAngularVelocity = Vector3.zero;
			} else {
				if (angle > 180f)
					angle -= 360f;

				_targetAngularVelocity = axis.normalized * (angle * Mathf.Deg2Rad / deltaTime);
			}

			RememberTarget(position, rotation, root);
		}

		private void RememberTarget(Vector3 position, Quaternion rotation, Transform root) {
			_lastTargetPosition = position;
			_lastTargetRotation = rotation;
			_lastRootMatrix     = root ? root.localToWorldMatrix : Matrix4x4.identity;
			_hasTargetPose      = true;
		}

		/// <summary>
		/// Moves the smoothed follow point towards the tracked pose, with AutoHand's square root ramp: the extra
		/// distance covered grows with the lag, so a hand that is thrown moves faster than one that is resting.
		/// </summary>
		private void SetMoveTo() {
			var position = BodyPosition;
			var rotation = BodyRotation;

			var distance = Vector3.Distance(position, _targetPosition);
			var angle = Quaternion.Angle(rotation, _targetRotation);

			_moveToPosition = Vector3.MoveTowards(position, _targetPosition, physics.maxMoveToDistance + Mathf.Sqrt(distance + 1f) - 1f);
			_moveToRotation = Quaternion.RotateTowards(rotation, _targetRotation, physics.maxMoveToAngle + Mathf.Sqrt(angle + 1f) - 1f);
		}

		/// <summary>
		/// Mass of the body, the way AutoHand's <c>SetMass</c> derives it: light when the body sits on its follow
		/// point, heavy when it lags behind or is twisted away from it, so a hand blocked by the world pushes with
		/// its own weight instead of teleporting through.
		/// </summary>
		private void SetMass() {
			var total = physics.distanceMassDifference + physics.angleMassDifference;
			if (total <= 0f)
				return;

			var distance = Mathf.Clamp01(Vector3.Distance(_moveToPosition, BodyPosition) / physics.distanceMassMaxDistance);
			var angle = Mathf.Clamp01(Quaternion.Angle(_moveToRotation, BodyRotation) / physics.angleMassMaxAngle);

			var distanceMass = Mathf.Lerp(physics.minMass, physics.maxMass, distance) * physics.distanceMassDifference / total;
			var angleMass = Mathf.Lerp(physics.minMass, physics.maxMass, angle) * physics.angleMassDifference / total;

			_body.mass = Mathf.Max(distanceMass + angleMass, 0.001f);
		}

		/// <summary>
		/// AutoHand's <c>CheckHandMaxDistance</c>: a body that lags more than <see cref="Physics.maxFollowDistance"/>
		/// behind its follow point is pulled back onto it.
		/// <para>
		/// A body the world is holding (a hand pressed on a wall while the player walks) is left where it is: pulling it
		/// onto its tracker would put it on the other side of what stops it.
		/// </para>
		/// </summary>
		private void CheckMaxDistance() {
			var distance = Vector3.Distance(BodyPosition, _targetPosition);

			if (distance > physics.maxFollowDistance && !Pressing())
				Seat();
		}

		/// <summary>
		/// Whether the servo is already asking for all it can give: a part that stays that far behind while pressing is not
		/// late, the world is holding it.
		/// </summary>
		private bool Pressing()
			=> Vector3.Distance(_moveToPosition, BodyPosition) * physics.positionStrength >= physics.maxPushVelocity;

		/// <summary>Puts the body exactly on its follow point, at rest.</summary>
		private void Seat() {
			var position = _targetPosition;
			var rotation = _targetRotation;

			_body.position = position;
			_body.rotation = rotation;
			_body.linearVelocity = Vector3.zero;
			_body.angularVelocity = Vector3.zero;

			transform.SetPositionAndRotation(position, rotation);
		}

		private void Drive(float deltaTime) {
			var frameScale = REFERENCE_STEP / deltaTime;

			var delta = _moveToPosition - BodyPosition;
			var distance = delta.magnitude;
			var speed = part.FollowSpeed > 0f ? part.FollowSpeed : physics.maxVelocity;

			var velocity = _body.linearVelocity;

			var damping = Mathf.Lerp(physics.drag * physics.dragDamper, physics.drag, distance / physics.dragDamperDistance) * frameScale
						  + Grip(deltaTime, velocity.magnitude) * frameScale;

			_body.linearDamping = damping;

			var desired = Vector3.ClampMagnitude(delta * physics.positionStrength, physics.maxPushVelocity);

			// the speed of the tracker is damped back up by as much as the drag of this step eats, so the body holds it
			// instead of reaching it only through the error the spring needs to pull with that speed
			var feed = Vector3.ClampMagnitude(_targetVelocity, speed) * (1f + damping * deltaTime);
			desired += feed;

			if (desired.sqrMagnitude > 0.0001f)
				_body.WakeUp();

			var change = physics.minVelocityChange * frameScale * (1f + distance * physics.minVelocityDistance);

			// the allowance grows with the tracker speed too: the body starts moving with the tracker in the same step,
			// the ramp only smooths what the spring asks for
			_body.linearVelocity = new Vector3(
				Mathf.MoveTowards(velocity.x, desired.x, change / 5f + Mathf.Abs(velocity.x) / 1.5f + Mathf.Abs(feed.x)),
				Mathf.MoveTowards(velocity.y, desired.y, change / 5f + Mathf.Abs(velocity.y) / 1.5f + Mathf.Abs(feed.y)),
				Mathf.MoveTowards(velocity.z, desired.z, change / 5f + Mathf.Abs(velocity.z) / 1.5f + Mathf.Abs(feed.z)));

			var rotation = _moveToRotation * Quaternion.Inverse(BodyRotation);
			rotation.ToAngleAxis(out var angle, out var axis);

			if (float.IsInfinity(axis.x) || Mathf.Approximately(angle, 0f)) {
				var idleDamping = physics.angularDrag * physics.angularDragDamper * frameScale;

				_body.angularDamping  = idleDamping;
				_body.angularVelocity = AngularFeed(idleDamping, deltaTime);
				return;
			}

			if (angle > 180f)
				angle -= 360f;

			var angularDamping = Mathf.Lerp(
				physics.angularDrag * physics.angularDragDamper, physics.angularDrag,
				Mathf.Abs(angle) / physics.angularDragDamperAngle) * frameScale;

			_body.angularDamping = angularDamping;
			_body.angularVelocity = axis.normalized * (angle * Mathf.Deg2Rad * physics.rotationStrength)
									+ AngularFeed(angularDamping, deltaTime);
		}

		/// <summary>
		/// Speed the tracked rotation turns at, damped back up like the linear one: it is what keeps a rolling hand on its
		/// tracker instead of trailing by the angle the spring needs to reach that speed.
		/// </summary>
		private Vector3 AngularFeed(float damping, float deltaTime)
			=> _targetAngularVelocity * (1f + damping * deltaTime);

		/// <summary>
		/// Extra drag of the grip, for a fingerprint pressing something.
		/// <para>
		/// It fades in over <see cref="Physics.gripBlendTime"/> and out with the speed of the body, so it holds the
		/// small movements of a finger resting on a surface and disappears as soon as the hand slides. A friction
		/// material cannot do that: it only bites on the contact, holds, slips, holds again, and the hand stutters.
		/// </para>
		/// </summary>
		private float Grip(float deltaTime, float velocity) {
			var gripping = part is Hand hand && hand.Gripping;

			_grip = Mathf.MoveTowards(_grip, gripping ? 1f : 0f, deltaTime / Mathf.Max(physics.gripBlendTime, 0.001f));

			if (_grip <= 0f)
				return 0f;

			var bite = 1f - Mathf.Clamp01(velocity / Mathf.Max(physics.gripReleaseSpeed, 0.001f));
			return _grip * physics.gripDrag * bite;
		}

		private void OnEnable() {
			_seated        = false;
			_hasTargetPose = false;
			SubscribeRender(true);
		}

		private void OnDisable()
			=> SubscribeRender(false);

		/// <summary>
		/// Subscribes the last chance to write the view: the pose applied right before the head camera
		/// renders, i.e. after every <c>LateUpdate</c> of the frame (see <c>nox.desktop</c>, which places its
		/// camera the same way). Only the head part carries the view camera, so the other parts skip it.
		/// </summary>
		private void SubscribeRender(bool subscribe) {
			if (subscribe) {
				if (applyBeforeRender && part is Head)
					RenderOrder.Subscribe(OnBeginCameraRendering, RenderPhase.Input);

				return;
			}

			RenderOrder.Unsubscribe(OnBeginCameraRendering);
		}

		private void OnBeginCameraRendering(Camera camera) {
			if (!applyBeforeRender || !follow || !part || !part.PartEnabled)
				return;

			if (part is not Head head || camera != head.Camera)
				return;

			ApplyNow();
		}

		/// <summary>
		/// Resolves the tracked pose and writes it now. Used by the late passes (end of <c>LateUpdate</c> and
		/// before the camera renders) so the view is written after everything else in the frame.
		/// </summary>
		/// <returns><c>false</c> when the part is frozen or the tracked source has no pose.</returns>
		public bool ApplyNow() {
			if (!isActiveAndEnabled || !follow || !part || !part.PartEnabled)
				return false;

			if (!TryGetPose(out var pose))
				return false;

			ApplyPose(pose);
			return true;
		}

		/// <summary>
		/// Applies the tracked pose <b>before</b> the late update, so everything that reads the part in
		/// LateUpdate (the avatar rig, which solves there) sees the pose of the current frame instead of
		/// the previous one.
		/// <para>
		/// The pose is applied again in <see cref="LateUpdate"/> (execution order 1000), after the player
		/// moved its tracking space (order 1) and after the avatar rig solved: the view - the camera
		/// carried by the head part - is the last transform written in the frame, so it never inherits
		/// the move of its parent and the gaze keeps no extra frame of latency.
		/// </para>
		/// </summary>
		private void Update() {
			if (!applyInUpdate)
				return;

			ApplyNow();
		}

		private void LateUpdate() {
			if (!follow || !part)
				return;

			// A part disabled by hand stays untouched; only a part disabled because its source went
			// away is re-enabled automatically when the source comes back.
			if (!part.PartEnabled && !_disabledByMissing)
				return;

			if (!TryGetPose(out var pose)) {
				if (disableWhenMissing && part.PartEnabled) {
					part.SetEnabled(false);
					_disabledByMissing = true;

					// SetEnabled(false) switches the body and the colliders off: put them back.
					if (_body && keepPhysicsActive)
						EnsurePhysicsActive();
				}

				return;
			}

			if (_disabledByMissing) {
				part.SetEnabled(true);
				_disabledByMissing = false;
			}

			ApplyPose(pose);
		}

		/// <summary>
		/// Writes a resolved pose on the part (the part decides what it drives).
		/// <para>
		/// The pose is already in world space (<see cref="TrackerResolver.TryGetPose(ushort, out TrackerPose)"/> applies
		/// the tracking space, the avatar fit and the calibration): transforming it again here would move a calibrated
		/// bone by the container rotation and stretch its offset with the fit.
		/// </para>
		/// </summary>
		private void ApplyPose(TrackerPose pose) {
			_targetPosition = pose.Position + part.HoldOffsetPosition;
			_targetRotation = pose.Rotation * part.HoldOffsetRotation;

			part.SetPose(pose.Position, pose.Rotation);
		}

		private bool TryGetPose(out TrackerPose pose) {
			if (part.Rig != EntityRig.None)
				return TrackerResolver.TryGetPose(part.Rig, out pose);

			return string.IsNullOrEmpty(freeTrackerId)
				? TrackerResolver.TryGetFreeTracker(freeTrackerIndex, out pose)
				: TrackerResolver.TryGetFreeTracker(freeTrackerId, out pose);
		}
	}
}
