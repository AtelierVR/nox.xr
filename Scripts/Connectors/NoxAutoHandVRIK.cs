using System.Collections;
using System.Collections.Generic;
using Autohand;
using Nox.Avatars.AutoHand;
using Nox.Avatars.Hand;
using Nox.CCK.Utils;
using RootMotion.FinalIK;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Connectors {
	/// <summary>
	/// Fait suivre le squelette de l'avatar (VRIK) par la main AutoHand du rig.
	/// <para>
	/// La main AutoHand « réelle » n'est <b>pas</b> celle de l'armature : c'est un
	/// <b>duplicata</b> de la main de l'avatar, créé dans le dossier « Hands » du rig XR — mêmes
	/// colliders, mêmes pokes, même échelle. C'est lui que <see cref="AutoHandPlayer"/> pilote, qui
	/// attrape et qui collisionne ; l'armature, elle, ne garde que ses os, que VRIK écrit en
	/// suivant le duplicata (position, rotation <i>et</i> doigts). La main du rig contrôle donc
	/// l'armature, jamais l'inverse.
	/// </para>
	/// </summary>
	[DefaultExecutionOrder(12), RequireComponent(typeof(VRIK))]
	public class NoxAutoHandVRIK : MonoBehaviour {
		[Tooltip("Transform du contrôleur VR suivi (droite) : sa POSE BRUTE, pas le « follow » "
		         + "d'une main de remplacement (celui-ci porte l'offset de rotation du prefab AutoHand).")]
		public Transform rightTrackedController;
		[Tooltip("Transform du contrôleur VR suivi (gauche) : voir rightTrackedController.")]
		public Transform leftTrackedController;
		[Tooltip("Données IHand de la main droite de l'avatar (pivot, os, doigts).")]
		public IHand rightHandSource;
		[Tooltip("Données IHand de la main gauche de l'avatar (pivot, os, doigts).")]
		public IHand leftHandSource;
		[Tooltip("Dossier « Hands » du rig XR : c'est là que sont créés les duplicatas physiques "
		         + "des mains de l'avatar (à côté des mains de remplacement AutoHand).")]
		public Transform physicalHandsRoot;

		/// <summary>Main AutoHand (duplicata) du côté droit : c'est le corps physique.</summary>
		public Hand rightPhysicalHand { get; private set; }
		/// <summary>Main AutoHand (duplicata) du côté gauche : c'est le corps physique.</summary>
		public Hand leftPhysicalHand { get; private set; }

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
			// Les duplicatas des mains ont été créés par nous : c'est à nous de les jeter.
			if (rightPhysicalHand != null) rightPhysicalHand.gameObject.Destroy();
			if (leftPhysicalHand  != null) leftPhysicalHand.gameObject.Destroy();

			// Les HandOffset sont enfants des contrôleurs.
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
			// Doit passer avant VRIK, qui résout en LateUpdate.
			RefreshIkTargets();

			if (!_resetQueued) return;
			vrik.solver.Reset();
			_resetQueued = false;
		}

		protected virtual void LateUpdate() {
			// Après VRIK : la main du rig pilote l'armature jusqu'au bout des doigts.
			MirrorFingers(_rightVisualJoints, _rightPhysicalJoints);
			MirrorFingers(_leftVisualJoints,  _leftPhysicalJoints);

			if (AutoHandPlayer.Instance == null) return;
			var pos = transform.position;
			pos.y = AutoHandPlayer.Instance.transform.position.y;
			transform.position = pos;
		}

		/// <summary>
		/// Positionne chaque ancre IK du bras sur le <c>HandOffset</c> (pivot de l'avatar, porté par le
		/// contrôleur), <b>corrigé de l'écart de la main physique à sa cible</b>.
		/// <para>
		/// La main physique (le duplicata, dynamique) est la seule à subir la physique : poussée par les
		/// murs, retenue par un objet, spring de <see cref="HandFollow"/>. Sans cette correction, l'ancre
		/// ne portait que la pose « idéale » du contrôleur et la main <b>visible</b> traversait la
		/// géométrie que la main physique, elle, ne traverse pas (c'est le seul défaut : le contact,
		/// lui, est bien calculé sur la main physique).
		/// </para>
		/// <para>
		/// L'écart est calculé comme le décalage rigide « cible → physique »
		/// (<c>physical − physical.follow</c>) et appliqué à la pose du HandOffset. Il est posé sur un
		/// objet à part (enfant de l'avatar, <b>jamais</b> de la main) : y toucher serait écrasé par VRIK.
		/// </para>
		/// </summary>
		private void RefreshIkTargets() {
			RefreshIkTarget(_rightIkTarget, _rightHandOffset, rightPhysicalHand);
			RefreshIkTarget(_leftIkTarget,  _leftHandOffset,  leftPhysicalHand);
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
				rotation = (body.rotation * Quaternion.Inverse(follow.rotation)) * rotation;
			}

			ikTarget.SetPositionAndRotation(position, rotation);
		}

		/// <summary>Recopie la pose des doigts du duplicata sur les os de l'armature.</summary>
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
			// HandOffset = pivot de l'avatar (PositionOffset/RotationOffset du IHand) porté par le
			// CONTRÔLEUR. Sa pose monde est exactement celle que doit avoir l'os de main : c'est
			// donc à la fois la cible de suivi de la main physique et l'orientation de l'ancre IK.
			_rightHandOffset = CreateHandOffset(rightTrackedController, rightHandSource);
			_leftHandOffset  = CreateHandOffset(leftTrackedController,  leftHandSource);

			rightPhysicalHand = CreatePhysicalHand(rightHandSource, _rightHandOffset, "R");
			leftPhysicalHand  = CreatePhysicalHand(leftHandSource,  _leftHandOffset,  "L");

			// AutoHandPlayer doit piloter les mains de l'avatar (les duplicatas), pas les mains de
			// remplacement : ce sont elles qui portent les colliders, les pokes et les doigts.
			if (AutoHandPlayer.Instance != null) {
				if (rightPhysicalHand != null) AutoHandPlayer.Instance.handRight = rightPhysicalHand;
				if (leftPhysicalHand  != null) AutoHandPlayer.Instance.handLeft  = leftPhysicalHand;
			}

			SubscribeGrabs();

			// Doigts : os de l'armature ← os du duplicata (même hiérarchie, mêmes index).
			BuildFingerJoints(rightHandSource, rightPhysicalHand, out _rightVisualJoints, out _rightPhysicalJoints);
			BuildFingerJoints(leftHandSource,  leftPhysicalHand,  out _leftVisualJoints,  out _leftPhysicalJoints);

			// L'ancre IK du bras est un objet à part (enfant de l'avatar, pas de la main).
			_rightIkTarget = CreateIkTarget();
			_leftIkTarget  = CreateIkTarget();

			// Chaque bras est résolu INDÉPENDAMMENT : un côté sans contrôleur (ou sans main) ne doit
			// pas empêcher l'autre d'être configuré.
			vrik.solver.rightArm.target = ResolveArmTarget("right", rightHandSource, ref _rightHandOffset, _rightIkTarget);
			vrik.solver.leftArm.target  = ResolveArmTarget("left",  leftHandSource,  ref _leftHandOffset,  _leftIkTarget);

			Logger.LogDebug(
				$"{nameof(NoxAutoHandVRIK)}: rig ready."
				+ $" right[cible={(rightTrackedController ? rightTrackedController.name : "NULL")}"
				+ $" pivot={(_rightHandOffset ? _rightHandOffset.name : "NULL")}"
				+ $" hand={(rightPhysicalHand ? rightPhysicalHand.name : "NULL")}]"
				+ $" left[cible={(leftTrackedController ? leftTrackedController.name : "NULL")}"
				+ $" pivot={(_leftHandOffset ? _leftHandOffset.name : "NULL")}"
				+ $" hand={(leftPhysicalHand ? leftPhysicalHand.name : "NULL")}]",
				this
			);
		}

		private void SubscribeGrabs() {
			if (_subscribed) return;
			_subscribed = true;
			if (rightPhysicalHand != null) {
				rightPhysicalHand.OnGrabbed  += OnRightGrab;
				rightPhysicalHand.OnReleased += OnRightRelease;
			}
			if (leftPhysicalHand != null) {
				leftPhysicalHand.OnGrabbed  += OnLeftGrab;
				leftPhysicalHand.OnReleased += OnLeftRelease;
			}
		}

		private void UnsubscribeGrabs() {
			if (!_subscribed) return;
			_subscribed = false;
			if (rightPhysicalHand != null) {
				rightPhysicalHand.OnGrabbed  -= OnRightGrab;
				rightPhysicalHand.OnReleased -= OnRightRelease;
			}
			if (leftPhysicalHand != null) {
				leftPhysicalHand.OnGrabbed  -= OnLeftGrab;
				leftPhysicalHand.OnReleased -= OnLeftRelease;
			}
		}

		/// <summary>
		/// Crée la <b>vraie</b> main AutoHand : un duplicata complet de la main de l'avatar (os,
		/// colliders, pokes, échelle) posé dans le dossier « Hands » du rig, rendu dynamique et
		/// piloté par <see cref="HandFollow"/> sur le pivot de l'avatar. Il est invisible (c'est la
		/// main de l'avatar qui est rendue) et c'est lui qui collisionne, avec la géométrie réelle
		/// de la main de l'avatar.
		/// <para>
		/// Après duplication, tout l'Autohand est <b>retiré de l'armature</b> : elle ne garde que
		/// ses os, que VRIK écrit en suivant ce duplicata.
		/// </para>
		/// </summary>
		private Hand CreatePhysicalHand(IHand source, Transform follow, string side) {
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

			// Duplicata complet de la main (os, colliders, pokes, composants AutoHand).
			var ghost = anchor.gameObject.Instantiate(root);
			ghost.name = $"Physical Hand ({side})";

			// Reposé exactement sur la main de l'avatar, à l'échelle monde : le dossier « Hands »
			// n'a pas forcément la même échelle que l'armature.
			var ghostTransform = ghost.transform;
			ghostTransform.SetPositionAndRotation(worldPosition, worldRotation);
			var rootScale = root.lossyScale;
			ghostTransform.localScale = new Vector3(
				rootScale.x != 0f ? worldScale.x / rootScale.x : worldScale.x,
				rootScale.y != 0f ? worldScale.y / rootScale.y : worldScale.y,
				rootScale.z != 0f ? worldScale.z / rootScale.z : worldScale.z
			);

			// Le duplicata est invisible : la main rendue est celle de l'avatar.
			foreach (var renderer in ghost.GetComponentsInChildren<Renderer>(true))
				renderer.enabled = false;

			var hand = ghost.GetComponent<Hand>();
			var body = ghost.GetOrAddComponent<Rigidbody>();
			body.isKinematic = false;
			body.useGravity  = false;

			if (hand != null) {
				hand.enableMovement = true;  // HandFollow n'avance la main que si elle peut bouger.
				hand.follow         = follow; // pivot de l'avatar, pas l'offset du prefab RobotHand.
				// AlignEncapsulationBox n'a pas pu se faire sur la main inactive de l'avatar : la
				// boîte d'encapsulation du duplicata ne couvrirait pas les doigts.
				HandToAutoHand.AlignEncapsulationBox(hand);
			} else {
				Logger.LogError(
					$"{nameof(NoxAutoHandVRIK)}: the {side} hand has no Autohand.Hand component to duplicate.",
					this
				);
			}

			// Le duplicata porte désormais tout l'Autohand : on dépouille l'armature (composants
			// AutoHand, pokes, colliders, Rigidbody). Elle ne garde que ses os.
			StripAutoHand(anchor.gameObject);

			return hand;
		}

		/// <summary>
		/// Retire d'un os de main de l'avatar tout ce que la conversion AutoHand y avait ajouté.
		/// <para>
		/// L'ordre compte : <c>HandBase</c> déclare
		/// <c>[RequireComponent(Rigidbody, HandFollow, HandAnimator, HandGrabbableHighlighter)]</c>,
		/// donc Unity recrée ces composants dès qu'on les détruit tant que <c>Hand</c> existe
		/// encore. On retire d'abord <c>Hand</c> (et ce qui le requiert), puis le reste.
		/// </para>
		/// </summary>
		private static void StripAutoHand(GameObject handRoot) {
			var behaviours     = handRoot.GetComponentsInChildren<MonoBehaviour>(true);
			var autoHandAssembly = typeof(Hand).Assembly;

			// 1. Les composants qui *requièrent* les autres.
			foreach (var behaviour in behaviours) {
				if (behaviour is Hand || behaviour is HandAdvancedOptions || behaviour is HandCollisionHaptics)
					behaviour.Destroy();
			}

			// 2. Le reste de l'Autohand, plus les pokes ajoutés par le connecteur de mains.
			foreach (var behaviour in behaviours) {
				if (behaviour == null) continue;
				var type = behaviour.GetType();
				var isAutoHandComponent = type.Assembly == autoHandAssembly;
				var isPokeComponent = type == typeof(HandPokeConnector)
				                      || type == typeof(FingerKeybindConnector)
				                      || type == typeof(PokeInteractor);
				if (isAutoHandComponent || isPokeComponent)
					behaviour.Destroy();
			}

			// 3. Plus rien ne doit rester de la physique de la main sur l'armature.
			foreach (var collider in handRoot.GetComponentsInChildren<Collider>(true))
				collider.Destroy();

			foreach (var body in handRoot.GetComponentsInChildren<Rigidbody>(true))
				body.Destroy();
		}

		/// <summary>
		/// Apparie les os de doigts de l'armature avec ceux du duplicata (même ordre : le duplicata
		/// est une copie de l'armature).
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
		/// Résout la cible IK d'un bras. Sans contrôleur suivi, on retombe sur le pivot de la main
		/// pour que l'IK tourne quand même (et on signale la cause au lieu de laisser une T-pose).
		/// </summary>
		private Transform ResolveArmTarget(string side, IHand hand, ref Transform handOffset, Transform ikTarget) {
			if (handOffset == null) {
				Logger.LogWarning(
					$"{nameof(NoxAutoHandVRIK)}: no {side} tracked controller, {side} arm IK target falls back to the hand pivot"
					+ $" — the {side} arm will not follow the controller.",
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

		/// <summary>
		/// Ancre IK du bras : un objet à part, reposé chaque frame par <see cref="RefreshIkTargets"/>.
		/// </summary>
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
