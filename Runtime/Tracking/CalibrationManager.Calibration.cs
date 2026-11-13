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
	
	public partial class CalibrationManager : MonoBehaviour {

		private void TickCalibration() {
			var rig = GetRig();
			if (rig == null)
				return;

			TrackerResolver.Get(_trackers, excludeHanded: true, excludeControllers: !AllowControllerFlaggedTrackers);
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
			var list  = new List<TrackerBinding>();

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
					Logger.LogWarning($"Calibration: {pair.Key} est ÃƒÂ  {offset.magnitude:0.##} m de {tracker.Id} Ã¢â‚¬â€ la pose de calibration semble fausse (l'avatar doit ÃƒÂªtre en pose {AvatarPose.Calibration} pendant la calibration), l'offset risque d'ÃƒÂªtre inutilisable.", this, tag: nameof(CalibrationManager));

				list.Add(new TrackerBinding {
					TrackerId      = tracker.Id,
					Rig            = pair.Key.ToIndex(),
					OffsetPosition = offset,
					OffsetRotation = inverse * boneTransform.rotation,
					Scale          = scale
				});
			}

			_data = new CalibrationData { Bindings = list.ToArray() };
		}

	}
}
