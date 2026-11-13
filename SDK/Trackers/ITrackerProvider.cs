using System;
using System.Collections.Generic;
using Nox.CCK.Events;

namespace Nox.XR.Trackers {
	/// <summary>
	/// Trackers exposed by an XR loader, plus the free trackers of the generic XR device source.
	/// <para>
	/// A loader decides which trackers it can resolve (HMD, controllers, hardware trackers...),
	/// owns them as <see cref="ITracker"/> objects and raises lifecycle events so nox.xr can react
	/// without polling every device. The same interface is implemented by the internal generic
	/// fallback, which reads the <c>InputDevice</c> list of the active runtime.
	/// </para>
	/// </summary>
	public interface ITrackerProvider : IDisposable {
		/// <summary>Loader id (e.g. <c>openxr</c>, <c>openvr</c>), used for logs and diagnostics.</summary>
		string Id { get; }

		/// <summary>Whether this provider can answer right now (loader started, session open...).</summary>
		bool IsAvailable { get; }

		/// <summary>One-line state, for diagnostics (panel, operators, terminal).</summary>
		string Describe();

		/// <summary>Trackers currently exposed by this provider.</summary>
		IReadOnlyList<ITracker> Trackers { get; }

		/// <summary>Find a tracker by rig slot (an <c>EntityRig</c> value or a custom id).</summary>
		bool TryGet(ushort rig, out ITracker tracker);

		/// <summary>Find a tracker by its stable identifier (<see cref="ITracker.Id"/>).</summary>
		bool TryGet(string id, out ITracker tracker);

		/// <summary>Find a tracker by rig slot and read its pose in the requested mode.</summary>
		bool TryGetPose(ushort rig, TrackerPoseMode mode, out TrackerPose pose);

		/// <summary>Find a tracker by its stable identifier and read its pose in the requested mode.</summary>
		bool TryGetPose(string id, TrackerPoseMode mode, out TrackerPose pose);

		/// <summary>Polls tracking states and raises <see cref="OnTracking"/> on changes.</summary>
		void Poll();

		/// <summary>
		/// Adds the free trackers usable by full-body tracking to <paramref name="into"/>.
		/// </summary>
		/// <param name="excludeHanded">Skip devices currently used as hands.</param>
		/// <param name="excludeControllers">Skip devices the runtime flags as controllers.</param>
		void Get(List<TrackerPose> into, bool excludeHanded, bool excludeControllers);

		/// <summary>A tracker was added.</summary>
		NoxEvent<ITracker> OnRegistered { get; }

		/// <summary>A tracker was removed/disconnected.</summary>
		NoxEvent<ITracker> OnUnregistered { get; }

		/// <summary>A tracker started or stopped tracking.</summary>
		NoxEvent<ITracker, bool> OnTracking { get; }
	}
}
