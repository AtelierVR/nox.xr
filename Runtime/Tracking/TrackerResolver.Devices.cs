using System.Collections.Generic;
using Nox.CCK.XR;
using Nox.XR.Trackers;
using UnityEngine;

namespace Nox.XR.Runtime.Tracking {
	public static partial class TrackerResolver {
		/// <summary>Whether at least one loader exposes a usable tracker provider.</summary>
		public static bool HasAvailableProvider {
			get {
				var providers = Providers();
				for (var i = 0; i < providers.Count; i++) {
					if (providers[i] != null && providers[i].IsAvailable)
						return true;
				}

				return false;
			}
		}

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
