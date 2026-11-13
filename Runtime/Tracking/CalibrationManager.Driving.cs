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

		private void TickDriving() {
			DriverTargets.Clear();

			if (_data.IsEmpty)
				return;

			var rig = GetRig();
			if (rig == null)
				return;

			TrackerResolver.Get(_trackers, excludeHanded: true, excludeControllers: !AllowControllerFlaggedTrackers);

			var scale = AvatarScale;
			CalibrationService.SetScale(scale);

			foreach (var binding in _data.Bindings) {
				var bone  = binding.Rig.ToHumanBodyBones();
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
					Logger.LogWarning($"Full-body tracking: no IK target for {bone} on the rig, this bone will not follow its tracker.", this, tag: nameof(CalibrationManager));
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

		/// <summary>Mirrors the state of the driven bones on the avatar's <c>Tracking/*/Active</c> parameters.</summary>
		private void SetTrackingActive(HumanBodyBones bone, bool active) {
			var parameters = GetParameters();
			if (parameters == null)
				return;

			var parameter = parameters.GetParameter($"Tracking/{bone}/Active");
			if (parameter != null)
				parameter.Value = active;
		}

	}
}
