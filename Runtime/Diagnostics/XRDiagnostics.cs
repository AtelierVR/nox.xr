using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Nox.Avatars.Rigging;
using Nox.Avatars.Scale;
using Nox.CCK.Players;
using Nox.CCK.XR;
using Nox.XR.Runtime.FullBody;
using Nox.XR.Runtime.Loaders;
using Nox.XR.Runtime.Settings;
using UnityEngine;
using UnityEngine.XR;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Runtime.Diagnostics {
	/// <summary>
	/// Read-only snapshots of the XR state, built for external tooling (the <c>nox.control</c>
	/// operators, the <c>nox.terminal</c> command and the editor debug window).
	/// <para>
	/// Everything is resolved through the public/static entry points of the mod
	/// (<see cref="XRInputs"/>, <see cref="XRLoaderManager"/>, <see cref="FullBodyCalibration"/>),
	/// so the three consumers share a single source of truth and cannot drift apart.
	/// </para>
	/// <para>
	/// Snapshots must be built on the main thread (they read Unity APIs).
	/// </para>
	/// </summary>
	public static class XRDiagnostics {
		/// <summary>Set once when the controllers mod API turned out to be unavailable.</summary>
		private static bool _controllerApiWarning;

		#region DTOs

		/// <summary>
		/// A vector as three numbers. The DTOs cannot expose Unity's <see cref="Vector3"/> directly:
		/// Newtonsoft walks its public members, and <c>Vector3.normalized</c> returns another Vector3
		/// ⇒ "Self referencing loop detected for property 'normalized'" in every operator output.
		/// </summary>
		[Serializable]
		public sealed class VectorSnapshot {
			public float X;
			public float Y;
			public float Z;

			public static VectorSnapshot From(Vector3 value)
				=> new() { X = value.x, Y = value.y, Z = value.z };

			public override string ToString()
				=> string.Format(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###}", X, Y, Z);
		}

		/// <summary>One physical XR device, as <c>InputDevices</c> reports it.</summary>
		[Serializable]
		public sealed class DeviceSnapshot {
			public string Name;
			public string Manufacturer;
			public string Serial;
			/// <summary>Flags of <see cref="InputDeviceCharacteristics"/>, space separated.</summary>
			public string Characteristics;
			public bool IsValid;
			public bool? IsTracked;
			public bool HasPosition;
			public bool HasRotation;
			public VectorSnapshot Position;
			/// <summary>Euler angles, in the tracking space (or the avatar's, when an origin is set).</summary>
			public VectorSnapshot Rotation;
			/// <summary>Would <see cref="FullBodyTrackers.Get"/> keep this device?</summary>
			public bool TrackerCandidate;
			/// <summary>Why the tracker filter rejected the device (null when kept).</summary>
			public string TrackerRejection;

			public string Describe() {
				var sb = new StringBuilder();
				sb.Append(Name ?? "(unnamed)");
				if (!string.IsNullOrEmpty(Serial)) sb.Append(" [").Append(Serial).Append(']');
				sb.Append(" — ").Append(Characteristics);
				sb.Append(IsValid ? ", valid" : ", INVALID");
				sb.Append(IsTracked switch {
					true  => ", tracked",
					false => ", NOT tracked",
					null  => ", tracked=?"
				});
				if (HasPosition)
					sb.Append(", pos ").Append(Position);
				if (HasRotation)
					sb.Append(", rot ").Append(Rotation).Append("°");
				sb.Append(TrackerCandidate
					? "  => USED as tracker"
					: "  => REJECTED: " + TrackerRejection);
				return sb.ToString();
			}
		}

		public sealed class ProviderSnapshot {
			public string Id;
			public int Priority;
			public bool Valid;

			public override string ToString()
				=> $"{Id} (priority {Priority}{(Valid ? "" : ", invalid here")})";
		}

		/// <summary>Runtime/loader/provider state, independent from any calibration.</summary>
		[Serializable]
		public sealed class StateSnapshot {
			public bool Playing;
			public string Platform;
			public bool XREnabled;
			public bool LoaderRunning;
			public string ActiveLoader;
			public string PreferredLoader;
			public ProviderSnapshot[] Providers;
			public string InputProvider;
			public string DefaultProvider;
			public string ActiveProvider;
			public bool HasHeadset;
			public bool HasHandLeft;
			public bool HasHandRight;
			public int HardwareTrackerNodeCount;
			public int DeviceCount;
			public int TrackerCount;
			public bool XRControllerActive;
			public string Origin;
			public VectorSnapshot OriginPosition;
			public float AvatarScale;
			public bool FullBodyTracking;
			public float CalibrationRange;
			public bool OneHandValidation;

			/// <summary>State of every tracker provider of the loaded XR loaders (see <see cref="Nox.XR.Trackers.ITrackerProvider"/>).</summary>
			public string[] TrackerProviders;

			public string Describe(string indent = "  ") {
				var sb = new StringBuilder();
				sb.Append(indent).Append("play mode: ").Append(Playing ? "yes" : "no")
					.Append(", platform: ").Append(Platform).AppendLine();
				sb.Append(indent).Append("XR enabled: ").Append(XREnabled)
					.Append(", loader running: ").Append(LoaderRunning)
					.Append(", active loader: ").Append(ActiveLoader ?? "(none)")
					.Append(", preferred: ").Append(string.IsNullOrEmpty(PreferredLoader) ? "(auto)" : PreferredLoader)
					.AppendLine();
				sb.Append(indent).Append("providers: ").Append(Providers == null || Providers.Length == 0
						? "(none discovered)"
						: string.Join(", ", Providers.Select(p => p.ToString())))
					.AppendLine();
				sb.Append(indent).Append("input provider: ").Append(InputProvider ?? "(none)")
					.Append(" [default: ").Append(DefaultProvider ?? "none")
					.Append(", active: ").Append(ActiveProvider ?? "none").Append(']').AppendLine();
				sb.Append(indent).Append("headset: ").Append(HasHeadset)
					.Append(", hand L: ").Append(HasHandLeft)
					.Append(", hand R: ").Append(HasHandRight).AppendLine();
				sb.Append(indent).Append("devices: ").Append(DeviceCount)
					.Append(", XRNode.HardwareTracker devices: ").Append(HardwareTrackerNodeCount)
					.Append(", trackers used: ").Append(TrackerCount).AppendLine();
				sb.Append(indent).Append("XRController is the active proxy: ").Append(XRControllerActive).AppendLine();
				sb.Append(indent).Append("origin: ").Append(Origin ?? "(none)")
					.Append(" @ ").Append(OriginPosition)
					.Append(" — avatar scale: ").Append(AvatarScale.ToString("0.###", CultureInfo.InvariantCulture)).AppendLine();
				foreach (var provider in TrackerProviders ?? Array.Empty<string>())
					sb.Append(indent).Append("trackers — ").Append(provider).AppendLine();
				sb.Append(indent).Append("FBT: ").Append(FullBodyTracking)
					.Append(", range: ").Append(CalibrationRange.ToString("0.###", CultureInfo.InvariantCulture))
					.Append(" m, one-hand validation: ").Append(OneHandValidation);
				return sb.ToString();
			}
		}

		[Serializable]
		public sealed class BindingSnapshot {
			public string Tracker;
			public string Bone;
			public VectorSnapshot OffsetPosition;
			/// <summary>Euler angles.</summary>
			public VectorSnapshot OffsetRotation;
			public float Scale;

			public override string ToString()
				=> $"{Bone} <- {Tracker} (offset {OffsetPosition}, avatar scale {Scale:0.###})";
		}

		[Serializable]
		public sealed class MatchSnapshot {
			public string Bone;
			public string Tracker;
			public float Distance;

			public override string ToString()
				=> $"{Bone} <- {Tracker} ({Distance.ToString("0.###", CultureInfo.InvariantCulture)} m)";
		}

		[Serializable]
		public sealed class RigBoneSnapshot {
			public string Bone;
			public string Path;
			public VectorSnapshot Position;
			public bool Active;
		}

		/// <summary>Full-body calibration + driving state.</summary>
		[Serializable]
		public sealed class FullBodySnapshot {
			public bool Available;
			public bool Calibrating;
			public bool HasCalibration;
			public bool Driving;
			public float Range;
			public float AvatarScale;
			public string[] Bones;
			public BindingSnapshot[] Bindings;
			public MatchSnapshot[] Matches;
			public RigBoneSnapshot[] RigBones;
			public string RigBackend;

			public string Describe(string indent = "  ") {
				if (!Available)
					return indent + "full-body calibration component is not present (the XR proxy is not created?).";

				var sb = new StringBuilder();
				sb.Append(indent).Append("calibrating: ").Append(Calibrating)
					.Append(", stored calibration: ").Append(HasCalibration)
					.Append(", driving: ").Append(Driving).AppendLine();
				sb.Append(indent).Append("range: ").Append(Range.ToString("0.###", CultureInfo.InvariantCulture))
					.Append(" m, avatar scale: ").Append(AvatarScale.ToString("0.###", CultureInfo.InvariantCulture))
					.Append(", rig: ").Append(RigBackend ?? "(none)").AppendLine();
				sb.Append(indent).Append("bones: ").Append(Bones == null || Bones.Length == 0 ? "(none)" : string.Join(", ", Bones)).AppendLine();

				if (Calibrating) {
					sb.Append(indent).Append("live matches: ");
					sb.Append(Matches == null || Matches.Length == 0 ? "(none)" : string.Join(" | ", Matches.Select(m => m.ToString())));
					sb.AppendLine();
				}

				if (Bindings is { Length: > 0 }) {
					sb.Append(indent).Append("stored bindings:").AppendLine();
					foreach (var binding in Bindings)
						sb.Append(indent).Append("  ").Append(binding).AppendLine();
				}

				return sb.ToString();
			}
		}

		#endregion

		#region Builders

		/// <summary>Every XR device reported by Unity, with its tracker-filter verdict.</summary>
		public static List<DeviceSnapshot> Devices(bool trackedOnly = false) {
			var devices = new List<InputDevice>();
			InputDevices.GetDevices(devices);

			var snapshots = new List<DeviceSnapshot>(devices.Count);
			foreach (var device in devices) {
				var snapshot = Snapshot(device);
				if (trackedOnly && snapshot.IsTracked != true)
					continue;
				snapshots.Add(snapshot);
			}

			return snapshots;
		}

		/// <summary>One device as a diagnostic snapshot (including the exact tracker-filter verdict).</summary>
		public static DeviceSnapshot Snapshot(InputDevice device) {
			var characteristics = device.characteristics;

			var hasPosition = device.TryGetFeatureValue(CommonUsages.devicePosition, out var position);
			var hasRotation = device.TryGetFeatureValue(CommonUsages.deviceRotation, out var rotation);
			var hasTracked  = device.TryGetFeatureValue(CommonUsages.isTracked, out var tracked);

			if (FullBodyTrackers.TrackingSpace() is { } trackingSpace) {
				position = trackingSpace.TransformPoint(position);
				rotation = trackingSpace.rotation * rotation;
			}

			FullBodyTrackers.IsCandidate(device, true, out var rejection);

			return new DeviceSnapshot {
				Name            = device.name,
				Manufacturer    = device.manufacturer,
				Serial          = device.serialNumber,
				Characteristics = DescribeCharacteristics(characteristics),
				IsValid         = device.isValid,
				IsTracked       = hasTracked ? tracked : null,
				HasPosition     = hasPosition,
				HasRotation     = hasRotation,
				Position        = VectorSnapshot.From(position),
				Rotation        = VectorSnapshot.From(rotation.eulerAngles),
				TrackerCandidate = rejection == null,				TrackerRejection = rejection
			};
		}

		/// <summary>Trackers used by full-body tracking, plus (optionally) every other tracked device.</summary>
		/// <param name="includeAll">When set, also returns devices the in-game filter rejects.</param>
		public static List<DeviceSnapshot> Trackers(bool includeAll = false) {
			var devices = new List<InputDevice>();
			InputDevices.GetDevices(devices);

			var snapshots = new List<DeviceSnapshot>();
			foreach (var device in devices) {
				var snapshot = Snapshot(device);
				var trackedDevice = device.characteristics.HasFlag(InputDeviceCharacteristics.TrackedDevice);

				if (snapshot.TrackerCandidate || (includeAll && trackedDevice))
					snapshots.Add(snapshot);
			}

			return snapshots;
		}

		/// <summary>Loader/provider/device state.</summary>
		public static StateSnapshot State() {
			var providers = XRLoaderManager.Providers;
			var tracked   = new List<InputDevice>();
			var all       = new List<InputDevice>();
			InputDevices.GetDevices(all);
			InputDevices.GetDevicesAtXRNode(XRNode.HardwareTracker, tracked);

			var origin = XROriginSetter.GlobalOrigin;
			var trackingSpace = FullBodyTrackers.TrackingSpace();

			return new StateSnapshot {
				Playing           = Application.isPlaying,
				Platform          = Application.platform.ToString(),
				XREnabled         = EnableXRSetting.Value,
				LoaderRunning     = XRLoaderManager.IsRunning,
				ActiveLoader      = XRLoaderManager.Current?.Id,
				PreferredLoader   = XRLoaderPreference.Preferred,
				Providers         = providers == null
					? Array.Empty<ProviderSnapshot>()
					: providers.Select(p => new ProviderSnapshot { Id = p.Id, Priority = p.Priority, Valid = p.IsValid }).ToArray(),
				InputProvider     = XRInputs.Provider?.GetType().Name,
				DefaultProvider   = XRInputs.DefaultProvider?.GetType().Name,
				ActiveProvider    = XRInputs.ActiveProvider?.GetType().Name,
				HasHeadset        = XRInputs.HasHeadset,
				HasHandLeft       = XRInputs.HasHandLeft,
				HasHandRight      = XRInputs.HasHandRight,
				HardwareTrackerNodeCount = tracked.Count,
				DeviceCount       = all.Count,
				TrackerCount      = Trackers().Count,
				XRControllerActive = IsXRControllerActive(),
				Origin            = origin ? $"{origin.name} (suivi : {trackingSpace?.name ?? "racine"})" : null,
				OriginPosition    = VectorSnapshot.From(trackingSpace ? trackingSpace.position : Vector3.zero),
				AvatarScale       = AvatarScale(),
				FullBodyTracking  = FullBodyTrackingSetting.Value,
				CalibrationRange  = FullBodyCalibrationRangeSetting.Value,
				OneHandValidation = OneHandValidationSetting.Value,
				TrackerProviders  = FullBodyTrackers.Providers()
					.Select(provider => provider == null ? "(null)" : $"{provider.Id}: {provider.Describe()}")
					.ToArray()
			};
		}

		/// <summary>Calibration + rig state (empty snapshot when the XR proxy is absent).</summary>
		public static FullBodySnapshot FullBody(bool withRig = false) {
			var calibration = FullBodyCalibration.Instance;
			if (calibration == null)
				return new FullBodySnapshot { Available = false };

			var avatarLoader = calibration.avatarLoader;
			var rig          = avatarLoader?.GetAvatar()?.Descriptor?.Anchor
				?.GetComponentInChildren<IRigProvider>(true)?.GetRig();

			var snapshot = new FullBodySnapshot {
				Available       = true,
				Calibrating     = calibration.IsCalibrating,
				HasCalibration  = calibration.HasCalibration,
				Driving         = calibration.HasCalibration && !calibration.IsCalibrating && IsXRControllerActive(),
				Range           = calibration.CurrentRange,
				AvatarScale     = AvatarScale(),
				Bones           = FullBodyCalibration.GetAssignableBones(rig)
					.Select(b => b.ToString())
					.ToArray(),
				RigBackend      = rig?.Id,
				Matches         = calibration.Matches
					.Select(m => new MatchSnapshot {
						Bone    = m.Key.ToString(),
						Tracker = m.Value.Id,
						Distance = Vector3.Distance(
							rig?.GetBone(m.Key)?.position ?? Vector3.zero,
							m.Value.Position
						)
					})
					.OrderBy(m => m.Bone, StringComparer.Ordinal)
					.ToArray(),
				Bindings        = calibration.Data?.Bindings?
					.Select(b => new BindingSnapshot {
						Tracker        = b.TrackerId,
						Bone           = b.BodyBone.ToString(),
						OffsetPosition = VectorSnapshot.From(b.OffsetPosition),
						OffsetRotation = VectorSnapshot.From(b.OffsetRotation.eulerAngles),
						Scale          = b.Scale
					})
					.ToArray() ?? Array.Empty<BindingSnapshot>()
			};

			if (withRig && rig != null)
				snapshot.RigBones = FullBodyCalibration.GetAssignableBones(rig)
					.Select(bone => {
						var boneTransform = rig.GetBone(bone);
						return new RigBoneSnapshot {
							Bone     = bone.ToString(),
							Path     = boneTransform ? Path(boneTransform) : null,
							Position = VectorSnapshot.From(boneTransform ? boneTransform.position : Vector3.zero),
							Active   = rig.IsActive(bone)
						};
					})
					.ToArray();

			return snapshot;
		}

		/// <summary>
		/// Human-readable diagnosis of the current tracker situation — the "why is my tracker
		/// not used?" answer, shared by every consumer.
		/// </summary>
		public static string[] Hints() {
			var hints  = new List<string>();
			var state  = State();
			var all    = Devices();
			var used   = all.Where(d => d.TrackerCandidate).ToArray();

			// Reported even in edit mode: without that API the proxy state cannot be read at all.
			if (_controllerApiWarning)
				hints.Add("The controllers mod API is unavailable (XRController.IsCurrent() failed): the XR proxy state cannot be read.");

			// Trackers the active XR runtime cannot expose but another provider can (a tracker with no
			// role, a hands-role tracker, a network backend…): each provider reports its own state.
			foreach (var provider in FullBodyTrackers.Providers())
				hints.Add(provider.Describe());

			if (!state.Playing) {
				hints.Add("Not in play mode: device data only exists while the game runs.");
				return hints.ToArray();
			}

			if (!state.LoaderRunning)
				hints.Add("No XR loader is running (XR disabled or no headset): trackers cannot be read.");
			else
				hints.Add($"Active loader: {state.ActiveLoader ?? "(none)"}"
					+ (state.Providers is { Length: > 0 }
						? $" — chain: {string.Join(" > ", state.Providers.Select(p => p.Id + (p.Valid ? "" : "(invalid)")))}"
						: ""));

			if (state.DeviceCount == 0) {
				hints.Add("Unity reports no XR device at all.");
				return hints.ToArray();
			}

			if (used.Length == 0) {
				hints.Add($"No tracker is usable: {state.DeviceCount} device(s) seen, 0 kept by the filter (range {state.CalibrationRange:0.###} m).");

				foreach (var device in all.Where(d => d.Characteristics.Contains("TrackedDevice")))
					hints.Add($"  - {device.Name}: {device.TrackerRejection}");

				if (!all.Any(d => d.Characteristics.Contains("TrackedDevice")))
					hints.Add("  - no device is flagged 'TrackedDevice'.");
				else if (state.HardwareTrackerNodeCount == 0)
					hints.Add("  - no device is exposed on XRNode.HardwareTracker (runtime-side profile missing).");
			} else {
				hints.Add($"{used.Length} tracker(s) usable: {string.Join(", ", used.Select(d => d.Name))}.");
			}

			if (!state.FullBodyTracking)
				hints.Add("Full-body tracking is DISABLED in the XR settings: the calibration button is hidden and no tracker is driven.");
			else if (FullBodyCalibration.Instance is { HasCalibration: false })
				hints.Add("No stored calibration: run the XR calibration to bind the trackers to the rig bones.");

			return hints.ToArray();
		}

		/// <summary>
		/// <c>XRController.IsCurrent()</c> goes through the controllers mod: that mod is optional and
		/// its API does not exist before the mods are initialized. The call is guarded because a
		/// diagnostic runs from editor panels and from the control server, where an exception would
		/// break the caller (the panel tick, the mod update loop) instead of just reporting.
		/// </summary>
		internal static bool IsXRControllerActive() {
			try {
				return XRController.IsCurrent();
			} catch (Exception e) {
				if (!_controllerApiWarning) {
					_controllerApiWarning = true;
					Logger.LogWarning($"XRController.IsCurrent() is unavailable: {e.Message}", tag: nameof(XRDiagnostics));
				}

				return false;
			}
		}

		/// <summary>
		/// The whole diagnosis as text: what an editor panel copies to the clipboard and what a bug
		/// report should contain. It is built from the same snapshots as the operators, so a report
		/// and an operator answer always tell the same story.
		/// </summary>
		public static string Report() {
			var builder = new StringBuilder();

			builder.AppendLine("=== XR diagnostics ===");
			builder.AppendLine(State().Describe(string.Empty).TrimEnd());

			builder.AppendLine();
			builder.AppendLine("--- diagnosis ---");
			foreach (var hint in Hints())
				builder.AppendLine(hint);

			builder.AppendLine();
			builder.AppendLine("--- trackers used ---");
			var trackers = Trackers();
			if (trackers.Count == 0)
				builder.AppendLine("(none)");
			else
				foreach (var tracker in trackers)
					builder.AppendLine(tracker.Describe());

			builder.AppendLine();
			builder.AppendLine("--- devices ---");
			foreach (var device in Devices())
				builder.AppendLine(device.Describe());

			builder.AppendLine();
			builder.AppendLine("--- full-body ---");
			builder.AppendLine(FullBody(withRig: true).Describe(string.Empty).TrimEnd());

			return builder.ToString();
		}

		#endregion

		#region Helpers

		/// <summary>Flags of an <see cref="InputDeviceCharacteristics"/> value, space separated.</summary>
		public static string DescribeCharacteristics(InputDeviceCharacteristics characteristics) {
			if (characteristics == InputDeviceCharacteristics.None)
				return "None";

			var names = Enum.GetValues(typeof(InputDeviceCharacteristics))
				.Cast<InputDeviceCharacteristics>()
				.Where(flag => flag != InputDeviceCharacteristics.None && characteristics.HasFlag(flag))
				.Select(flag => flag.ToString())
				.ToArray();

			return names.Length == 0 ? characteristics.ToString() : string.Join(" | ", names);
		}

		public static float AvatarScale()
			=> FullBodyCalibration.Instance?.avatarLoader?.GetAvatar()?.Descriptor?
				.GetModules<IScaleAvatarModule>()
				.FirstOrDefault()?.Scale ?? 1f;

		/// <summary>Full hierarchy path of a transform, for readable rig diagnostics.</summary>
		public static string Path(Transform transform) {
			if (!transform)
				return null;

			var path = transform.name;
			var parent = transform.parent;
			while (parent) {
				path    = parent.name + "/" + path;
				parent  = parent.parent;
			}

			return path;
		}

		public static string HumanBodyBone(ushort rigIndex)
			=> rigIndex.ToPlayerRig().ToString();

		#endregion
	}
}
