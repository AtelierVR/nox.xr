using System.Collections.Generic;
using Nox.CCK.XR;
using Nox.XR.Runtime.Loaders;
using Nox.XR.Trackers;
using UnityEngine;
using UnityEngine.XR;

namespace Nox.XR.Runtime.FullBody {
	/// <summary>
	/// Trackers used by full-body tracking. They come from the XR loaders of every loaded mod (deduplicated by
	/// serial), then from the generic <see cref="XRInputDevicesTrackerProvider"/> fallback.
	/// </summary>
	public static class FullBodyTrackers {
		/// <summary>Providers read in order: known loaders first, then the generic XR device fallback.</summary>
		public static List<ITrackerProvider> Providers() {
			var providers = new List<ITrackerProvider>();

			foreach (var loader in XRLoaderManager.Providers) {
				ITrackerProvider trackers;
				try {
					trackers = loader?.Trackers;
				} catch (System.Exception) {
					// Un mod tiers ne doit pas casser la lecture des trackers des autres.
					continue;
				}

				if (trackers != null && providers.TrueForAll(p => p.Id != trackers.Id))
					providers.Add(trackers);
			}

			providers.Add(XRInputDevicesTrackerProvider.Instance);
			return providers;
		}

		/// <summary>Whether <paramref name="device"/> can be used as a full-body tracker, and why not when it cannot.</summary>
		public static bool IsCandidate(InputDevice device, bool excludeHanded, bool excludeControllers, out string reason)
			=> XRInputDevicesTrackerProvider.IsCandidate(device, excludeHanded, excludeControllers, out reason);

		/// <summary>Same as <see cref="IsCandidate(InputDevice, bool, bool, out string)"/> with controllers excluded.</summary>
		public static bool IsCandidate(InputDevice device, bool excludeHanded, out string reason)
			=> IsCandidate(device, excludeHanded, true, out reason);

		/// <summary>
		/// Space the XR device poses are expressed in: the camera floor offset of the <see cref="XROrigin"/>
		/// (the object the XR camera is a child of), not the rig root, which sits anywhere in the world.
		/// </summary>
		public static Transform TrackingSpace() {
			var origin = XROriginSetter.GlobalOrigin;
			if (!origin)
				return null;

			return origin.CameraFloorOffsetObject
				? origin.CameraFloorOffsetObject.transform
				: origin.transform;
		}

		/// <summary>
		/// Fills <paramref name="into"/> with every tracker of every available provider, in world space.
		/// <para>
		/// Trackers are deduplicated by serial across providers, in reading order: a tracker one
		/// provider already reported is never added a second time by another one.
		/// </para>
		/// </summary>
		public static void Get(List<TrackerPose> into, bool excludeHanded = true, bool excludeControllers = true) {
			into.Clear();

			var origin = TrackingSpace();

			foreach (var provider in Providers()) {
				if (provider == null || !provider.IsAvailable)
					continue;

				var start = into.Count;
				provider.Get(into, excludeHanded, excludeControllers);
				RemoveDuplicates(into, start);
				ToWorldSpace(into, start, origin);
			}
		}

		/// <summary>
		/// Brings the entries from <paramref name="from"/> to world space: providers publish their runtime
		/// tracking space, the XR origin belongs to nox.xr.
		/// </summary>
		private static void ToWorldSpace(List<TrackerPose> trackers, int from, Transform origin) {
			if (!origin)
				return;

			for (var i = from; i < trackers.Count; i++) {
				var tracker = trackers[i];
				tracker.Position = origin.TransformPoint(tracker.Position);
				tracker.Rotation = origin.rotation * tracker.Rotation;
				// Velocities are tracking-space directions: rotate them, do not translate.
				tracker.Velocity        = origin.rotation * tracker.Velocity;
				tracker.AngularVelocity = origin.rotation * tracker.AngularVelocity;
				trackers[i]             = tracker;
			}
		}

		/// <summary>Drops the entries from <paramref name="from"/> already present earlier in the list (same serial).</summary>
		private static void RemoveDuplicates(List<TrackerPose> trackers, int from) {
			for (var i = trackers.Count - 1; i >= from; i--) {
				var id = trackers[i].Id;
				if (string.IsNullOrEmpty(id))
					continue;

				for (var j = 0; j < i; j++) {
					if (!string.Equals(trackers[j].Id, id, System.StringComparison.OrdinalIgnoreCase))
						continue;

					trackers.RemoveAt(i);
					break;
				}
			}
		}
	}
}
