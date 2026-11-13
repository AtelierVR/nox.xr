using System.Collections.Generic;
using Nox.CCK.Entities;
using Nox.CCK.XR;
using CalibrationService = Nox.XR.Calibration.Calibration;
using Nox.XR.Runtime.Loaders;
using Nox.XR.Trackers;
using UnityEngine;


namespace Nox.XR.Runtime.Tracking {
	public static partial class TrackerResolver {
		public static bool TryGetPose(EntityRig rig, out TrackerPose pose)
			=> TryGetPose(rig.ToIndex(), out pose);

		/// <summary>Resolve the current pose of a rig slot, including custom ids (prop trackers...).</summary>
		public static bool TryGetPose(ushort rig, out TrackerPose pose) {
			// Loader-owned trackers first (HMD, controllers...): each runtime knows its own devices.
			foreach (var trackers in Providers()) {
				if (trackers == null || !trackers.IsAvailable)
					continue;

				trackers.Poll();
				if (trackers.TryGetPose(rig, TrackerPoseMode.Calibrated, out pose))
					return true;
			}

			var knownRig = rig.ToEntityRig();
			if (knownRig != EntityRig.None && TryGetCalibrated(knownRig, out pose))
				return true;

			RefreshTrackers();
			return TryGetFreeTracker(0, out pose);
		}

		/// <summary>Resolve a free (unclaimed) tracker by serial.</summary>
		public static bool TryGetFreeTracker(string trackerId, out TrackerPose pose) {
			RefreshTrackers();

			if (!string.IsNullOrEmpty(trackerId)) {
				for (var i = 0; i < Trackers.Count; i++) {
					if (!string.Equals(Trackers[i].Id, trackerId, System.StringComparison.OrdinalIgnoreCase))
						continue;

					pose = Trackers[i];
					return true;
				}
			}

			pose = default;
			return false;
		}

		/// <summary>Resolve the n-th free (unclaimed) tracker.</summary>
		public static bool TryGetFreeTracker(int index, out TrackerPose pose) {
			RefreshTrackers();

			var current = 0;
			for (var i = 0; i < Trackers.Count; i++) {
				if (CalibrationService.IsTrackerClaimed(Trackers[i].Id))
					continue;

				if (current++ != index)
					continue;

				pose = Trackers[i];
				return true;
			}

			pose = default;
			return false;
		}

		/// <summary>
		/// Fills <paramref name="into"/> with every tracker of every available provider, in world space.
		/// <para>
		/// Trackers are deduplicated by serial across providers, in reading order: a tracker one
		/// provider already reported is never added a second time by another one.
		/// </para>

		private static bool TryGetCalibrated(EntityRig rig, out TrackerPose pose) {
			pose = default;

			if (!CalibrationService.IsCalibratable(rig) || !CalibrationService.TryGetBinding(rig, out var binding))
				return false;

			RefreshTrackers();
			if (!TryFindTracker(binding.TrackerId, out var tracker))
				return false;

			var offset = tracker.Rotation * (binding.OffsetPosition * (binding.Scale > 0f ? CalibrationService.CurrentScale / binding.Scale : 1f));

			// Hips follow the tracker yaw only, so tilting the tracker does not move the pelvis vertically.
			if (rig == EntityRig.Hips && YawOf(tracker.Rotation) is { } yaw)
				offset = yaw * (binding.OffsetPosition * (binding.Scale > 0f ? CalibrationService.CurrentScale / binding.Scale : 1f));

			pose = new TrackerPose {
				Id = tracker.Id,
				Position = tracker.Position + offset,
				Rotation = tracker.Rotation * binding.OffsetRotation,
				Velocity = tracker.Velocity,
				AngularVelocity = tracker.AngularVelocity
			};
			return true;
		}

		private static bool TryFindTracker(string id, out TrackerPose tracker) {
			if (!string.IsNullOrEmpty(id)) {
				for (var i = 0; i < Trackers.Count; i++) {
					if (!string.Equals(Trackers[i].Id, id, System.StringComparison.OrdinalIgnoreCase))
						continue;

					tracker = Trackers[i];
					return true;
				}
			}

			tracker = default;
			return false;
		}

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

		/// <summary>Reads tracker poses once per frame, shared by every caller.</summary>
		private static void RefreshTrackers() {
			if (_trackersFrame == Time.frameCount)
				return;

			_trackersFrame = Time.frameCount;
			Get(Trackers, excludeHanded: true, excludeControllers: !CalibrationManager.IncludeControllerFlaggedTrackers);
		}

	}
}
