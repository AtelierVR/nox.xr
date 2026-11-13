using Nox.XR.Bindings;
using Nox.XR.Trackers;

namespace Nox.XR.Loaders {
	/// <summary>
	/// Optional features provided by an XR loader.
	/// <para>
	/// Inputs and trackers are separate concerns: a loader only fills the ones it actually
	/// implements. Trackers cover every node the runtime can resolve (HMD, controllers, hardware
	/// trackers...) through <see cref="ITracker"/>. Grouping them keeps
	/// <see cref="IXRLoaderProvider"/> focused on lifecycle/selection.
	/// </para>
	/// </summary>
	public sealed class XRLoaderFeatures {
		/// <summary>Input bindings of this runtime, or <c>null</c>.</summary>
		public IBinding Binding { get; set; }

		/// <summary>Trackers exposed by this runtime, or <c>null</c>.</summary>
		public ITrackerProvider Trackers { get; set; }
	}
}
