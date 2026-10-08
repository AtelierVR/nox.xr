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
	/// <b>Calibration</b> asks the avatar for its calibration pose (<see cref="AvatarPose.Calibration"/>) and
	/// links every detected tracker to the closest compatible bone within
	/// <see cref="FullBodyCalibrationRangeSetting"/> (at most one tracker per bone).
	/// </para>
	/// <para>
	/// <b>Driving</b> writes the calibrated rig targets every frame from the tracker poses.
	/// </para>
	/// </summary>
	[DefaultExecutionOrder(16)]
	public class FullBodyCalibration : MonoBehaviour {
		public static FullBodyCalibration Instance { get; private set; }

		/// <summary>
		/// Bones a tracker may be assigned to: the parts the avatar's rig exposes, converted to humanoid bones.
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

		/// <summary>Bones already reported as having no IK target.</summary>
		private readonly HashSet<HumanBodyBones> _missingParts = new();

		// Owned by this mod (nox.xr): assets live in Packages/nox.xr/Assets/xr/calibration.
		private const string TrackerVisualAddress = "calibration/tracker.prefab";
		private const string BoneVisualAddress    = "calibration/bone.prefab";
		private const string RangeVisualAddress   = "calibration/range.prefab";

		private readonly List<TrackerPose> _trackers = new();
		private readonly Dictionary<HumanBodyBones, TrackerPose> _matches = new();

		/// <summary>Validation armed (see <see cref="CheckValidation"/>).</summary>
		private bool _validationLatch;

		/// <summary>Time left before the validation may arm.</summary>
		private float _validationArmedIn;

		/// <summary>Arming delay: the press that starts a calibration must be released first.</summary>
		private const float ValidationArmDelay = 0.35f;

		/// <summary>Short lockout after a validation, so the same press cannot start a new calibration.</summary>
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

		/// <summary>
		/// IK targets of the tracked bones (key = part index), published so the controller can expose them as
		/// parts: that channel (threshold, interpolation, batching) is what lets a viewer replay the pose applied
		/// locally. Velocities come straight from the tracker device.
		/// </summary>
		private static readonly Dictionary<ushort, (Vector3 Position, Quaternion Rotation, Vector3 Velocity, Vector3 Angular)> DriverTargets = new();

		/// <summary>Part ids of the full-body targets currently driven by a tracker.</summary>
		public static ICollection<ushort> DriverTargetIds
			=> DriverTargets.Keys;

		/// <summary>
		/// Target of a part, when a tracker drives it: a fresh <see cref="TransformObject"/> carrying the pose
		/// (position/rotation) and the tracker velocities, so callers may own and mutate it.
		/// </summary>
		public static bool TryGetDriverTarget(ushort partId, out TransformObject target) {
			target = null;
			if (!DriverTargets.TryGetValue(partId, out var driven))
				return false;

			target = new TransformObject();
			target.SetPosition(driven.Position);
			target.SetRotation(driven.Rotation);
			target.SetVelocity(driven.Velocity);
			target.SetAngular(driven.Angular);
			return true;
		}

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

		/// <summary>
		/// Usable trackers right now, with the same filters as the calibration: hands and controllers are
		/// excluded, a tracker flagged as a controller counts when <see cref="AllowControllerFlaggedTrackers"/> is on.
		/// 0 outside XR or when full-body tracking is off.
		/// </summary>
		public static int TrackerCount {
			get {
				if (!IsXRControllerActive || !FullBodyTrackingSetting.Value)
					return 0;

				_probeTrackers.Clear();
				FullBodyTrackers.Get(_probeTrackers, excludeHanded: true, excludeControllers: !AllowControllerFlaggedTrackers);
				return _probeTrackers.Count;
			}
		}

		/// <summary>Shared working list of <see cref="TrackerCount"/>, which only counts.</summary>
		private static readonly List<TrackerPose> _probeTrackers = new();

		#region Lifecycle

		private void Awake()
			=> Instance = this;

		private void OnEnable()
			=> _data = FullBodyCalibrationStore.Load();

		private void OnDisable() {
			DriverTargets.Clear();

			if (IsCalibrating)
				CancelCalibration();
			ClearVisuals();
		}

		private void OnDestroy() {
			DriverTargets.Clear();
			SetExternalRootControl(false);
			if (Instance == this)
				Instance = null;
		}

		private void Update() {
			if (_restartCooldown > 0f)
				_restartCooldown -= Time.unscaledDeltaTime;

			if (!IsXRControllerActive)
				return;

			if (IsEstimatingMetrics)
				TickMetricsEstimate();

			if (IsCalibrating)
				TickCalibration();
		}

		private void LateUpdate() {
			if (!IsXRControllerActive) {
				DriverTargets.Clear();
				return;
			}

			// The avatar is in its calibration pose, so the rig targets are not written.
			if (IsCalibrating) {
				DriverTargets.Clear();
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
			_validationLatch   = false;
			_validationArmedIn = ValidationArmDelay;

			SetPose(AvatarPose.Calibration);
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

		/// <summary>Refreshes the settings menu: the calibration button label follows the calibration state.</summary>
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
		/// Validates the calibration with the controllers: both trigger/grip buttons together, or a single one
		/// when only one controller is connected or <see cref="OneHandValidationSetting"/> is on. The validation
		/// only arms once both hands have been released.
		/// </summary>
		private void CheckValidation() {
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

			// Nothing pressed: the next press will validate.
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
		/// Links every tracker to its closest bone within the calibration range, one tracker per bone: the
		/// closest pairs are linked first, and a tie is broken by the height gap.
		/// </summary>
		private void MatchTrackers(IRigging rig) {
			_matches.Clear();
			var bones = GetAssignableBones(rig);
			if (_trackers.Count == 0 || bones.Count == 0)
				return;

			var range      = EffectiveRange;
			var used       = new bool[_trackers.Count];
			var candidates = new List<(float score, float distance, int bone, int tracker)>();

			for (var b = 0; b < bones.Count; b++) {
				var boneTransform = ReferenceBone(rig, bones[b]);
				if (!boneTransform)
					continue;

				for (var t = 0; t < _trackers.Count; t++) {
					var distance = Vector3.Distance(boneTransform.position, _trackers[t].Position);
					if (distance > range)
						continue;

					// The height gap breaks ties between bones at a comparable distance.
					var heightGap = Mathf.Abs(boneTransform.position.y - _trackers[t].Position.y);
					candidates.Add((distance + heightGap, distance, b, t));
				}
			}

			// Greedy: closest pairs first, one bone and one tracker each.
			candidates.Sort((a, b) => a.score.CompareTo(b.score));
			foreach (var (_, _, boneIndex, trackerIndex) in candidates) {
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

				// The hips are driven with the tracker's yaw (see TickDriving), so the offset is measured in the
				// same frame.
				if (pair.Key == HumanBodyBones.Hips && YawOf(tracker.Rotation) is { } captureYaw)
					offset = Quaternion.Inverse(captureYaw) * (boneTransform.position - tracker.Position);

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
			DriverTargets.Clear();

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

				// A tracker that is gone hands the bone back to the animation instead of leaving it frozen on its
				// last pose; publishing the animated reference pose keeps both channels consistent.
				if (found == null) {
					rig.SetTracking(bone, RiggingTrackingMode.Normal);
					if (rig.IsActive(bone))
						rig.SetActive(bone, false);
					SetTrackingActive(bone, false);

					var reference = ReferenceBone(rig, bone);
					if (reference) {
						var relaxed = (reference.position, reference.rotation, Vector3.zero, Vector3.zero);
						DriverTargets[bone.ToIndex()] = relaxed;
						RigPartDriver.Write(rig, bone.ToIndex(), relaxed.position, relaxed.rotation);
					}
					continue;
				}

				// A present tracker wins over the pose asked by the avatar: the target is always written.
				rig.SetTracking(bone, RiggingTrackingMode.Tracking);
				rig.SetActive(bone, true);
				SetTrackingActive(bone, true);

				var tracker = found.Value;

				// Only the avatar scale changed since the capture is compensated.
				var scaleFactor = binding.Scale > 0f ? scale / binding.Scale : 1f;
				var offset      = tracker.Rotation * (binding.OffsetPosition * scaleFactor);

				// The hips follow the tracker's yaw only, so tilting the tracker does not move the pelvis vertically.
				if (bone == HumanBodyBones.Hips) {
					var yaw = YawOf(tracker.Rotation);
					if (yaw.HasValue)
						offset = yaw.Value * (binding.OffsetPosition * scaleFactor);
				}

				var position = tracker.Position + offset;
				var rotation = tracker.Rotation * binding.OffsetRotation;

				// Published as a controller part: a viewer replays it through the parts channel.
				DriverTargets[bone.ToIndex()] = (position, rotation, tracker.Velocity, tracker.AngularVelocity);

				if (RigPartDriver.Write(rig, bone.ToIndex(), position, rotation))
					_missingParts.Remove(bone);
				else if (_missingParts.Add(bone))
					Logger.LogWarning($"Full-body tracking: no IK target for {bone} on the rig, this bone will not follow its tracker.", this, tag: nameof(FullBodyCalibration));
			}
		}

		/// <summary>
		/// Yaw of <paramref name="rotation"/> (rotation around the vertical axis), used for the hips so their
		/// position offset does not follow the tracker's pitch or roll. Null when the yaw cannot be defined.
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
		/// Bone used as the reference for a limb. For a foot, the VRIK leg solver matches the target to the last
		/// bone of the chain (the toes when the rig exposes them), and offsets are measured on it.
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

		/// <summary>Mirrors the state of the driven bones on the avatar's <c>tracking/*/active</c> parameters.</summary>
		private void SetTrackingActive(HumanBodyBones bone, bool active) {
			var parameters = GetParameters();
			if (parameters == null)
				return;

			var parameter = parameters.GetParameter($"tracking/{bone.ToString().ToSnakeCase()}/active");
			if (parameter != null)
				parameter.Value = active;
		}

		#endregion

		#region Player metrics (height / arm span)

		/// <summary>Duration of the T-pose capture started by <see cref="StartMetricsEstimate"/>.</summary>
		public const float MetricsEstimateDelay = 5f;

		/// <summary>
		/// Eye height as a fraction of the total height (anthropometric ratio). The headset gives the eye height,
		/// the total height follows.
		/// </summary>
		private const float EyeHeightRatio = 0.936f;

		/// <summary>Plausible player height: bounds an estimation made crouched or while moving.</summary>
		private const float MinPlayerHeight = 1.2f;
		private const float MaxPlayerHeight = 2.2f;

		private float _metricsRemaining;
		private float _metricsEyeHeight;
		private float _metricsArmSpan;
		private int   _metricsShownSecond = -1;

		/// <summary>True while the capture runs (shown by the settings button).</summary>
		public bool IsEstimatingMetrics
			=> _metricsRemaining > 0f;

		/// <summary>Seconds left in the capture.</summary>
		public float MetricsEstimateRemaining
			=> Mathf.Max(0f, _metricsRemaining);

		/// <summary>
		/// Starts the capture: stay in T-pose without moving. The highest eye position and the widest hand span
		/// are kept, then written to <see cref="RealHeightSetting"/> and <see cref="PlayerArmSpanSetting"/>.
		/// </summary>
		public void StartMetricsEstimate() {
			if (!IsXRControllerActive || IsCalibrating)
				return;

			_metricsRemaining = MetricsEstimateDelay;
			_metricsEyeHeight = 0f;
			_metricsArmSpan   = 0f;
			_metricsShownSecond = Mathf.CeilToInt(MetricsEstimateDelay);
			SettingsNotifier.NotifyUpdated(null);
		}

		private void TickMetricsEstimate() {
			_metricsRemaining -= Time.unscaledDeltaTime;

			var camera = player && player.headCamera ? player.headCamera.transform : null;
			if (camera)
				_metricsEyeHeight = Mathf.Max(_metricsEyeHeight, camera.position.y);

			// In T-pose the hand span is the arm span (wrist to wrist).
			if (player && player.handLeft && player.handRight)
				_metricsArmSpan = Mathf.Max(_metricsArmSpan, Vector3.Distance(player.handLeft.transform.position, player.handRight.transform.position));

			var second = Mathf.CeilToInt(Mathf.Max(0f, _metricsRemaining));
			if (second != _metricsShownSecond) {
				_metricsShownSecond = second;
				SettingsNotifier.NotifyUpdated(null);
			}

			// Same controller validation as a calibration, so the capture can be confirmed before the delay.
			if (_metricsRemaining > 0f && MetricsEstimateDelay - _metricsRemaining > ValidationArmDelay) {
				var leftConnected  = XRInputs.HasHandLeft;
				var rightConnected = XRInputs.HasHandRight;
				var left  = leftConnected && IsHandPressed(XRNode.LeftHand);
				var right = rightConnected && IsHandPressed(XRNode.RightHand);
				if ((left || right) && (!leftConnected || left) && (!rightConnected || right))
					_metricsRemaining = 0f;
			}

			if (_metricsRemaining > 0f)
				return;

			if (_metricsEyeHeight > 0.5f)
				RealHeightSetting.Value = Mathf.Clamp(_metricsEyeHeight / EyeHeightRatio, MinPlayerHeight, MaxPlayerHeight);

			if (_metricsArmSpan > 0f)
				PlayerArmSpanSetting.Value = _metricsArmSpan;

			Logger.LogDebug($"Mesures joueur : taille {RealHeightSetting.Value:F3} m (yeux {_metricsEyeHeight:F3} m), envergure {PlayerArmSpanSetting.Value:F3} m — avatar {AvatarHeight():F2} m", this, tag: nameof(FullBodyCalibration));
			SettingsNotifier.NotifyUpdated(null);
		}

		/// <summary>
		/// Real (world) height of the avatar in metres, from its scale module. The value is measured on the
		/// model's rest pose, so it does not follow the animation or the trackers.
		/// </summary>
		private float AvatarHeight()
			=> avatarLoader?.GetAvatar()?.Descriptor
				?.GetModules<IScaleAvatarModule>()
				.FirstOrDefault()?.Height ?? 0f;

		#endregion

		#region Pose

		/// <summary>
		/// Asks the avatar for a pose through its standard "Pose" layer (see <see cref="AvatarPose"/>): only the
		/// integer is written, the avatar cuts tracking and drives its own layers from it.
		/// </summary>
		private void SetPose(AvatarPose pose) {
			var parameter = GetParameters()?.GetParameter(AvatarPoseConstants.ParameterName);
			if (parameter == null) {
				if (pose != AvatarPose.Normal)
					Logger.LogWarning($"Calibration: l'avatar n'a pas de paramètre « {AvatarPoseConstants.ParameterName} » : la pose « {pose} » ne sera pas appliquée (couches : {AvailableLayers()}).", this, tag: nameof(FullBodyCalibration));
				return;
			}

			parameter.Value = (int)pose;

			// The layer must be running for the pose to be visible.
			if (pose != AvatarPose.Normal)
				GetPlayableLayers()?.StartLayer(PlayableLayerNaming.Pose.ToString());

			Logger.LogDebug($"Calibration: pose « {pose} » demandée à l'avatar.", this, tag: nameof(FullBodyCalibration));
		}

		/// <summary>Avatar layers, for the diagnostic messages.</summary>
		private string AvailableLayers() {
			var layers = GetPlayableLayers();
			if (layers == null)
				return "none";

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
					SetWorldRadius(rangeVisual.transform, LinkedDistance(bone, boneTransform.position));
				}
			}
		}

		/// <summary>Distance between <paramref name="bone"/> and its tracker, 0 when there is none.</summary>
		private float LinkedDistance(HumanBodyBones bone, Vector3 bonePosition)
			=> _matches.TryGetValue(bone, out var tracker)
				? Vector3.Distance(bonePosition, tracker.Position)
				: 0f;

		/// <summary>
		/// Applies a world radius to a visual whose sphere is one unit across, compensating the parent scale:
		/// the visuals are children of the player, which carries the avatar scale.
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
		/// Aligns the avatar root on the head during a calibration (same X/Z and same yaw), so the player can line
		/// up their avatar with their body. The Y is left alone, <c>AvatarSyncConnector</c> handles it.
		/// </summary>
		private void ApplyCalibrationRoot() {
			var anchor = GetAnchor()?.transform;
			var head   = GetHeadTransform();
			if (!anchor || !head)
				return;

			var position = anchor.position;
			var headPos  = head.position;
			anchor.position = new Vector3(headPos.x, position.y, headPos.z);

			var forward = head.forward;
			forward.y = 0f;
			if (forward.sqrMagnitude > 0.0001f)
				anchor.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
		}

		/// <summary>Head target of the rig (the one the head bone follows), or <c>null</c>.</summary>
		private Transform GetHeadTransform() {
			var rig = GetRig();
			return rig != null && rig.TryGetPart(HumanBodyBones.Head.ToIndex(), out var part) ? part.GetTransform() : null;
		}

		/// <summary>Gives the rig control of the avatar root, or takes it back.</summary>
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
