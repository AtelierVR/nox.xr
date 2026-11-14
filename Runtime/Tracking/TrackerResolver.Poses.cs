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

		/// <summary>
		/// Resolve the current pose of a rig slot, including custom ids (prop trackers...), in <b>world</b> space and
		/// with the calibration applied.
		/// <para>
		/// The calibration offsets are measured on the avatar skeleton in world space, so they are composed here, once
		/// the raw pose was brought to world space: applied inside the tracking space (as the loader sees it) they
		/// would be turned by the player and stretched by the avatar fit, and the pelvis would jump the moment the
		/// calibration is confirmed.
		/// </para>
		/// </summary>
		public static bool TryGetPose(ushort rig, out TrackerPose pose) {
			// Loader-owned trackers first (HMD, controllers...): each runtime knows its own devices.
			foreach (var trackers in Providers()) {
				if (trackers == null || !trackers.IsAvailable)
					continue;

				trackers.Poll();
				if (trackers.TryGetPose(rig, TrackerPoseMode.Calibrated, out pose)) {
					pose = ToWorld(rig, pose);
					return true;
				}
			}

			var knownRig = rig.ToEntityRig();
			if (knownRig != EntityRig.None && TryGetCalibrated(knownRig, out pose))
				return true;

			RefreshTrackers();
			return TryGetFreeTracker(0, out pose);
		}

		/// <summary>
		/// Brings a raw pose of a loader (tracking space) to world space, then applies the calibration of its rig slot.
		/// </summary>
		private static TrackerPose ToWorld(ushort rig, TrackerPose pose) {
			var origin = TrackingSpace();
			if (origin) {
				pose.Position        = origin.TransformPoint(pose.Position * PoseScale);
				pose.Rotation        = origin.rotation * pose.Rotation;
				pose.Velocity        = origin.rotation * pose.Velocity;
				pose.AngularVelocity = origin.rotation * pose.AngularVelocity;
			}

			if (!CalibrationService.IsCalibratable(rig) || !CalibrationService.TryGetBinding(rig.ToEntityRig(), out var binding))
				return pose;

			CalibrationService.Apply(binding, pose.Position, pose.Rotation, out var position, out var rotation);
			pose.Position = position;
			pose.Rotation = rotation;
			return pose;
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

			CalibrationService.Apply(binding, tracker.Position, tracker.Rotation, out var position, out var rotation);

			pose = new TrackerPose {
				Id = tracker.Id,
				Position = position,
				Rotation = rotation,
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

		/// <summary>Reads tracker poses once per frame, shared by every caller.</summary>
		private static void RefreshTrackers() {
			if (_trackersFrame == Time.frameCount)
				return;

			_trackersFrame = Time.frameCount;
			Get(Trackers, excludeHanded: true, excludeControllers: !CalibrationManager.IncludeControllerFlaggedTrackers);
		}

	}
}
