using System.Collections.Generic;
using System.Linq;
using Autohand;
using Nox.Avatars.Parameters;
using Nox.Avatars.Rigging;
using Nox.Avatars.Scale;
using Nox.Avatars.StateMachines;
using Nox.CCK.Avatars.Playable;
using Nox.CCK.Avatars.Rigging;
using Nox.CCK.Entities;
using Nox.CCK.Utils;
using Nox.CCK.XR;
using Nox.Settings;
using Nox.XR.Calibration;
using CalibrationService = Nox.XR.Calibration.Calibration;
using Nox.XR.Runtime.Connectors;
using Nox.XR.Runtime.Settings;
using Nox.XR.Trackers;
using UnityEngine;
using UnityEngine.XR;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Runtime.Tracking {
	[DefaultExecutionOrder(16)]
	public partial class CalibrationManager : MonoBehaviour {
		public static CalibrationManager Instance { get; private set; }

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

			CreateVisuals();
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
			SetPose(AvatarPose.Normal);
			SetExternalRootControl(false);
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
			SetPose(AvatarPose.Normal);
			_data = CalibrationStore.Load();
			CalibrationService.Publish(_data, AvatarScale);
			SetExternalRootControl(false);
			NotifySettingsMenu();
		}

		/// <summary>Forgets the stored calibration.</summary>
		public void ClearCalibration() {
			_data = new CalibrationData();
			CalibrationStore.Clear();
			CalibrationService.Publish(_data, AvatarScale);
			NotifySettingsMenu();
		}

		/// <summary>Refreshes the settings menu: the calibration button label follows the calibration state.</summary>
		private static void NotifySettingsMenu()
			=> SettingsNotifier.NotifyUpdated(null);


		/// <summary>Calibration range, grown by the avatar scale so a scaled avatar keeps a usable window.</summary>
		private float EffectiveRange
			=> CalibrationRangeSetting.Value * Mathf.Max(0.01f, AvatarScale);

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
			=> avatarLoader?.GetAvatar()?.Descriptor
				?.GetModules<IRigProvider>()
				.FirstOrDefault()
				?.Rig;

		private IPlayableLayerModule GetPlayableLayers()
			=> avatarLoader?.GetAvatar()?.Descriptor?
				.GetModules<IPlayableLayerModule>()
				.FirstOrDefault();

		private IParameterModule GetParameters()
			=> avatarLoader?.GetAvatar()?.Descriptor?
				.GetModules<IParameterModule>()
				.FirstOrDefault();

	}
}
