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
		public IHand rightSource;
		[Tooltip("Données IHand de la main gauche de l'avatar (pivot, os, doigts).")]
		public IHand leftSource;
		[Tooltip("Dossier « Hands » du rig XR : c'est là que sont créés les duplicatas physiques "
		         + "des mains de l'avatar (à côté des mains de remplacement AutoHand).")]
		public Transform physicalHandsRoot;

		/// <summary>Main AutoHand (duplicata) du côté droit : c'est le corps physique.</summary>
		public Hand rightPhysical { get; private set; }
		/// <summary>Main AutoHand (duplicata) du côté gauche : c'est le corps physique.</summary>
		public Hand leftPhysical { get; private set; }

		/// <summary>
		/// <see cref="IHand"/> décrivant le duplicata droit (ses propres os) : c'est lui qui a été
		/// converti en <see cref="rightPhysical"/>.
		/// </summary>
		public IHand rightPhysicalSource { get; private set; }
		/// <summary>Voir <see cref="rightPhysicalSource"/>.</summary>
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
			// Les duplicatas des mains ont été créés par nous : c'est à nous de les jeter.
			if (rightPhysical != null) rightPhysical.gameObject.Destroy();
			if (leftPhysical  != null) leftPhysical.gameObject.Destroy();

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
			_rightHandOffset = CreateHandOffset(rightTrackedController, rightSource);
			_leftHandOffset  = CreateHandOffset(leftTrackedController,  leftSource);

			rightPhysical = CreatePhysicalHand(rightSource, _rightHandOffset, "R", out var rgs);
			leftPhysical  = CreatePhysicalHand(leftSource,  _leftHandOffset,  "L", out var lgs);

			rightPhysicalSource = rgs;
			leftPhysicalSource  = lgs;

			// AutoHandPlayer doit piloter les mains de l'avatar (les duplicatas), pas les mains de
			// remplacement : ce sont elles qui portent les colliders, les pokes et les doigts.
			if (AutoHandPlayer.Instance != null) {
				if (rightPhysical != null) AutoHandPlayer.Instance.handRight = rightPhysical;
				if (leftPhysical  != null) AutoHandPlayer.Instance.handLeft  = leftPhysical;
			}

			SubscribeGrabs();

			// Doigts : os de l'armature ← os du duplicata (même hiérarchie, mêmes index).
			BuildFingerJoints(rightSource, rightPhysical, out _rightVisualJoints, out _rightPhysicalJoints);
			BuildFingerJoints(leftSource,  leftPhysical,  out _leftVisualJoints,  out _leftPhysicalJoints);

			// L'ancre IK du bras est un objet à part (enfant de l'avatar, pas de la main).
			_rightIkTarget = CreateIkTarget();
			_leftIkTarget  = CreateIkTarget();

			// Chaque bras est résolu INDÉPENDAMMENT : un côté sans contrôleur (ou sans main) ne doit
			// pas empêcher l'autre d'être configuré.
			vrik.solver.rightArm.target = ResolveArmTarget("right", rightSource, ref _rightHandOffset, _rightIkTarget);
			vrik.solver.leftArm.target  = ResolveArmTarget("left",  leftSource,  ref _leftHandOffset,  _leftIkTarget);

			Logger.LogDebug(
				$"{nameof(NoxAutoHandVRIK)}: rig ready."
				+ $" right[cible={(rightTrackedController ? rightTrackedController.name : "NULL")}"
				+ $" pivot={(_rightHandOffset ? _rightHandOffset.name : "NULL")}"
				+ $" hand={(rightPhysical ? rightPhysical.name : "NULL")}]"
				+ $" left[cible={(leftTrackedController ? leftTrackedController.name : "NULL")}"
				+ $" pivot={(_leftHandOffset ? _leftHandOffset.name : "NULL")}"
				+ $" hand={(leftPhysical ? leftPhysical.name : "NULL")}]",
				this
			);
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
		/// Crée la <b>vraie</b> main AutoHand : un duplicata complet de la main de l'avatar (os,
		/// colliders, pokes, échelle) posé dans le dossier « Hands » du rig, rendu dynamique et
		/// piloté par <see cref="HandFollow"/> sur le pivot de l'avatar. Il est invisible (c'est la
		/// main de l'avatar qui est rendue) et c'est lui qui collisionne, avec la géométrie réelle
		/// de la main de l'avatar.
		/// <para>
		/// La main de l'avatar est décrite par un <see cref="IHand"/> qui vit <b>à côté</b> de
		/// l'armature : dupliquer l'ancre (un os) ne le copie donc pas. On en crée un pour le
		/// duplicata (<see cref="CreateGhostHand"/>) et c'est <b>lui</b> qui est converti, car
		/// <see cref="HandToAutoHand.Convert(IHand)"/> installe l'Autohand sur <c>IHand.Anchor</c> —
		/// un os du duplicata, jamais de l'armature.
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

			// Duplicata de l'ancre de la main : on copie la hiérarchie d'os, avec tout l'Autohand que
			// la conversion faite sur la main de l'avatar y a laissé (composants, colliders, pokes).
			// La source est reposée INACTIVE le temps de la copie : sans ça l'Autohand copié
			// s'éveillerait sur le duplicata (HandBase.Awake ajoute un HandStabilizer à la caméra, crée
			// la paume, la boîte d'encapsulation...) alors qu'on va le retirer.
			var sourceWasActive = anchor.gameObject.activeSelf;
			anchor.gameObject.SetActive(false);
			var ghost = anchor.gameObject.Instantiate(root);
			anchor.gameObject.SetActive(sourceWasActive);
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

			// Le duplicata ne garde que ses os et ses descripteurs : tout l'Autohand hérité de la main de
			// l'avatar (composants, colliders, rigidbody, animation, rendu) part maintenant, c'est
			// Convert() qui ré-équipera le duplicata.
			StripToBones(ghost);

			// La main DU DUPLICATA : mêmes descripteurs que celle de l'avatar, références re-pointées
			// sur les os du duplicata. On la valide avant de la convertir — Convert() lève une
			// exception sur une main invalide.
			ghostSource = CreateGhostHand(source, ghost, side);
			if (ghostSource != null && !Nox.CCK.Avatars.Hand.HandExtensions.IsValid(ghostSource, out var ghostError)) {
				Logger.LogError(
					$"{nameof(NoxAutoHandVRIK)}: the {side} hand could not be duplicated ({ghostError.Message}), "
					+ $"the {side} arm will stay in T-pose.",
					this
				);
				ghostSource = null;
			}

			// Le duplicata doit être ACTIF au moment de la conversion : Convert() le repose dans l'état
			// où il le trouve (SetActive(wasActive) à la fin) et c'est ce SetActive(true) qui déclenche
			// les Awake déjà configurés (HandBase : rigidbody, colliders, boîte d'encapsulation...).
			// Laissé inactif — il naît ainsi pour ne pas réveiller l'Autohand copié — la main physique
			// ne serait jamais initialisée.
			ghost.SetActive(true);

			var hand = ghostSource != null 
				? HandToAutoHand.Convert(ghostSource) 
				: null;
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
					$"{nameof(NoxAutoHandVRIK)}: the duplicata of the {side} hand has no Autohand.Hand component.",
					this
				);
			}

			return hand;
		}

		/// <summary>Os et descripteurs de la main : c'est tout ce que le duplicata garde.</summary>
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
		/// Ne laisse que les os et les descripteurs du duplicata : tout l'Autohand que la copie a hérité
		/// de la main de l'avatar (composants, colliders, rigidbody, animation, rendu) est retiré, et
		/// c'est <see cref="HandToAutoHand"/> qui ré-équipera le duplicata ensuite.
		/// <para>
		/// Deux précautions, sans lesquelles le dépouillage échoue en partie :
		/// <list type="bullet">
		/// <item>la suppression est <b>immédiate</b>. Avec un <c>Destroy</c> différé, le composant
		/// requis est encore là quand on retire ce qui dépend de lui : Unity refuse (« Can't remove
		/// Rigidbody because Hand depends on it ») et le laisse en place. Pire, le <c>Hand</c>
		/// « condamné » mais toujours vivant est retrouvé par la conversion qui suit
		/// (<c>GetOrAddComponent</c>), laquelle installe alors une main réellement détruite en fin de
		/// frame — d'où la <c>MissingReferenceException</c> dans AutoHandPlayer.UpdateTrackedObjects ;</item>
		/// <item>l'ordre suit les <c>RequireComponent</c> : un composant n'est retiré que lorsque plus
		/// personne ne le requiert, sinon Unity refuse aussi.</item>
		/// </list>
		/// </para>
		/// </summary>
		private static void StripToBones(GameObject handRoot) {
			var doomed = new List<Component>();
			foreach (var component in handRoot.GetComponentsInChildren<Component>(true))
				if (component != null && !IsAllowed(component))
					doomed.Add(component);

			// Par passes : ce qui ne peut pas encore partir (requis par un autre composant) est retenté
			// à la passe suivante, une fois son requérant parti.
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

			// Ne doit pas arriver : il reste un cycle de RequireComponent entre composants.
			if (doomed.Count > 0)
				Logger.LogWarning(
					$"{nameof(NoxAutoHandVRIK)}: {doomed.Count} component(s) could not be stripped from "
					+ $"'{handRoot.name}' (requirement cycle): {string.Join(", ", doomed.ConvertAll(c => c.GetType().Name))}.",
					handRoot
				);
		}

		/// <summary>
		/// Un des composants en attente de suppression requiert-il encore <paramref name="component"/>
		/// (<c>RequireComponent</c>, forcément sur le même GameObject) ?
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
		/// <paramref name="type"/> déclare-t-il <c>RequireComponent</c> sur <paramref name="required"/> ?
		/// Les attributs sont hérités (<c>HandBase</c> les déclare pour <c>Hand</c>) et leurs champs sont
		/// lus sans dépendre de leurs noms (<c>m_Type0</c>...).
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
		/// Donne au duplicata <b>sa</b> main : un <see cref="IHand"/> qui décrit la copie — même type,
		/// même pivot, même paume et mêmes doigts que la main de l'avatar, mais toutes ses références
		/// re-pointées sur les os du duplicata.
		/// <para>
		/// Les descripteurs de la main de l'avatar (composants <c>Hand</c>/<c>Finger</c> du CCK)
		/// vivent dans un dossier à part, hors de l'ancre : dupliquer l'ancre ne les copie donc pas.
		/// Sans cette main, le duplicata n'a que des os et <see cref="HandToAutoHand.Convert(IHand)"/>
		/// — qui équipe <c>IHand.Anchor</c> — irait équiper l'armature de l'avatar.
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
		/// Recopie un doigt de la main de l'avatar sur le duplicata : le descripteur se pose sur la
		/// copie de sa première phalange — comme le fait la conversion inverse
		/// (<see cref="AutoHandToHand.Convert(Autohand.Hand)"/>) — et ses jointures pointent les copies.
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

			// Les poses sont les rotations locales des os : les os du duplicata étant les copies de
			// ceux de l'avatar (mêmes repères locaux), elles se rejouent telles quelles.
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
		/// Retrouve, dans le duplicata, la copie d'une référence de la main de l'avatar : le duplicata
		/// étant une copie de l'ancre de la main, la copie se retrouve par son chemin relatif à
		/// l'ancre. Renvoie <c>null</c> quand la référence est hors de la main dupliquée (ou absente).
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

			// Remontée qui n'atteint pas l'ancre : la référence n'a pas été dupliquée.
			if (current != anchor)
				return null;

			path.Reverse();
			return ghostAnchor.Find(string.Join("/", path));
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
