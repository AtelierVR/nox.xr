using System.Collections.Generic;
using Nox.CCK.Entities;
using Nox.CCK.XR;
using CalibrationService = Nox.XR.Calibration.Calibration;
using Nox.XR.Runtime.Loaders;
using Nox.XR.Trackers;
using UnityEngine;


namespace Nox.XR.Runtime.Tracking {
	/// <summary>
	/// Tracker resolution. Loaders expose their trackers (HMD, controllers, hardware trackers)
	/// through <see cref="ITrackerProvider"/>; without any available provider there are no trackers,
	/// hence no calibration. Poses are cached once per frame and deduplicated by serial.
	/// </summary>
	public static partial class TrackerResolver {
		private static readonly List<TrackerPose> Trackers = new();
		private static int _trackersFrame = -1;

		/// <summary>Providers exposed by the loaded XR loaders, in loader order.</summary>
		public static List<ITrackerProvider> Providers() {
			var providers = new List<ITrackerProvider>();

			foreach (var loader in XRLoaderManager.Providers) {
				ITrackerProvider trackers;
				try {
					trackers = loader?.Features?.Trackers;
				} catch (System.Exception) {
					// A third-party mod must not break tracker reads from the other loaders.
					continue;
				}

				if (trackers != null && providers.TrueForAll(p => p.Id != trackers.Id))
					providers.Add(trackers);
			}

			return providers;
		}
	}
}
