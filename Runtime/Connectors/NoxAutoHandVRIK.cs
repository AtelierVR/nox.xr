using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Autohand;
using Nox.Avatars.AutoHand;
using Nox.Avatars.Hand;
using Nox.CCK.Utils;
using RootMotion.FinalIK;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;
using NFinger = Nox.CCK.Avatars.Hand.Finger;
using NHand = Nox.CCK.Avatars.Hand.Hand;

namespace Nox.XR.Runtime.Connectors {
	/// <summary>
	/// Drives the avatar skeleton (VRIK) from the rig's AutoHand hands.
	/// <para>
	/// The <b>physical</b> hand is not the armature one: it is a <b>duplicate</b> of the avatar hand,
	/// created in the XR rig "Hands" folder with the same colliders, pokes and scale. It is the one
	/// <see cref="AutoHandPlayer"/> drives, grabs with and collides with; the armature only keeps its
	/// bones, which VRIK writes from the duplicate (position, rotation <i>and</i> fingers).
	/// </para>
	/// </summary>
	[DefaultExecutionOrder(12), RequireComponent(typeof(VRIK))]
	public class NoxAutoHandVRIK : MonoBehaviour {
		[Tooltip("Tracked VR controller (right): its RAW pose, not the follow transform of a "
		         + "replacement hand (that one carries the AutoHand prefab's rotation offset).")]
		public Transform rightTrackedController;
		[Tooltip("Tracked VR controller (left): see rightTrackedController.")]
		public Transform leftTrackedController;
		[Tooltip("IHand of the avatar's right hand (pivot, bones, fingers).")]
		public IHand rightSource;
		[Tooltip("IHand of the avatar's left hand (pivot, bones, fingers).")]
		public IHand leftSource;
		[Tooltip("XR rig 'Hands' folder: the physical hand duplicates are created there.")]
		public Transform physicalHandsRoot;

		/// <summary>Physical AutoHand duplicate, right side.</summary>
		public Hand rightPhysical { get; private set; }
		/// <summary>Physical AutoHand duplicate, left side.</summary>
		public Hand leftPhysical { get; private set; }

		/// <summary>IHand describing the right duplicate (its own bones); the one converted into <see cref="rightPhysical"/>.</summary>
		public IHand rightPhysicalSource { get; private set; }
		/// <summary>See <see cref="rightPhysicalSource"/>.</summary>
		public IHand leftPhysicalSource { get; private set; }

		private Transform _rightHandOffset;
		private Transform _leftHandOffset;
		private Transform _rightIkTarget;
		private Transform _leftIkTarget;

		private Transform[] _rightVisualJoints   = System.Array.Empty<Transform>();
		private Transform[] _rightPhysicalJoints = System.Array.Empty<Transform>();
		private Transform[] _leftVisualJoints    = System.Array.Empty<Transform>();
		private Transform[] _leftPhysicalJoints  = System.Array.Empty<Transform>();

		private bool _resetQueued;
		private bool _subscribed;
		private Animator _animator;

		public VRIK vrik { get; protected set; }

		protected virtual void Start() {
			vrik = GetComponent<VRIK>();
			_animator = GetComponentInChildren<Animator>();
			SetupIK();
			if (AutoHandPlayer.Instance != null)
				vrik.transform.position -= Vector3.up * AutoHandPlayer.Instance.heightOffset;
			StartCoroutine(RebindAnimatorDelay());
		}

		private IEnumerator RebindAnimatorDelay() {
			yield return new WaitForEndOfFrame();
			yield return new WaitForFixedUpdate();
			if (_animator)
				_animator.Rebind();
		}

		protected virtual void OnEnable() {
			if (AutoHandPlayer.Instance != null) {
				AutoHandPlayer.Instance.OnSnapTurn   += AutoPlayerResetIKEvent;
				AutoHandPlayer.Instance.OnSmoothTurn += AutoPlayerResetIKEvent;
				AutoHandPlayer.Instance.OnTeleported += AutoPlayerResetIKEvent;
			}
			_resetQueued = true;
		}

		protected virtual void OnDisable() {
			if (AutoHandPlayer.Instance != null) {
				AutoHandPlayer.Instance.OnSnapTurn   -= AutoPlayerResetIKEvent;
				AutoHandPlayer.Instance.OnSmoothTurn -= AutoPlayerResetIKEvent;
				AutoHandPlayer.Instance.OnTeleported -= AutoPlayerResetIKEvent;
			}
			UnsubscribeGrabs();
		}

		protected virtual void OnDestroy() {
			// We spawned the duplicates, we destroy them.
			if (rightPhysical != null) rightPhysical.gameObject.Destroy();
			if (leftPhysical  != null) leftPhysical.gameObject.Destroy();

			// The HandOffsets are children of the controllers.
			if (_rightHandOffset != null) _rightHandOffset.gameObject.Destroy();
			if (_leftHandOffset  != null) _leftHandOffset.gameObject.Destroy();
		}

		protected virtual void OnRightGrab(Hand hand, Grabbable grab)
			=> vrik.solver.rightArm.target = hand.handGrabPoint;

		protected virtual void OnRightRelease(Hand hand, Grabbable grab)
			=> vrik.solver.rightArm.target = _rightIkTarget != null ? _rightIkTarget : _rightHandOffset;

		protected virtual void OnLeftGrab(Hand hand, Grabbable grab)
			=> vrik.solver.leftArm.target = hand.handGrabPoint;

		protected virtual void OnLeftRelease(Hand hand, Grabbable grab)
			=> vrik.solver.leftArm.target = _leftIkTarget != null ? _leftIkTarget : _leftHandOffset;

		protected virtual void AutoPlayerResetIKEvent(AutoHandPlayer player)
			=> _resetQueued = true;

		private void Update() {
			// Must run before VRIK, which solves in LateUpdate.
			RefreshHeadTargetRotation();
			RefreshIkTargets();

			if (!_resetQueued) return;
			vrik.solver.Reset();
			_resetQueued = false;
		}

		/// <summary>
		/// Aligns the head target's rotation on the headset's.
		/// <para>
		/// VRIK does not look towards the target, it <i>assigns</i> its rotation:
		/// <c>IKSolverVRSpine.PreSolve</c> does <c>IKRotationHead = headTarget.rotation</c>, then
		/// <c>Bend()</c> rotates the head bone onto it. Any offset baked into the target therefore
		/// ends up in full in the render.
		/// </para>
		/// </summary>
		private void RefreshHeadTargetRotation() {
			// The avatar rig can be generated after us: read the target every frame instead of caching it.
			var target = vrik != null ? vrik.solver.spine.headTarget : null;
			if (target == null)
				return;

			var player = AutoHandPlayer.Instance;
			if (player == null || player.headCamera == null)
				return;

			// `Bend()` returns early when the weights are 0: with rotationWeight at 0 the head keeps its
			// animated rotation while its position still follows. An avatar can lower the weight through
			// `rig/ik/head/rotation_weight`.
			vrik.solver.spine.positionWeight = 1f;
			vrik.solver.spine.rotationWeight = 1f;

			target.rotation = player.headCamera.transform.rotation;
		}

		protected virtual void LateUpdate() {
			// After VRIK: the rig hand drives the armature down to the fingertips.
			MirrorFingers(_rightVisualJoints, _rightPhysicalJoints);
			MirrorFingers(_leftVisualJoints,  _leftPhysicalJoints);

			if (AutoHandPlayer.Instance == null) return;
			var pos = transform.position;
			pos.y = AutoHandPlayer.Instance.transform.position.y;
			transform.position = pos;
		}

		/// <summary>
		/// Sets each arm IK anchor from the <c>HandOffset</c> (avatar pivot carried by the controller),
		/// corrected by the rigid "target -> physical" offset
		/// (<c>physical - physical.follow</c>).
		/// <para>
		/// The physical duplicate is the only hand affected by physics (walls, held objects, the
		/// <see cref="HandFollow"/> spring): without the correction the visible hand went through the
		/// geometry the physical one stops at, while the IK anchor only carried the ideal controller pose.
		/// </para>
		/// </summary>
		private void RefreshIkTargets() {
			RefreshIkTarget(_rightIkTarget, _rightHandOffset, rightPhysical);
			RefreshIkTarget(_leftIkTarget,  _leftHandOffset,  leftPhysical);
		}

		private static void RefreshIkTarget(Transform ikTarget, Transform handOffset, Hand physical) {
			if (ikTarget == null) return;

			if (handOffset == null) {
				if (physical != null)
					ikTarget.SetPositionAndRotation(physical.transform.position, physical.transform.rotation);
				return;
			}

			var position = handOffset.position;
			var rotation = handOffset.rotation;

			var follow = physical != null ? physical.follow : null;
			if (follow != null) {
				var body = physical.transform;
				position += body.position - follow.position;
				rotation = body.rotation * Quaternion.Inverse(follow.rotation) * rotation;
			}

			ikTarget.SetPositionAndRotation(position, rotation);
		}

		/// <summary>Copies the duplicate's finger pose onto the armature bones.</summary>
		private static void MirrorFingers(Transform[] visualJoints, Transform[] physicalJoints) {
			if (visualJoints == null || physicalJoints == null || visualJoints.Length != physicalJoints.Length)
				return;

			for (var i = 0; i < visualJoints.Length; i++) {
				var visual   = visualJoints[i];
				var physical = physicalJoints[i];
				if (visual == null || physical == null) continue;
				visual.localRotation = physical.localRotation;
			}
		}

		protected virtual void SetupIK() {
			// HandOffset = avatar pivot (IHand PositionOffset/RotationOffset) carried by the CONTROLLER. Its
			// world pose is exactly the one the hand bone must have: both the physical hand's follow target
			// and the IK anchor orientation.
			_rightHandOffset = CreateHandOffset(rightTrackedController, rightSource);
			_leftHandOffset  = CreateHandOffset(leftTrackedController,  leftSource);

			rightPhysical = CreatePhysicalHand(rightSource, _rightHandOffset, "R", out var rgs);
			leftPhysical  = CreatePhysicalHand(leftSource,  _leftHandOffset,  "L", out var lgs);

			rightPhysicalSource = rgs;
			leftPhysicalSource  = lgs;

			// AutoHandPlayer must drive the avatar hands (the duplicates), the ones carrying the colliders,
			// pokes and fingers, not the replacement hands.
			if (AutoHandPlayer.Instance != null) {
				if (rightPhysical != null) AutoHandPlayer.Instance.handRight = rightPhysical;
				if (leftPhysical  != null) AutoHandPlayer.Instance.handLeft  = leftPhysical;
			}

			SubscribeGrabs();

			// Fingers: armature bones <- duplicate bones (same hierarchy, same indexes).
			BuildFingerJoints(rightSource, rightPhysical, out _rightVisualJoints, out _rightPhysicalJoints);
			BuildFingerJoints(leftSource,  leftPhysical,  out _leftVisualJoints,  out _leftPhysicalJoints);

			// The arm IK anchor is a separate object (child of the avatar, not of the hand).
			_rightIkTarget = CreateIkTarget();
			_leftIkTarget  = CreateIkTarget();

			// The head target is created by FinalIKRigGenerator and read every frame by
			// RefreshHeadTargetRotation, so it is not cached here.

			// Each arm is resolved independently: a side without controller or hand must not block the other.
			vrik.solver.rightArm.target = ResolveArmTarget("right", rightSource, ref _rightHandOffset, _rightIkTarget);
			vrik.solver.leftArm.target  = ResolveArmTarget("left",  leftSource,  ref _leftHandOffset,  _leftIkTarget);
		}

		private void SubscribeGrabs() {
			if (_subscribed) return;
			_subscribed = true;
			if (rightPhysical != null) {
				rightPhysical.OnGrabbed  += OnRightGrab;
				rightPhysical.OnReleased += OnRightRelease;
			}
			if (leftPhysical != null) {
				leftPhysical.OnGrabbed  += OnLeftGrab;
				leftPhysical.OnReleased += OnLeftRelease;
			}
		}

		private void UnsubscribeGrabs() {
			if (!_subscribed) return;
			_subscribed = false;
			if (rightPhysical != null) {
				rightPhysical.OnGrabbed  -= OnRightGrab;
				rightPhysical.OnReleased -= OnRightRelease;
			}
			if (leftPhysical != null) {
				leftPhysical.OnGrabbed  -= OnLeftGrab;
				leftPhysical.OnReleased -= OnLeftRelease;
			}
		}

		/// <summary>
		/// Creates the <b>real</b> AutoHand hand: a full duplicate of the avatar hand (bones, colliders,
		/// pokes, scale) placed in the rig's "Hands" folder, made dynamic and driven by
		/// <see cref="HandFollow"/> on the avatar pivot. It is invisible (the avatar hand is what gets
		/// rendered) and it is the one that collides, with the avatar hand's real geometry.
		/// <para>
		/// The avatar hand is described by an <see cref="IHand"/> living <b>next to</b> the armature, so
		/// duplicating the anchor (a bone) does not copy it: one is built for the duplicate
		/// (<see cref="CreateGhostHand"/>) and it is that one which gets converted, since
		/// <see cref="HandToAutoHand.Convert(IHand)"/> equips <c>IHand.Anchor</c> - a duplicate bone, never
		/// an armature one.
		/// </para>
		/// </summary>
		private Hand CreatePhysicalHand(IHand source, Transform follow, string side, out IHand ghostSource) {
			ghostSource = null;

			if (source == null || source.Anchor == null) {
				Logger.LogError(
					$"{nameof(NoxAutoHandVRIK)}: no {side} hand in the avatar, the {side} arm will stay in T-pose.",
					this
				);
				return null;
			}

			var anchor = source.Anchor;
			var root   = physicalHandsRoot != null ? physicalHandsRoot : transform;

			var worldPosition = anchor.position;
			var worldRotation = anchor.rotation;
			var worldScale    = anchor.lossyScale;

			// Duplicate of the hand anchor: copies the bone hierarchy together with whatever AutoHand the
			// avatar hand's conversion left on it (components, colliders, pokes). The source is set INACTIVE
			// for the copy: otherwise the copied AutoHand would wake up on the duplicate (HandBase.Awake adds
			// a HandStabilizer to the camera, creates the palm, the encapsulation box...) even though we are
			// about to strip it.
			var sourceWasActive = anchor.gameObject.activeSelf;
			anchor.gameObject.SetActive(false);
			var ghost = anchor.gameObject.Instantiate(root);
			anchor.gameObject.SetActive(sourceWasActive);
			ghost.name = $"Physical Hand ({side})";

			// Placed exactly on the avatar hand, in world scale: the "Hands" folder does not necessarily have
			// the same scale as the armature.
			var ghostTransform = ghost.transform;
			ghostTransform.SetPositionAndRotation(worldPosition, worldRotation);
			var rootScale = root.lossyScale;
			ghostTransform.localScale = new Vector3(
				rootScale.x != 0f ? worldScale.x / rootScale.x : worldScale.x,
				rootScale.y != 0f ? worldScale.y / rootScale.y : worldScale.y,
				rootScale.z != 0f ? worldScale.z / rootScale.z : worldScale.z
			);

			// The duplicate keeps only its bones and descriptors: everything inherited from the avatar hand
			// (components, colliders, rigidbody, animation, renderers) goes now, Convert() re-equips it.
			StripToBones(ghost);

			// The DUPLICATE's hand: same descriptors as the avatar's, references re-pointed on the duplicate
			// bones. Validated before converting it - Convert() throws on an invalid hand.
			ghostSource = CreateGhostHand(source, ghost, side);
			if (ghostSource != null && !Nox.CCK.Avatars.Hand.HandExtensions.IsValid(ghostSource, out var ghostError)) {
				Logger.LogError(
					$"{nameof(NoxAutoHandVRIK)}: the {side} hand could not be duplicated ({ghostError.Message}), "
					+ $"the {side} arm will stay in T-pose.",
					this
				);
				ghostSource = null;
			}

			// The duplicate must be ACTIVE during the conversion: Convert() restores the state it found
			// (SetActive(wasActive) at the end) and that SetActive(true) is what triggers the configured
			// Awakes (HandBase: rigidbody, colliders, encapsulation box...). Left inactive - it is born that
			// way to avoid waking the copied AutoHand - the physical hand would never be initialized.
			ghost.SetActive(true);

			var hand = ghostSource != null 
				? HandToAutoHand.Convert(ghostSource) 
				: null;
			var body = ghost.GetOrAddComponent<Rigidbody>();
			body.isKinematic = false;
			body.useGravity  = false;

			if (hand != null) {
				hand.enableMovement = true;  // HandFollow only moves the hand if it is allowed to.
				hand.follow         = follow; // avatar pivot, not the RobotHand prefab offset.
				// AlignEncapsulationBox could not run on the avatar's inactive hand, so the duplicate's
				// encapsulation box would not cover the fingers.
				HandToAutoHand.AlignEncapsulationBox(hand);
			} else {
				Logger.LogError(
					$"{nameof(NoxAutoHandVRIK)}: the duplicate of the {side} hand has no Autohand.Hand component.",
					this
				);
			}

			return hand;
		}

		/// <summary>Hand bones and descriptors: all that the duplicate keeps.</summary>
		private static readonly System.Type[] AllowedHandComponents = {
			typeof(Transform),
			typeof(IHand),
			typeof(IFinger)
		};

		private static bool IsAllowed(Component comp) {
			var type = comp.GetType();
			foreach (var allowed in AllowedHandComponents)
				if (allowed.IsAssignableFrom(type)) 
					return true;
			return false;
		}

		/// <summary>
		/// Keeps only the duplicate's bones and descriptors: everything the copy inherited from the avatar
		/// hand (components, colliders, rigidbody, animation, renderers) is removed, then
		/// <see cref="HandToAutoHand"/> re-equips the duplicate.
		/// <para>
		/// Two precautions, without which the strip partly fails:
		/// <list type="bullet">
		/// <item>removal is <b>immediate</b>. With a deferred <c>Destroy</c> the required component is
		/// still there when its dependants go: Unity refuses ("Can't remove Rigidbody because Hand
		/// depends on it") and leaves it in place. Worse, the doomed but still alive <c>Hand</c> is picked
		/// up by the following conversion (<c>GetOrAddComponent</c>), which then installs a hand actually
		/// destroyed at the end of the frame - hence the <c>MissingReferenceException</c> in
		/// AutoHandPlayer.UpdateTrackedObjects;</item>
		/// <item>the order follows <c>RequireComponent</c>: a component is only removed once nothing
		/// requires it anymore, otherwise Unity refuses too.</item>
		/// </list>
		/// </para>
		/// </summary>
		private static void StripToBones(GameObject handRoot) {
			var doomed = new List<Component>();
			foreach (var component in handRoot.GetComponentsInChildren<Component>(true))
				if (component != null && !IsAllowed(component))
					doomed.Add(component);

			// In passes: what cannot leave yet (still required by another component) is retried on the next
			// pass, once its requirer is gone.
			bool removed;
			do {
				removed = false;
				foreach (var component in doomed.ToArray()) {
					if (component == null) {
						doomed.Remove(component);
						continue;
					}
					if (IsStillRequired(doomed, component))
						continue;

					doomed.Remove(component);
					component.DestroyImmediate();
					removed = true;
				}
			} while (removed && doomed.Count > 0);

			// Should not happen: a RequireComponent cycle remains between components.
			if (doomed.Count > 0)
				Logger.LogWarning(
					$"{nameof(NoxAutoHandVRIK)}: {doomed.Count} component(s) could not be stripped from "
					+ $"'{handRoot.name}' (requirement cycle): {string.Join(", ", doomed.ConvertAll(c => c.GetType().Name))}.",
					handRoot
				);
		}

		/// <summary>
		/// Does one of the doomed components still require <paramref name="component"/> (<c>RequireComponent</c>
		/// is necessarily on the same GameObject)?
		/// </summary>
		private static bool IsStillRequired(List<Component> doomed, Component component) {
			foreach (var other in doomed) {
				if (other == null || other == component || other.gameObject != component.gameObject)
					continue;
				if (Requires(other.GetType(), component.GetType()))
					return true;
			}
			return false;
		}

		/// <summary>
		/// Does <paramref name="type"/> declare <c>RequireComponent</c> on <paramref name="required"/>?
		/// Attributes are inherited (<c>HandBase</c> declares them for <c>Hand</c>) and their fields are read
		/// without relying on their names (<c>m_Type0</c>...).
		/// </summary>
		private static bool Requires(System.Type type, System.Type required) {
			const BindingFlags fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
			foreach (var attribute in type.GetCustomAttributes(typeof(RequireComponent), true))
				foreach (var field in attribute.GetType().GetFields(fields))
					if (field.GetValue(attribute) is System.Type dependency && dependency.IsAssignableFrom(required))
						return true;
			return false;
		}

		/// <summary>
		/// Gives the duplicate <b>its own</b> hand: an <see cref="IHand"/> describing the copy - same type,
		/// pivot, palm and fingers as the avatar hand, but every reference re-pointed on the duplicate
		/// bones.
		/// <para>
		/// The avatar hand's descriptors (CCK <c>Hand</c>/<c>Finger</c> components) live in a separate
		/// folder outside the anchor, so duplicating the anchor does not copy them. Without this hand the
		/// duplicate only has bones, and <see cref="HandToAutoHand.Convert(IHand)"/> - which equips
		/// <c>IHand.Anchor</c> - would equip the avatar armature instead.
		/// </para>
		/// </summary>
		private static IHand CreateGhostHand(IHand source, GameObject ghost, string side) {
			var sourceAnchor = source.Anchor;
			var ghostAnchor  = ghost.transform;

			var hand = ghost.GetOrAddComponent<NHand>();
			hand.type                = source.Type;
			hand.anchor              = ghostAnchor;
			hand.palm                = ResolveGhost(sourceAnchor, source.Palm, ghostAnchor);
			hand.nearFar             = ResolveGhost(sourceAnchor, source.NearFar, ghostAnchor);
			hand.pivotPositionOffset = source.PositionOffset;
			hand.pivotRotationOffset = source.RotationOffset;

			var fingers = new List<NFinger>();
			foreach (var finger in source.Fingers) {
				var copy = CreateGhostFinger(sourceAnchor, finger, ghostAnchor);
				if (copy != null) {
					fingers.Add(copy);
					continue;
				}

				Logger.LogWarning(
					$"{nameof(NoxAutoHandVRIK)}: the {side} {finger?.Type} finger has no bone under the hand anchor, "
					+ "it will be missing from the physical hand.",
					ghost
				);
			}
			hand.fingers = fingers.ToArray();

			return hand;
		}

		/// <summary>
		/// Copies one finger of the avatar hand onto the duplicate: the descriptor is added to the copy of
		/// its proximal bone - as the reverse conversion (<see cref="AutoHandToHand.Convert(Autohand.Hand)"/>
		/// ) does - and its joints point at the copies.
		/// </summary>
		private static NFinger CreateGhostFinger(Transform sourceAnchor, IFinger source, Transform ghostAnchor) {
			if (source == null)
				return null;

			var proximal = ResolveGhost(sourceAnchor, source.Proximal, ghostAnchor);
			if (proximal == null)
				return null;

			var finger = proximal.gameObject.GetOrAddComponent<NFinger>();
			finger.type         = source.Type;
			finger.proximal     = proximal;
			finger.intermediate = ResolveGhost(sourceAnchor, source.Intermediate, ghostAnchor);
			finger.distal       = ResolveGhost(sourceAnchor, source.Distal,       ghostAnchor);
			finger.tip          = ResolveGhost(sourceAnchor, source.Tip,          ghostAnchor);
			finger.tipRadius    = source.TipRadius;

			// Poses are the bones' local rotations: the duplicate bones are copies of the avatar ones (same
			// local frames), so they replay as is.
			var poses = source.Poses;
			if (poses == null)
				return finger;

			finger.poses = new FingerPose[ poses.Length ];
			for (var i = 0; i < poses.Length; i++)
				finger.poses[i] = new FingerPose {
					curl   = poses[i].Curl,
					values = poses[i].Values != null ? (Quaternion[])poses[i].Values.Clone() : null
				};

			return finger;
		}

		/// <summary>
		/// Finds the duplicate's copy of an avatar hand reference: the duplicate being a copy of the hand
		/// anchor, the copy is found by its path relative to the anchor. Returns <c>null</c> when the
		/// reference lies outside the duplicated hand (or is missing).
		/// </summary>
		private static Transform ResolveGhost(Transform anchor, Transform reference, Transform ghostAnchor) {
			if (reference == null || anchor == null)
				return null;
			if (reference == anchor)
				return ghostAnchor;

			var path = new List<string>();
			var current = reference;
			while (current != null && current != anchor) {
				path.Add(current.name);
				current = current.parent;
			}

			// Walk-up that never reached the anchor: the reference was not duplicated.
			if (current != anchor)
				return null;

			path.Reverse();
			return ghostAnchor.Find(string.Join("/", path));
		}


		/// <summary>
		/// Pairs the armature's finger bones with the duplicate's (same order: the duplicate is a copy of
		/// the armature).
		/// </summary>
		private static void BuildFingerJoints(
			IHand             source,
			Hand              physical,
			out Transform[]   visualJoints,
			out Transform[]   physicalJoints
		) {
			var visual   = new List<Transform>();
			var mirrored = new List<Transform>();

			var sourceFingers   = source != null ? source.Fingers : null;
			var physicalFingers = physical != null ? physical.fingers : null;

			if (sourceFingers != null && physicalFingers != null) {
				var count = Mathf.Min(sourceFingers.Length, physicalFingers.Length);
				for (var i = 0; i < count; i++) {
					var finger         = sourceFingers[i];
					var physicalFinger = physicalFingers[i];
					if (finger == null || physicalFinger == null) continue;

					Add(finger.Proximal,     physicalFinger.knuckleJoint);
					Add(finger.Intermediate, physicalFinger.middleJoint);
					Add(finger.Distal,       physicalFinger.distalJoint);
					Add(finger.Tip,          physicalFinger.tip);
				}
			}

			visualJoints   = visual.ToArray();
			physicalJoints = mirrored.ToArray();
			return;

			void Add(Transform visualJoint, Transform physicalJoint) {
				if (visualJoint == null || physicalJoint == null) return;
				visual.Add(visualJoint);
				mirrored.Add(physicalJoint);
			}
		}

		/// <summary>
		/// Resolves an arm's IK target. Without a tracked controller it falls back to the hand pivot so the
		/// IK still runs (and reports the cause instead of leaving a T-pose).
		/// </summary>
		private Transform ResolveArmTarget(string side, IHand hand, ref Transform handOffset, Transform ikTarget) {
			if (handOffset == null) {
				Logger.LogWarning(
					$"{nameof(NoxAutoHandVRIK)}: no {side} tracked controller, {side} arm IK target falls back to the hand pivot"
					+ $" - the {side} arm will not follow the controller.",
					this
				);
				handOffset = hand != null ? hand.Palm : null;
			}

			if (ikTarget == null)
				Logger.LogError(
					$"{nameof(NoxAutoHandVRIK)}: no IK anchor for the {side} arm, VRIK will leave it in T-pose.",
					this
				);

			return ikTarget != null ? ikTarget : handOffset;
		}

		/// <summary>Arm IK anchor: a separate object, replaced every frame by <see cref="RefreshIkTargets"/>.</summary>
		private Transform CreateIkTarget() {
			var go = new GameObject("IK Target");
			go.transform.SetParent(transform, false);
			return go.transform;
		}

		private static Transform CreateHandOffset(Transform parent, IHand source) {
			if (parent == null) return null;
			var go = new GameObject("HandOffset");
			go.transform.SetParent(parent, false);
			go.AddComponent<HandOffsetSetup>().Setup(source);
			return go.transform;
		}
	}
}
