using System.Collections.Generic;
using Nox.Avatars.Rigging;
using Nox.CCK.Avatars.Rigging;
using Nox.CCK.Entities;
using Nox.CCK.Utils;
using Nox.XR.Calibration;
using CalibrationService = Nox.XR.Calibration.Calibration;
using Nox.XR.Trackers;
using UnityEngine;
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

			(Vector3 Position, Quaternion Rotation, Vector3 Velocity, Vector3 Angular)? hips = null;

			foreach (var binding in _data.Bindings) {
				var bone  = binding.Rig.ToHumanBodyBones();
				var found = FindTracker(binding.TrackerId);

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

				rig.SetTracking(bone, RiggingTrackingMode.Tracking);
				rig.SetActive(bone, true);
				SetTrackingActive(bone, true);

				var tracker = found.Value;

				CalibrationService.Apply(binding, tracker.Position, tracker.Rotation, out var position, out var rotation);

				DriverTargets[bone.ToIndex()] = (position, rotation, tracker.Velocity, tracker.AngularVelocity);

				if (RigPartDriver.Write(rig, bone.ToIndex(), position, rotation))
					_missingParts.Remove(bone);
				else if (_missingParts.Add(bone))
					Logger.LogWarning($"Full-body tracking: no IK target for {bone} on the rig, this bone will not follow its tracker.", this, tag: nameof(CalibrationManager));

				if (bone == HumanBodyBones.Hips) {
					DrivePelvisWeight(rig, binding, tracker);
					hips = DriverTargets[bone.ToIndex()];
				}
			}

			if (hips is { } pelvis)
				ClampPelvis(rig, pelvis);
		}

		private const float SpineMinRatio = 0.85f;

		private static void DrivePelvisWeight(IRigging rig, TrackerBinding binding, TrackerPose tracker) {
			var weight = CalibrationService.PelvisPositionWeight(binding, tracker.Rotation);

			foreach (var parameter in rig.GetParameters()) {
				if (parameter.Name != "Rig/Ik/Hips/PositionWeight")
					continue;

				parameter.Value = weight;
				return;
			}
		}
		private void ReleaseDrivenBones() {
			var rig = GetRig();
			if (rig == null)
				return;

			foreach (var binding in _data.Bindings) {
				var bone = binding.Rig.ToHumanBodyBones();

				rig.SetTracking(bone, RiggingTrackingMode.Normal);

				if (rig.IsActive(bone))
					rig.SetActive(bone, false);

				SetTrackingActive(bone, false);

				var reference = ReferenceBone(rig, bone);
				if (reference)
					RigPartDriver.Write(rig, bone.ToIndex(), reference.position, reference.rotation);
			}
		}
		private void ClampPelvis(IRigging rig, (Vector3 Position, Quaternion Rotation, Vector3 Velocity, Vector3 Angular) pelvis) {
			var position = LimitToLegReach(rig, pelvis.Position);
			position = KeepSpineLength(rig, position);

			if ((position - pelvis.Position).sqrMagnitude < 0.000001f)
				return;

			RigPartDriver.Write(rig, HumanBodyBones.Hips.ToIndex(), position, pelvis.Rotation);
			DriverTargets[HumanBodyBones.Hips.ToIndex()] = (position, pelvis.Rotation, pelvis.Velocity, pelvis.Angular);
		}

		private static Vector3 LimitToLegReach(IRigging rig, Vector3 position) {
			if (!RigPartDriver.TryGetPart(rig, HumanBodyBones.LeftFoot.ToIndex(), out var left) || !RigPartDriver.TryGetPart(rig, HumanBodyBones.RightFoot.ToIndex(), out var right))
				return position;

			var reach = LegReach(rig);
			if (reach <= 0.01f)
				return position;

			var feet     = (left.position + right.position) * 0.5f;
			var offset   = position - feet;
			var distance = offset.magnitude;

			return distance > reach ? feet + offset * (reach / distance) : position;
		}

		private static Vector3 KeepSpineLength(IRigging rig, Vector3 position) {
			if (!RigPartDriver.TryGetPart(rig, HumanBodyBones.Head.ToIndex(), out var head))
				return position;

			var min = SpineLength(rig) * SpineMinRatio;
			if (min <= 0.01f)
				return position;

			var offset   = position - head.position;
			var distance = offset.magnitude;

			if (distance >= min || distance < 0.0001f)
				return position;

			return head.position + offset * (min / distance);
		}

		private static float SpineLength(IRigging rig) {
			var hips  = rig.GetBone(HumanBodyBones.Hips);
			var spine = rig.GetBone(HumanBodyBones.Spine);
			var chest = rig.GetBone(HumanBodyBones.Chest);
			var neck  = rig.GetBone(HumanBodyBones.Neck);
			var head  = rig.GetBone(HumanBodyBones.Head);

			if (!hips || !spine || !head || !chest || !neck)
				return 0f;

			return Vector3.Distance(hips.position, spine.position)
			       + Vector3.Distance(spine.position, chest.position)
			       + Vector3.Distance(chest.position, neck.position)
			       + Vector3.Distance(neck.position, head.position);
		}

		private static float LegReach(IRigging rig) {
			var thigh = rig.GetBone(HumanBodyBones.LeftUpperLeg);
			var calf  = rig.GetBone(HumanBodyBones.LeftLowerLeg);
			var foot  = rig.GetBone(HumanBodyBones.LeftFoot);
			var toes  = rig.GetBone(HumanBodyBones.LeftToes);

			if (!thigh || !calf || !foot)
				return 0f;

			var reach = Vector3.Distance(thigh.position, calf.position) + Vector3.Distance(calf.position, foot.position);

			return toes ? reach + Vector3.Distance(foot.position, toes.position) : reach;
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
