using System.Collections.Generic;
using System.Text;
using Nox.CCK.Entities;
using Nox.CCK.Events;

namespace Nox.XR.Trackers {
	/// <summary>Reads a tracker pose in the requested mode.</summary>
	public delegate bool TryReadTrackerPose(TrackerPoseMode mode, out TrackerPose pose);

	/// <summary>Reads the live tracking state of a tracker.</summary>
	public delegate bool ReadTrackerTracking();

	/// <summary>Reads the hand skeleton of a tracker (see <see cref="TrackerFeatures.Skeleton"/>).</summary>
	public delegate bool ReadTrackerSkeleton(out HandSkeleton skeleton);

	/// <summary>
	/// Reads the live features of a tracker. They are not fixed for the life of a device: a runtime can start
	/// serving hand data (the hand subsystem starts after the devices appear) or stop doing it.
	/// </summary>
	public delegate TrackerFeatures ReadTrackerFeatures();

	/// <summary>Reads the physical connection state of a tracker.</summary>
	public delegate bool ReadTrackerConnected();

	/// <summary>Reads a tracker value by key (see <see cref="TrackerKeys"/>); value is boxed.</summary>
	public delegate bool TryReadTrackerValue(string key, out object value);

	/// <summary>Reads the rig slot a tracker currently drives (it can change after calibration).</summary>
	public delegate ushort ReadTrackerRig();

	/// <summary>Simple <see cref="ITracker"/> backed by delegates supplied by a loader.</summary>
	public sealed class TrackerEntry : ITracker {
		private readonly TryReadTrackerPose _pose;
		private readonly ReadTrackerTracking _tracking;
		private readonly ReadTrackerConnected _connected;
		private readonly TryReadTrackerValue _value;
		private readonly ReadTrackerRig _rig;
		private readonly ReadTrackerSkeleton _skeleton;
		private readonly ReadTrackerFeatures _readFeatures;
		private readonly TrackerFeatures _features;
		private readonly string _id;
		private readonly string _name;
		private readonly string _manufacturer;
		private readonly string _model;

		public TrackerEntry(
			ReadTrackerRig readRig,
			TrackerFeatures features,
			TryReadTrackerPose readPose,
			ReadTrackerTracking readTracking,
			string id = null,
			string name = null,
			string manufacturer = null,
			string model = null,
			ReadTrackerConnected readConnected = null,
			TryReadTrackerValue readValue = null,
			ReadTrackerSkeleton readSkeleton = null,
			ReadTrackerFeatures readFeatures = null) {
			_rig = readRig;
			_features = features;
			_readFeatures = readFeatures;
			_pose = readPose;
			_tracking = readTracking;
			_connected = readConnected;
			_value = readValue;
			_skeleton = readSkeleton;
			_id = id;
			_name = name;
			_manufacturer = manufacturer;
			_model = model;
		}

		public ushort Rig
			=> _rig?.Invoke() ?? EntityRig.None.ToIndex();

		public string Id
			=> string.IsNullOrEmpty(_id) ? Rig.ToString() : _id;

		public string Name
			=> string.IsNullOrEmpty(_name) ? Id : _name;

		public string Manufacturer
			=> _manufacturer ?? string.Empty;

		public string Model
			=> string.IsNullOrEmpty(_model) ? Name : _model;

		public bool IsConnected
			=> _connected?.Invoke() ?? true;

		public TrackerFeatures Features
			=> _readFeatures != null
				? _readFeatures()
				: _features;

		public bool IsTracking
			=> _tracking?.Invoke() ?? false;

		public T Get<T>(string key) where T : struct {
			if (_value != null && _value(key, out var raw) && raw is T typed)
				return typed;

			return default;
		}

		public bool TryGetPose(TrackerPoseMode mode, out TrackerPose pose) {
			if (_pose == null) {
				pose = default;
				return false;
			}

			return _pose(mode, out pose);
		}

		public bool TryGetSkeleton(out HandSkeleton skeleton) {
			if (_skeleton == null) {
				skeleton = default;
				return false;
			}

			return _skeleton(out skeleton);
		}

		public string Describe()
			=> $"{Name} ({Id}, rig {Rig})" +
			   $" [{(IsConnected ? "connected" : "disconnected")}, {(IsTracking ? "tracking" : "not tracking")}, {Features}]";
	}

	/// <summary>
	/// Base class for tracker providers: owns the tracker list, the lookup by rig, the
	/// registration/tracking events and the default free-tracker enumeration.
	/// </summary>
	public abstract class TrackerProviderBase : ITrackerProvider {
		private readonly List<ITracker> _trackers = new();
		private readonly Dictionary<ITracker, bool> _tracking = new();

		public abstract string Id { get; }

		public abstract bool IsAvailable { get; }

		public IReadOnlyList<ITracker> Trackers
			=> _trackers;

		public NoxEvent<ITracker> OnRegistered { get; } = new();
		public NoxEvent<ITracker> OnUnregistered { get; } = new();
		public NoxEvent<ITracker, bool> OnTracking { get; } = new();

		public virtual string Describe() {
			var builder = new StringBuilder($"{Id}: {_trackers.Count} tracker(s)");
			for (var i = 0; i < _trackers.Count; i++)
				builder.AppendLine().Append("  - ").Append(_trackers[i].Describe());

			return builder.ToString();
		}

		public bool TryGet(ushort rig, out ITracker tracker)
			=> TryGetRegistered(rig, out tracker);

		/// <summary>
		/// Lookup of an already-registered tracker, without touching the device list.
		/// <para>
		/// Several devices can claim the same rig: a runtime tracks the controllers and, when hand tracking is
		/// enabled, a pair of bare hands on the same <see cref="EntityRig.LeftHand"/> / <see cref="EntityRig.RightHand"/>
		/// slots. The controller always wins - it is the device the player actually holds, with its buttons, trigger
		/// and grip - and the bare hand is only used for a side no controller serves. Without that preference the
		/// device enumeration order decided the answer: one hand of a Quest session could answer with the hand
		/// device (no buttons, no skeleton while the controllers are held) and the other with its controller.
		/// </para>
		/// </summary>
		protected bool TryGetRegistered(ushort rig, out ITracker tracker) {
			tracker = null;

			for (var i = 0; i < _trackers.Count; i++) {
				var candidate = _trackers[i];
				if (candidate.Rig != rig)
					continue;

				if (tracker == null) {
					tracker = candidate;
				} else if (!IsController(tracker) && IsController(candidate))
					tracker = candidate;

				if (IsController(tracker))
					return true;
			}

			return tracker != null;
		}

		/// <summary>
		/// Whether a tracker is a physical controller. A bare hand can carry buttons too (the grip or the pinch of a
		/// hand device), which is why the <see cref="TrackerFeatures.Handed"/> flag is excluded.
		/// </summary>
		private static bool IsController(ITracker tracker) {
			if (tracker == null)
				return false;

			var features = tracker.Features;
			return features.HasFlag(TrackerFeatures.Buttons) && !features.HasFlag(TrackerFeatures.Handed);
		}

		public bool TryGet(string id, out ITracker tracker) {
			if (!string.IsNullOrEmpty(id))
				for (var i = 0; i < _trackers.Count; i++) {
					if (!string.Equals(_trackers[i].Id, id, global::System.StringComparison.OrdinalIgnoreCase))
						continue;

					tracker = _trackers[i];
					return true;
				}

			tracker = null;
			return false;
		}

		public bool TryGetPose(ushort rig, TrackerPoseMode mode, out TrackerPose pose) {
			if (TryGet(rig, out var tracker) && tracker.IsTracking)
				return tracker.TryGetPose(mode, out pose);

			pose = default;
			return false;
		}

		public bool TryGetPose(string id, TrackerPoseMode mode, out TrackerPose pose) {
			if (TryGet(id, out var tracker) && tracker.IsTracking)
				return tracker.TryGetPose(mode, out pose);

			pose = default;
			return false;
		}

		/// <summary>Refreshes tracking flags and raises <see cref="OnTracking"/> on changes.</summary>
		public void Poll() {
			for (var i = 0; i < _trackers.Count; i++) {
				var tracker = _trackers[i];
				var current = tracker.IsTracking;
				if (_tracking.TryGetValue(tracker, out var previous) && previous == current)
					continue;

				_tracking[tracker] = current;
				OnTracking.Invoke(tracker, current);
			}
		}

		/// <summary>
		/// Default free-tracker enumeration: every tracked entry except the HMD and, when asked,
		/// the hands. Loaders with dedicated trackers can override it.
		/// </summary>
		public virtual void Get(List<TrackerPose> into, bool excludeHanded, bool excludeControllers) {
			for (var i = 0; i < _trackers.Count; i++) {
				var tracker = _trackers[i];

				// The HMD is never a full-body tracker.
				if (tracker.IsHead())
					continue;

				if ((excludeHanded || excludeControllers) && tracker.IsHand())
					continue;

				if (!tracker.IsTracking || !tracker.TryGetPose(TrackerPoseMode.Raw, out var pose))
					continue;

				into.Add(pose);
			}
		}

		protected void Add(ITracker tracker) {
			if (tracker == null || _trackers.Contains(tracker))
				return;

			_trackers.Add(tracker);
			_tracking[tracker] = tracker.IsTracking;
			OnRegistered.Invoke(tracker);
		}

		protected void Remove(ITracker tracker) {
			if (tracker == null || !_trackers.Remove(tracker))
				return;

			_tracking.Remove(tracker);
			OnUnregistered.Invoke(tracker);
		}

		/// <summary>Unregisters every tracker; subclasses also unsubscribe their device events here.</summary>
		public virtual void Dispose() {
			for (var i = _trackers.Count - 1; i >= 0; i--)
				Remove(_trackers[i]);

			_tracking.Clear();
		}
	}
}
