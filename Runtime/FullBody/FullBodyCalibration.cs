using System.Collections.Generic;
using System.Linq;
using Autohand;
using Nox.Avatars.Parameters;
using Nox.Avatars.Rigging;
using Nox.Avatars.Scale;
using Nox.Avatars.StateMachines;
using Nox.CCK.Avatars.Playable;
using Nox.CCK.Avatars.Rigging;
using Nox.CCK.Players;
using Nox.CCK.Utils;
using Nox.CCK.XR;
using Nox.Settings;
using Nox.XR.Runtime.Connectors;
using Nox.XR.Runtime.Settings;
using Nox.XR.Trackers;
using UnityEngine;
using UnityEngine.XR;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Runtime.FullBody {
	/// <summary>
	/// Full-body tracking calibration and driver for the local XR player.
	/// <para>
	/// <b>Calibration</b> demande à l'avatar sa pose de calibration — la couche standard « Pose »,
	/// <see cref="AvatarPose.Calibration"/> — puis affiche chaque tracker détecté comme une sphère à côté du
	/// bone auquel il est rattaché et le relie au bone compatible le plus proche dans la portée de
	/// <see cref="FullBodyCalibrationRangeSetting"/> (au plus un tracker par bone). Les offsets obtenus sont
	/// mémorisés par numéro de série d'appareil (voir <see cref="FullBodyCalibrationData"/>) et réappliqués ensuite.
	/// </para>
	/// <para>
	/// <b>Driving</b> writes the calibrated hip/foot rig targets every frame from the tracker poses,
	/// with the position offset re-scaled by the avatar scale so the result survives avatar scaling.
	/// </para>
	/// </summary>
	[DefaultExecutionOrder(16)]
	public class FullBodyCalibration : MonoBehaviour {
		public static FullBodyCalibration Instance { get; private set; }

		/// <summary>
		/// Bones a tracker may be assigned to: the parts the avatar's rig actually exposes
		/// (<see cref="IRigging.GetParts"/> — each part id is a <see cref="PlayerRig"/> — converted to a humanoid bone).
		/// Sourcing them from the rig makes the calibration follow whatever the active backend provides
		/// (FinalIK exposes pelvis/feet, RigBuilder also exposes spine/arms/legs/toes) instead of a hard-coded list.
		/// </summary>
		public static List<HumanBodyBones> GetAssignableBones(IRigging rig) {
			var bones = new List<HumanBodyBones>();
			if (rig == null)
				return bones;

			var parts = rig.GetParts();

			for (var i = 0; i < parts.Length; i++) {
				var bone = ((PlayerRig)parts[i].GetId()).ToHumanBodyBones();
				if (bone == HumanBodyBones.LastBone || bones.Contains(bone))
					continue;
				bones.Add(bone);
			}

			return bones;
		}

		public AvatarLoaderConnector avatarLoader;
		public AutoHandPlayer         player;

		/// <summary>Bones pilotés sur lesquels l'avertissement « aucune cible IK » a déjà été émis.</summary>
		private readonly HashSet<HumanBodyBones> _missingParts = new();

		// Owned by this mod (nox.xr): assets live in Packages/nox.xr/Assets/xr/calibration.
		private const string TrackerVisualAddress = "calibration/tracker.prefab";
		private const string BoneVisualAddress    = "calibration/bone.prefab";
		private const string RangeVisualAddress   = "calibration/range.prefab";

		private readonly List<TrackerPose> _trackers = new();
		private readonly Dictionary<HumanBodyBones, TrackerPose> _matches = new();

		/// <summary>Validation armée (voir <see cref="CheckValidation"/>).</summary>
		private bool _validationLatch;

		/// <summary>Temps restant avant que la validation ne puisse s'armer, après le lancement.</summary>
		private float _validationArmedIn;

		/// <summary>Délai d'armement de la validation (l'appui qui lance la calibration doit retomber).</summary>
		private const float ValidationArmDelay = 0.35f;

		/// <summary>
		/// Blocage temporaire après une validation : le même appui peut valider (aux manettes) et cliquer le
		/// bouton du menu, ce qui relancerait une calibration aussitôt après l'avoir validée.
		/// </summary>
		private float _restartCooldown;

		private const float RestartCooldown = 0.5f;

		private FullBodyCalibrationData _data = new();
		private Transform              _visualsRoot;
		private readonly List<GameObject> _trackerVisuals = new();
		private readonly Dictionary<HumanBodyBones, GameObject> _boneVisuals  = new();
		private readonly Dictionary<HumanBodyBones, GameObject> _rangeVisuals = new();

		public bool IsCalibrating { get; private set; }

		public bool HasCalibration
			=> !_data.IsEmpty;

		#region Diagnostics

		/// <summary>Stored calibration (read-only view for the diagnostics).</summary>
		public FullBodyCalibrationData Data
			=> _data;

		/// <summary>Trackers matched during the current calibration run, per bone (diagnostics).</summary>
		public IReadOnlyDictionary<HumanBodyBones, TrackerPose> Matches
			=> _matches;

		/// <summary>Live capture radius, avatar scale included (diagnostics).</summary>
		public float CurrentRange
			=> EffectiveRange;

		#endregion

		/// <summary>
		/// Some runtimes flag a Vive tracker as a controller (a role was assigned to it).
		/// Accepting them keeps the tracker usable; the real controllers stay filtered out because
		/// they are also flagged <c>Left</c>/<c>Right</c>.
		/// </summary>
		private static bool AllowControllerFlaggedTrackers
			=> TrackersIncludeControllerSetting.Value;

		/// <summary>
		/// Whether full-body tracking may run at all: the local player must be using the XR proxy,
		/// i.e. the XR controller must be the active controller. This prevents calibration (and the
		/// tracker driving) from being offered or applied outside of XR (desktop mode, other proxies).
		/// </summary>
		public static bool IsXRControllerActive
			=> XRController.IsCurrent();

		#region Lifecycle

		private void Awake()
			=> Instance = this;

		private void OnEnable()
			=> _data = FullBodyCalibrationStore.Load();

		private void OnDisable() {
			if (IsCalibrating)
				CancelCalibration();
			ClearVisuals();
		}

		private void OnDestroy() {
			SetExternalRootControl(false);
			if (Instance == this)
				Instance = null;
		}

		private void Update() {
			if (_restartCooldown > 0f)
				_restartCooldown -= Time.unscaledDeltaTime;

			if (!IsXRControllerActive)
				return;

			if (IsCalibrating)
				TickCalibration();
		}

		private void LateUpdate() {
			if (!IsXRControllerActive)
				return;

			// Pendant la calibration l'avatar est dans sa pose de calibration (l'avatar a coupé son propre suivi) :
			// les cibles du rig ne sont pas écrites, sinon l'IK réécrirait la pose par-dessus. La racine, elle, est
			// alignée sur la tête (voir ApplyCalibrationRoot).
			if (IsCalibrating) {
				ApplyCalibrationRoot();
				return;
			}

			TickDriving();
		}

		#endregion

		#region Public API

		/// <summary>Starts a new calibration session (idempotent).</summary>
		public void StartCalibration() {
			if (IsCalibrating || _restartCooldown > 0f)
				return;
			if (!IsXRControllerActive) {
				Logger.LogWarning("Full-body calibration is only available while the XR controller is active.", this, tag: nameof(FullBodyCalibration));
				return;
			}

			IsCalibrating = true;
			_matches.Clear();
			// La calibration est lancée par un clic dans le menu, donc avec la gâchette : on désarme la
			// validation jusqu'à ce que les deux mains soient relâchées (sinon elle se validait seule).
			_validationLatch  = false;
			_validationArmedIn = ValidationArmDelay;

			// La pose de calibration est demandée à l'avatar (couche « Pose ») : c'est elle qui coupe le suivi
			// (TrackingControl) et met l'avatar en T-pose, il n'y a pas d'autre chemin.
			SetPose(AvatarPose.Calibration);

			// Le jeu pilote la racine pendant la calibration (alignée sur la tête) : VRIK arrête de la déplacer
			// lui-même (voir ApplyCalibrationRoot).
			SetExternalRootControl(true);

			CreateVisuals();
			NotifySettingsMenu();
			Logger.Log($"Full-body calibration started (pose {AvatarPose.Calibration}).", this, tag: nameof(FullBodyCalibration));
		}

		/// <summary>Applies the current matches, persists them and leaves calibration mode.</summary>
		public void ConfirmCalibration() {
			if (!IsCalibrating)
				return;

			IsCalibrating = false;
			_restartCooldown = RestartCooldown;
			CaptureMatches();
			FullBodyCalibrationStore.Save(_data);

			ClearVisuals();
			SetPose(AvatarPose.Normal);
			SetExternalRootControl(false);
			NotifySettingsMenu();
			Logger.Log($"Full-body calibration saved ({_data.Bindings.Length} tracker(s)).", this, tag: nameof(FullBodyCalibration));
		}

		/// <summary>Cancels calibration without saving; the previously stored calibration is kept.</summary>
		public void CancelCalibration() {
			if (!IsCalibrating)
				return;

			IsCalibrating = false;
			_restartCooldown = RestartCooldown;
			_matches.Clear();
			ClearVisuals();
			SetPose(AvatarPose.Normal);
			_data = FullBodyCalibrationStore.Load();
			SetExternalRootControl(false);
			NotifySettingsMenu();
		}

		/// <summary>Forgets the stored calibration.</summary>
		public void ClearCalibration() {
			_data = new FullBodyCalibrationData();
			FullBodyCalibrationStore.Clear();
			NotifySettingsMenu();
		}

		/// <summary>
		/// Rafraîchit le menu de réglages : le libellé du bouton de calibration dépend de l'état de la
		/// calibration, qui peut changer sans clic — validation aux manettes, perte du contrôleur XR,
		/// nettoyage depuis le panneau. Un handler « nul » rafraîchit tous les handlers (comme l'événement
		/// <c>controller_changed</c> de nox.settings).
		/// </summary>
		private static void NotifySettingsMenu()
			=> SettingsNotifier.NotifyUpdated(null);

		#endregion

		#region Calibration

		private void TickCalibration() {
			var rig = GetRig();
			if (rig == null)
				return;

			FullBodyTrackers.Get(_trackers, excludeHanded: true, excludeControllers: !AllowControllerFlaggedTrackers);
			UpdateTrackerVisuals();
			MatchTrackers(rig);
			UpdateBoneVisuals(rig);
			CheckValidation();
		}

		/// <summary>
		/// Validates the calibration with the controllers: both trigger/grip buttons together, or a
		/// single one when only one controller is connected or <see cref="OneHandValidationSetting"/> is on.
		/// <para/>
		/// La validation est <b>désarmée</b> tant que les deux mains n'ont pas été relâchées : le clic qui
		/// lance la calibration (ou le menu) se fait à la gâchette, et sans ça la calibration se validait
		/// toute seule dans la frame suivante.
		/// </summary>
		private void CheckValidation() {
			// Délai d'armement : laisse retomber l'appui qui a lancé la calibration.
			if (_validationArmedIn > 0f) {
				_validationArmedIn -= Time.unscaledDeltaTime;
				return;
			}

			var leftConnected  = XRInputs.HasHandLeft;
			var rightConnected = XRInputs.HasHandRight;
			if (!leftConnected && !rightConnected)
				return;

			var left  = leftConnected && IsHandPressed(XRNode.LeftHand);
			var right = rightConnected && IsHandPressed(XRNode.RightHand);

			// Rien de pressé : la validation s'arme, et ne se déclenchera qu'à la pression suivante.
			if (!left && !right) {
				_validationLatch = true;
				return;
			}

			if (!_validationLatch)
				return;

			// One hand is enough when the setting is on, or when a single controller is connected.
			var singleEnough = OneHandValidationSetting.Value || leftConnected != rightConnected;
			var confirmed    = singleEnough ? left || right : left && right;
			if (!confirmed)
				return;

			_validationLatch = false;
			ConfirmCalibration();
		}

		private static bool IsHandPressed(XRNode node) {
			HandBuffer.Clear();
			InputDevices.GetDevicesAtXRNode(node, HandBuffer);

			foreach (var device in HandBuffer) {
				if (!device.isValid)
					continue;
				if (device.TryGetFeatureValue(CommonUsages.triggerButton, out var trigger) && trigger)
					return true;
				if (device.TryGetFeatureValue(CommonUsages.gripButton, out var grip) && grip)
					return true;
			}

			return false;
		}

		private static readonly List<InputDevice> HandBuffer = new();

		/// <summary>
		/// Attache chaque tracker à son bone le plus proche <b>dans la portée</b> (elle borne la distance
		/// maximale du lien), à raison d'un tracker par bone et d'un bone par tracker : les paires les
		/// plus proches sont liées d'abord. Au-delà de la portée, le tracker reste ignoré — c'est ce qui
		/// se voit à l'écran (le bone lié affiche sa portée, les autres rien).
		/// </summary>
		private void MatchTrackers(IRigging rig) {
			_matches.Clear();
			var bones = GetAssignableBones(rig);
			if (_trackers.Count == 0 || bones.Count == 0)
				return;

			var range      = EffectiveRange;
			var used       = new bool[_trackers.Count];
			var candidates = new List<(float distance, int bone, int tracker)>();

			for (var b = 0; b < bones.Count; b++) {
				var boneTransform = ReferenceBone(rig, bones[b]);
				if (!boneTransform)
					continue;

				for (var t = 0; t < _trackers.Count; t++) {
					var distance = Vector3.Distance(boneTransform.position, _trackers[t].Position);
					if (distance <= range)
						candidates.Add((distance, b, t));
				}
			}

			// Greedy: closest pairs first, one bone and one tracker each.
			candidates.Sort((a, b) => a.distance.CompareTo(b.distance));
			foreach (var (_, boneIndex, trackerIndex) in candidates) {
				var bone = bones[boneIndex];
				if (used[trackerIndex] || _matches.ContainsKey(bone))
					continue;
				used[trackerIndex] = true;
				_matches[bone]     = _trackers[trackerIndex];
			}
		}

		private void CaptureMatches() {
			var rig   = GetRig();
			var scale = AvatarScale;
			var list  = new List<FullBodyTrackerBinding>();

			foreach (var pair in _matches) {
				var boneTransform = rig == null ? null : ReferenceBone(rig, pair.Key);
				if (!boneTransform)
					continue;

				var tracker = pair.Value;
				var inverse = Quaternion.Inverse(tracker.Rotation);
				var offset  = inverse * (boneTransform.position - tracker.Position);

				// Un bone à plus d'une portée de son tracker veut dire que la pose de calibration n'est pas
				// celle attendue (rig non figé, couche de calibration absente…) : le lien serait faux.
				if (offset.magnitude > EffectiveRange)
					Logger.LogWarning($"Calibration: {pair.Key} est à {offset.magnitude:0.##} m de {tracker.Id} — la pose de calibration semble fausse (l'avatar doit être en pose {AvatarPose.Calibration} pendant la calibration), l'offset risque d'être inutilisable.", this, tag: nameof(FullBodyCalibration));

				list.Add(new FullBodyTrackerBinding {
					TrackerId      = tracker.Id,
					Bone           = (int)pair.Key,
					OffsetPosition = offset,
					OffsetRotation = inverse * boneTransform.rotation,
					Scale          = scale
				});
			}

			_data = new FullBodyCalibrationData { Bindings = list.ToArray() };
		}

		#endregion

		#region Driving

		private void TickDriving() {
			if (_data.IsEmpty)
				return;

			var rig = GetRig();
			if (rig == null)
				return;

			FullBodyTrackers.Get(_trackers, excludeHanded: true, excludeControllers: !AllowControllerFlaggedTrackers);

			var scale = AvatarScale;

			foreach (var binding in _data.Bindings) {
				var bone  = binding.BodyBone;
				var found = FindTracker(binding.TrackerId);

				// Tracker absent (débranché, batterie vide, session qui redémarre…) : le bone repasse en
				// IK au lieu de rester figé sur sa dernière pose, et reprendra tout seul au retour.
				if (found == null) {
					// On rend la main à l'avatar (plus d'override) avant de relâcher le canal contrôleur.
					rig.SetTracking(bone, RiggingTrackingMode.Normal);
					if (rig.IsActive(bone))
						rig.SetActive(bone, false);
					SetTrackingActive(bone, false);
					continue;
				}

				// La cible IK du bone doit être active, sinon le rig ignore l'écriture : c'est le cas du
				// pelvis en 3-point (poids VRIK à 0 tant qu'aucun tracker de bassin n'est utilisé).
				//
				// Le suivi physique prime sur la pose demandée par l'avatar : un tracker présent suit, même
				// quand un TrackingControl a coupé le membre. Après une calibration, l'état « Calibration »
				// laisse le bassin en Animation et l'état « Normal » ne le rétablit pas (il ne liste que la
				// tête, les mains et les pieds) : sans ça l'override reste, `IsActive` est faux et les poids
				// VRIK du pelvis restent à 0 — la cible du bassin est écrite mais jamais suivie.
				rig.SetTracking(bone, RiggingTrackingMode.Tracking);
				rig.SetActive(bone, true);
				SetTrackingActive(bone, true);

				var tracker     = found.Value;
				var scaleFactor = binding.Scale > 0f ? scale / binding.Scale : 1f;
				var offset      = tracker.Rotation * (binding.OffsetPosition * scaleFactor);

				// Le bassin : l'offset tracker → bassin (~13 cm, quasi horizontal) ne suit que le *cap* du
				// tracker. Avec la rotation complète, incliner le tracker faisait basculer cet offset dans le
				// plan vertical : le bassin montait quand on inclinait le tracker vers le bas, et ne bougeait
				// pas dans l'autre sens (la descente est bloquée par la colonne). Une rotation autour de l'axe
				// vertical préserve la composante Y de l'offset : la hauteur du bassin ne dépend donc plus de
				// l'inclinaison, alors que sa translation et son cap continuent de le déplacer.
				// (L'offset est appliqué en entier, 3 axes : c'est la position du bone donnée par le tracker,
				// mesurée pendant la calibration — l'aplatir sur le plan jetait sa composante Y.)
				if (bone == HumanBodyBones.Hips) {
					var yaw = YawOf(tracker.Rotation);
					if (yaw.HasValue)
						offset = yaw.Value * (binding.OffsetPosition * scaleFactor);
				}

				var position = tracker.Position + offset;
				var rotation = tracker.Rotation * binding.OffsetRotation;

				if (RigPartDriver.Write(rig, bone.ToIndex(), position, rotation))
					_missingParts.Remove(bone);
				else if (_missingParts.Add(bone))
					Logger.LogWarning($"Full-body tracking: aucune cible IK pour {bone} sur le rig — ce bone ne suivra pas son tracker.", this, tag: nameof(FullBodyCalibration));
			}
		}

		/// <summary>
		/// Cap (rotation autour de l'axe vertical) de <paramref name="rotation"/> — utilisé pour le bassin, dont
		/// l'offset de position ne doit pas suivre le piqué/roulis du tracker. Renvoie <c>null</c> quand le cap
		/// n'est pas définissable (axe avant, puis axe « up », quasi verticaux — tracker posé à plat).
		/// </summary>
		private static Quaternion? YawOf(Quaternion rotation) {
			var forward = rotation * Vector3.forward;
			forward.y = 0f;
			if (forward.sqrMagnitude < 0.0001f) {
				forward = rotation * Vector3.up;
				forward.y = 0f;
				if (forward.sqrMagnitude < 0.0001f)
					return null;
			}

			return Quaternion.LookRotation(forward.normalized, Vector3.up);
		}

		/// <summary>
		/// Bone servant de référence pour le suivi d'un membre. Pour un pied, le solveur de jambe de VRIK fait
		/// correspondre la cible au <b>dernier</b> bone de la chaîne — les orteils quand le rig les expose
		/// (<c>IKSolverVRLeg.Leg.PreSolve</c> : <c>position = lastBone.solverPosition</c>,
		/// <c>toes.solverRotation = IKRotation</c>), comme <c>VRIKCalibrator.CalibrateLeg</c> qui prend
		/// <c>leftToes ?? leftFoot</c>. Mesurer l'offset sur la cheville tirerait les orteils vers la position de
		/// la cheville et leur donnerait la rotation de la cheville.
		/// </summary>
		private static Transform ReferenceBone(IRigging rig, HumanBodyBones bone) {
			if (bone == HumanBodyBones.LeftFoot || bone == HumanBodyBones.RightFoot) {
				var toes = rig.GetBone(bone == HumanBodyBones.LeftFoot ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes);
				if (toes)
					return toes;
			}

			return rig.GetBone(bone);
		}

		private TrackerPose? FindTracker(string id) {
			foreach (var tracker in _trackers)
				if (tracker.Id == id)
					return tracker;
			return null;
		}

		/// <summary>Reflète l'état des bones suivis sur les paramètres <c>tracking/*/active</c> de l'avatar.</summary>
		private void SetTrackingActive(HumanBodyBones bone, bool active) {
			var parameters = GetParameters();
			if (parameters == null)
				return;

			var parameter = parameters.GetParameter($"tracking/{bone.ToString().ToSnakeCase()}/active");
			if (parameter != null)
				parameter.Value = active;
		}

		#endregion

		#region Pose

		/// <summary>
		/// Demande une pose à l'avatar par sa couche standard « Pose » : seul l'entier <c>Pose</c> est écrit
		/// (0 normal, 1 présentation, 2 calibration, 3 assis — voir <see cref="AvatarPose"/>), c'est l'avatar
		/// qui décide de la suite : ses états coupent le suivi avec <c>TrackingControl</c> et arrêtent ou
		/// reprennent ses autres couches avec <c>PlayableLayerControl</c>. Le jeu ne touche donc ni aux poids des
		/// couches, ni aux bones : il n'y a qu'une seule façon de mettre l'avatar en pose de calibration.
		/// </summary>
		private void SetPose(AvatarPose pose) {
			var parameter = GetParameters()?.GetParameter(AvatarPoseConstants.ParameterName);
			if (parameter == null) {
				if (pose != AvatarPose.Normal)
					Logger.LogWarning($"Calibration: l'avatar n'a pas de paramètre « {AvatarPoseConstants.ParameterName} » : la pose « {pose} » ne sera pas appliquée (couches : {AvailableLayers()}).", this, tag: nameof(FullBodyCalibration));
				return;
			}

			parameter.Value = (int)pose;

			// La couche doit être à 1 pour que sa pose soit visible : le module les démarre toutes à 1, mais un
			// autre contrôle a pu l'arrêter. Son état « normal » ne fait rien, la laisser active est sans effet.
			if (pose != AvatarPose.Normal)
				GetPlayableLayers()?.StartLayer(PlayableLayerNaming.Pose.ToString());

			Logger.LogDebug($"Calibration: pose « {pose} » demandée à l'avatar.", this, tag: nameof(FullBodyCalibration));
		}

		/// <summary>Couches de l'avatar, pour les messages de diagnostic (« clé[rôle] »).</summary>
		private string AvailableLayers() {
			var layers = GetPlayableLayers();
			if (layers == null)
				return "aucune";

			var keys = new List<string>();
			for (var i = 0; i < layers.LayerCount; i++)
				keys.Add($"{layers.GetLayerKey(i)}[{layers.GetLayerRole(i)}]");
			return string.Join(", ", keys);
		}

		#endregion

		#region Visuals

		private void CreateVisuals() {
			ClearVisuals();

			_visualsRoot = new GameObject("FBT Calibration Visuals").transform;
			_visualsRoot.SetParent(player ? player.transform : transform, false);
		}

		private void UpdateTrackerVisuals() {
			while (_trackerVisuals.Count < _trackers.Count)
				_trackerVisuals.Add(CreateVisual(TrackerVisualAddress));

			for (var i = 0; i < _trackerVisuals.Count; i++) {
				var visual = _trackerVisuals[i];
				if (!visual)
					continue;

				var active = i < _trackers.Count;
				visual.SetActive(active);
				if (active)
					visual.transform.SetPositionAndRotation(_trackers[i].Position, _trackers[i].Rotation);
			}
		}

		private void UpdateBoneVisuals(IRigging rig) {
			var bones = GetAssignableBones(rig);
			if (bones.Count == 0)
				return;

			foreach (var bone in bones) {
				var boneTransform = rig?.GetBone(bone);
				if (!boneTransform)
					continue;

				if (!_boneVisuals.TryGetValue(bone, out var marker) || !marker) {
					marker = CreateVisual(BoneVisualAddress);
					_boneVisuals[bone] = marker;
				}
				if (marker)
					marker.transform.SetPositionAndRotation(boneTransform.position, boneTransform.rotation);

				if (!_rangeVisuals.TryGetValue(bone, out var rangeVisual) || !rangeVisual) {
					rangeVisual = CreateVisual(RangeVisualAddress);
					_rangeVisuals[bone] = rangeVisual;
				}
				if (rangeVisual) {
					rangeVisual.transform.SetPositionAndRotation(boneTransform.position, boneTransform.rotation);
					// La sphère d'un bone lié se cale sur la distance réelle tracker ↔ bone (elle passe donc
					// par le tracker), et disparaît sur les bones sans tracker.
					SetWorldRadius(rangeVisual.transform, LinkedDistance(bone, boneTransform.position));
				}
			}
		}

		/// <summary>Distance entre <paramref name="bone"/> et le tracker qui lui est attaché, 0 s'il n'y en a pas.</summary>
		private float LinkedDistance(HumanBodyBones bone, Vector3 bonePosition)
			=> _matches.TryGetValue(bone, out var tracker)
				? Vector3.Distance(bonePosition, tracker.Position)
				: 0f;

		/// <summary>
		/// Applique un rayon en unités <b>monde</b> à un visuel dont la sphère fait 1 unité de diamètre
		/// (sphère Unity). L'échelle du parent est compensée : les visuels sont enfants du joueur, qui
		/// porte l'échelle de l'avatar — sans cela la sphère ne représentait plus la portée réelle
		/// (elle subissait l'échelle une seconde fois).
		/// </summary>
		private static void SetWorldRadius(Transform visual, float radius) {
			const float diameter = 2f;

			var parent = visual.parent;
			var scale  = parent ? parent.lossyScale : Vector3.one;

			visual.localScale = new Vector3(
				radius * diameter / Mathf.Max(0.0001f, Mathf.Abs(scale.x)),
				radius * diameter / Mathf.Max(0.0001f, Mathf.Abs(scale.y)),
				radius * diameter / Mathf.Max(0.0001f, Mathf.Abs(scale.z))
			);
		}

		private GameObject CreateVisual(string address) {
			var prefab = Client.CoreAPI?.AssetAPI?.GetAsset<GameObject>(address);
			if (!prefab) {
				Logger.LogWarning($"Calibration visual prefab not found: {address}", this, tag: nameof(FullBodyCalibration));
				return null;
			}

			return prefab.Instantiate(_visualsRoot);
		}

		private void ClearVisuals() {
			foreach (var visual in _trackerVisuals)
				if (visual)
					visual.Destroy();
			_trackerVisuals.Clear();

			foreach (var visual in _boneVisuals.Values)
				if (visual)
					visual.Destroy();
			_boneVisuals.Clear();

			foreach (var visual in _rangeVisuals.Values)
				if (visual)
					visual.Destroy();
			_rangeVisuals.Clear();

			if (_visualsRoot) {
				_visualsRoot.gameObject.Destroy();
				_visualsRoot = null;
			}
		}

		#endregion

		#region Helpers

		/// <summary>Calibration range, grown by the avatar scale so a scaled avatar keeps a usable window.</summary>
		private float EffectiveRange
			=> FullBodyCalibrationRangeSetting.Value * Mathf.Max(0.01f, AvatarScale);

		/// <summary>
		/// Aligne la racine de l'avatar sur la tête pendant la calibration : même position X/Z et même cap (yaw),
		/// ce qui permet au joueur d'aligner son avatar sur son corps (les pieds de la T-pose restent sous lui).
		/// <para>
		/// VRIK ne pilote plus la racine pendant la calibration (<c>SetExternalRootControl</c>) : sans ça sa
		/// locomotion la déplace d'après la tête (les pieds « glissaient » quand on bouge la tête) et son
		/// rattrapage d'angle la translate autour du pivot de l'Animator. Le Y est laissé tel quel,
		/// <c>AvatarSyncConnector</c> le recale sur le joueur.
		/// </para>
		/// </summary>
		private void ApplyCalibrationRoot() {
			var anchor = GetAnchor()?.transform;
			var head   = GetHeadTransform();
			if (!anchor || !head)
				return;

			// Position : sous la tête (X/Z de la cible de tête, Y laissé à AvatarSyncConnector).
			var position = anchor.position;
			var headPos  = head.position;
			anchor.position = new Vector3(headPos.x, position.y, headPos.z);

			// Cap : l'avatar regarde où le joueur regarde (yaw seul, on ignore le piqué/roulis de la tête).
			var forward = head.forward;
			forward.y = 0f;
			if (forward.sqrMagnitude > 0.0001f)
				anchor.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
		}

		/// <summary>Cible de tête du rig (celle que suit le bone tête), sinon <c>null</c>.</summary>
		private Transform GetHeadTransform() {
			var rig = GetRig();
			return rig != null && rig.TryGetPart(HumanBodyBones.Head.ToIndex(), out var part) ? part.GetTransform() : null;
		}

		/// <summary>Donne/rend la main au rig sur la racine de l'avatar (voir <see cref="ApplyCalibrationRoot"/>).</summary>
		private void SetExternalRootControl(bool external)
			=> GetRig()?.SetExternalRootControl(external);

		private GameObject GetAnchor()
			=> avatarLoader?.GetAvatar()?.Descriptor?.Anchor;

		private float AvatarScale
			=> avatarLoader?.GetAvatar()?.Descriptor?
				.GetModules<IScaleAvatarModule>()
				.FirstOrDefault()?.Scale ?? 1f;

		private IRigging GetRig()
			=> avatarLoader?.GetAvatar()?.Descriptor?.Anchor
				?.GetComponentInChildren<IRigProvider>(true)
				?.GetRig();

		private IPlayableLayerModule GetPlayableLayers()
			=> avatarLoader?.GetAvatar()?.Descriptor?
				.GetModules<IPlayableLayerModule>()
				.FirstOrDefault();

		private IParameterModule GetParameters()
			=> avatarLoader?.GetAvatar()?.Descriptor?
				.GetModules<IParameterModule>()
				.FirstOrDefault();

		#endregion
	}
}
