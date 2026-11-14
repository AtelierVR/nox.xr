using System.Collections.Generic;
using System.Linq;
using Autohand;
using Nox.Avatars;
using Nox.Avatars.Parameters;
using Nox.Avatars.Rigging;
using Nox.Avatars.Scale;
using Nox.Avatars.StateMachines;
using Nox.CCK.Entities;
using Nox.CCK.Events;
using Nox.Settings;
using Nox.XR.Calibration;
using CalibrationService = Nox.XR.Calibration.Calibration;
using Nox.XR.Runtime.Connectors;
using Nox.XR.Runtime.Settings;
using Nox.XR.Trackers;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Runtime.Tracking {
	[DefaultExecutionOrder(16)]
	public partial class CalibrationManager : MonoBehaviour {
		public static CalibrationManager Instance { get; private set; }

		public AvatarLoaderConnector avatarLoader;
		public AutoHandPlayer         player;

		[Tooltip("Avatar calibrated by this manager when the proxy hosting it has no AvatarLoaderConnector (v2 player rig).")]
		[System.NonSerialized] public IRuntimeAvatar avatar;

		[Tooltip("Root the calibration visuals are parented to when the proxy has no AutoHandPlayer.")]
		public Transform root;

		[Tooltip("Write the calibrated bones directly on the rig. The v2 rig does not need it: its parts follow the calibrated poses themselves.")]
		public bool driveRigParts = true;

		[Tooltip("Align the avatar root on the head while calibrating. Must be off on a proxy whose avatar container already follows the head: the two alignments add up and the avatar slides away.")]
		public bool alignAvatarRoot = true;

		[Tooltip("Pose the head of the avatar must reach while calibrating: the view of the player (eyes). Falls back to the head target of the rig.")]
		public Transform headReference;

		/// <summary>Raised when a calibration starts (true) and when it is saved or cancelled (false).</summary>
		public static readonly NoxEvent<bool> CalibrationStateChanged = new();

		/// <summary>Avatar being calibrated: the loader of the v1 proxy, or the one bound by the v2 proxy.</summary>
		public IRuntimeAvatar Avatar
			=> avatarLoader ? avatarLoader.GetAvatar() : avatar;

		/// <summary>Root the calibration visuals are parented to.</summary>
		private Transform Root
			=> player ? player.transform : root ? root : transform;

		/// <summary>Binds the avatar of a proxy that has no AvatarLoaderConnector.</summary>
		public void Bind(IRuntimeAvatar target, Transform visualsRoot, bool driveParts = true, bool alignRoot = true) {
			avatar        = target;
			root          = visualsRoot;
			driveRigParts = driveParts;
			alignAvatarRoot = alignRoot;
		}

		/// <summary>Forgets the avatar bound to this manager.</summary>
		public void Unbind() {
			avatar = null;
			root   = null;
		}

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

		private CalibrationData _data = new();
		private Transform              _visualsRoot;
		private readonly List<GameObject> _trackerVisuals = new();
		private readonly Dictionary<HumanBodyBones, GameObject> _boneVisuals  = new();
		private readonly Dictionary<HumanBodyBones, GameObject> _rangeVisuals = new();

		public bool IsCalibrating { get; private set; }

		public bool HasCalibration
			=> !_data.IsEmpty;

		/// <summary>

		private void Awake()
			=> Instance = this;

		private void OnEnable() {
			_data = CalibrationStore.Load();
			CalibrationService.Publish(_data, AvatarScale);
		}

		private void OnDisable() {
			DriverTargets.Clear();

			if (IsCalibrating)
				CancelCalibration();
			ClearVisuals();
		}

		private void OnDestroy() {
			DriverTargets.Clear();
			if (driveRigParts)
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
				if (alignAvatarRoot)
					ApplyCalibrationRoot();
				return;
			}

			// The v2 rig follows the calibrated poses through its parts: the scale it must compensate
			// still comes from here.
			if (!driveRigParts || !FullBodyTrackingSetting.Value) {
				DriverTargets.Clear();
				CalibrationService.SetScale(AvatarScale);

				if (driveRigParts)
					ReleaseDrivenBones();

				return;
			}

			TickDriving();
		}



		/// <summary>
		/// Whether trackers flagged as controllers are accepted (see <see cref="AllowControllerFlaggedTrackers"/>).
		/// Consumers that list trackers themselves (e.g. <c>XRFollow</c>) should use the same filter.
		/// </summary>
		public static bool IncludeControllerFlaggedTrackers
			=> AllowControllerFlaggedTrackers;

		/// <summary>
		/// Rig slot driven by a tracker serial according to the stored calibration, or <see cref="EntityRig.None"/>.
		/// <para>
		/// XR loaders only expose tracker ids and poses: the tracker Ã¢â€ â€™ body part link lives in the calibration
		/// data. This is the lookup consumers need to turn a tracker into an <see cref="EntityRig"/>.
		/// </para>
		/// </summary>
		public static bool TryGetRigForTracker(string trackerId, out EntityRig rig) {
			if (!Instance) {
				rig = EntityRig.None;
				return false;
			}

			return Instance._data.TryGetRig(trackerId, out rig);
		}

		/// <summary>Tracker serial driving a rig slot according to the stored calibration.</summary>
		public static bool TryGetTrackerForRig(EntityRig rig, out string trackerId) {
			if (!Instance) {
				trackerId = null;
				return false;
			}

			return Instance._data.TryGetTrackerId(rig, out trackerId);
		}

		/// <summary>Calibration entry (tracker id + offsets) driving a rig slot.</summary>
		public static bool TryGetBinding(EntityRig rig, out TrackerBinding binding) {
			if (!Instance) {
				binding = null;
				return false;
			}

			return Instance._data.TryGetBinding(rig, out binding);
		}

		/// <summary>Whether a tracker is already assigned to an <see cref="EntityRig"/> by the calibration.</summary>
		public static bool IsTrackerClaimed(string trackerId) {
			if (!Instance || string.IsNullOrEmpty(trackerId))
				return false;

			return Instance._data.TryGetRig(trackerId, out _);
		}

		/// <summary>Avatar scale used to compensate the stored calibration offsets.</summary>
		public static float CurrentAvatarScale
			=> Instance ? Instance.AvatarScale : 1f;

		/// <summary>Starts a new calibration session (idempotent).</summary>
		public void StartCalibration() {
			if (IsCalibrating || _restartCooldown > 0f)
				return;
			if (!IsXRControllerActive) {
				Logger.LogWarning("Full-body calibration is only available while the XR controller is active.", this, tag: nameof(CalibrationManager));
				return;
			}

			if (!TrackerResolver.HasAvailableProvider) {
				Logger.LogWarning("Calibration is unavailable: no XR loader exposes a tracker provider.", this, tag: nameof(CalibrationManager));
				return;
			}

			IsCalibrating = true;
			_matches.Clear();
			_validationLatch   = false;
			_validationArmedIn = ValidationArmDelay;

			SetPose(AvatarPose.Calibration);
			SetExternalRootControl(true);
			SetReferencePose(true);

			CreateVisuals();
			CalibrationStateChanged.Invoke(true);
			NotifySettingsMenu();
			Logger.Log($"Full-body calibration started (pose {AvatarPose.Calibration}).", this, tag: nameof(CalibrationManager));
		}

		/// <summary>Applies the current matches, persists them and leaves calibration mode.</summary>
		public void ConfirmCalibration() {
			if (!IsCalibrating)
				return;

			IsCalibrating = false;
			_restartCooldown = RestartCooldown;
			CaptureMatches();
			CalibrationStore.Save(_data);
			CalibrationService.Publish(_data, AvatarScale);

			ClearVisuals();
			SetReferencePose(false);
			SetPose(AvatarPose.Normal);
			if (driveRigParts)
				SetExternalRootControl(false);
			CalibrationStateChanged.Invoke(false);
			NotifySettingsMenu();
			Logger.Log($"Full-body calibration saved ({_data.Bindings.Length} tracker(s)).", this, tag: nameof(CalibrationManager));
		}

		/// <summary>Cancels calibration without saving; the previously stored calibration is kept.</summary>
		public void CancelCalibration() {
			if (!IsCalibrating)
				return;

			IsCalibrating = false;
			_restartCooldown = RestartCooldown;
			_matches.Clear();
			ClearVisuals();
			SetReferencePose(false);
			SetPose(AvatarPose.Normal);
			_data = CalibrationStore.Load();
			CalibrationService.Publish(_data, AvatarScale);
			if (driveRigParts)
				SetExternalRootControl(false);
			CalibrationStateChanged.Invoke(false);
			NotifySettingsMenu();
		}

		/// <summary>Forgets the stored calibration.</summary>
		public void ClearCalibration() {
			_data = new CalibrationData();
			CalibrationStore.Clear();
			CalibrationService.Publish(_data, AvatarScale);
			CalibrationStateChanged.Invoke(false);
			NotifySettingsMenu();
		}

		/// <summary>Refreshes the settings menu: the calibration button label follows the calibration state.</summary>
		private static void NotifySettingsMenu()
			=> SettingsNotifier.NotifyUpdated(null);


		/// <summary>Calibration range, grown by the avatar scale so a scaled avatar keeps a usable window.</summary>
		private float EffectiveRange
			=> CalibrationRangeSetting.Value * Mathf.Max(0.01f, AvatarScale);

		/// <summary>
		/// Places the avatar under the player while calibrating: the head bone lands on the reference (the eyes, see
		/// <see cref="headReference"/>) and the body takes its yaw.
		/// <para>
		/// The body is in its reference pose (straight legs and spine, see <see cref="SetReferencePose"/>) and the
		/// avatar is usually taller than the player, so the root has to sink for the head to reach the eyes: the feet
		/// then go under the floor. That is the point, the pose has to stay the same for every avatar and the player
		/// cannot be stretched, and the offsets captured from it are measured in that pose.
		/// </para>
		/// <para>
		/// The head bone offset in the root frame is constant while the body holds its pose, so subtracting it lands the
		/// head on the reference and keeps it there, instead of adding the offset on every frame (which made the avatar
		/// slide away).
		/// </para>
		/// </summary>
		private void ApplyCalibrationRoot() {
			var anchor = GetAnchor()?.transform;
			if (!anchor)
				return;

			var head      = GetRig()?.GetBone(HumanBodyBones.Head);
			var reference = headReference ? headReference : GetHeadTransform();
			if (!reference)
				return;

			var forward = reference.forward;
			forward.y = 0f;
			if (forward.sqrMagnitude > 0.0001f)
				anchor.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);

			var position = anchor.position;
			if (!head) {
				anchor.position = new Vector3(reference.position.x, position.y, reference.position.z);
				return;
			}

			var offset = anchor.TransformVector(anchor.InverseTransformPoint(head.position));
			anchor.position = new Vector3(reference.position.x - offset.x, reference.position.y - offset.y, reference.position.z - offset.z);
		}

		/// <summary>Head target of the rig (the one the head bone follows), or <c>null</c>.</summary>
		private Transform GetHeadTransform() {
			var rig = GetRig();
			return rig != null && rig.TryGetPart(HumanBodyBones.Head.ToIndex(), out var part) ? part.GetTransform() : null;
		}

		/// <summary>Gives the rig control of the avatar root, or takes it back.</summary>
		private void SetExternalRootControl(bool external)
			=> GetRig()?.SetExternalRootControl(external);

		/// <summary>
		/// Asks the rig to hold the reference pose of a calibration: body upright, arms and legs straight, feet on the
		/// ground. The limbs are handed back to the animation during a calibration, so the pose is written on the bones
		/// by the rig itself instead of going through the IK targets.
		/// </summary>
		private void SetReferencePose(bool enabled) {
			var rig = GetRig();
			if (rig != null)
				rig.ReferencePose = enabled;
		}

		private GameObject GetAnchor()
			=> Avatar?.Descriptor?.Anchor;

		private float AvatarScale
			=> Avatar?.Descriptor?
				.GetModules<IScaleAvatarModule>()
				.FirstOrDefault()?.Scale ?? 1f;

		private IRigging GetRig()
			=> Avatar?.Descriptor
				?.GetModules<IRigProvider>()
				.FirstOrDefault()
				?.Rig;

		private IPlayableLayerModule GetPlayableLayers()
			=> Avatar?.Descriptor?
				.GetModules<IPlayableLayerModule>()
				.FirstOrDefault();

		private IParameterModule GetParameters()
			=> Avatar?.Descriptor?
				.GetModules<IParameterModule>()
				.FirstOrDefault();

	}
}
