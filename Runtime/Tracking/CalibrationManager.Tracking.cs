using System.Collections.Generic;
using System.Linq;
using Autohand;
using Nox.Avatars.Parameters;
using Nox.Avatars.Rigging;
using Nox.Avatars.Scale;
using Nox.Avatars.StateMachines;
using Nox.CCK.Avatars.Playable;
using Nox.CCK.Avatars.Rigging;
using Nox.CCK.Entities;
using Nox.CCK.Utils;
using Nox.CCK.XR;
using Nox.Settings;
using Nox.XR.Calibration;
using CalibrationService = Nox.XR.Calibration.Calibration;
using Nox.XR.Runtime.Connectors;
using Nox.XR.Runtime.Settings;
using Nox.XR.Trackers;
using UnityEngine;
using UnityEngine.XR;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Runtime.Tracking {
	
	public partial class CalibrationManager : MonoBehaviour {
		/// <summary>
		/// Bones a tracker may be assigned to: the parts the avatar's rig exposes, converted to humanoid bones.
		/// </summary>
		public static List<HumanBodyBones> GetAssignableBones(IRigging rig) {
			var bones = new List<HumanBodyBones>();
			if (rig == null)
				return bones;

			var parts = rig.GetParts();

			for (var i = 0; i < parts.Length; i++) {
				var bone = parts[i].GetId().ToHumanBodyBones();
				if (bone == HumanBodyBones.LastBone || bones.Contains(bone))
					continue;
				bones.Add(bone);
			}

			return bones;
		}

		/// <summary>Stored calibration (read-only view for the diagnostics).</summary>
		public CalibrationData Data
			=> _data;

		/// <summary>Trackers matched during the current calibration run, per bone (diagnostics).</summary>
		public IReadOnlyDictionary<HumanBodyBones, TrackerPose> Matches
			=> _matches;

		/// <summary>Live capture radius, avatar scale included (diagnostics).</summary>
		public float CurrentRange
			=> EffectiveRange;


		/// <summary>
		/// Some runtimes flag a Vive tracker as a controller (a role was assigned to it).
		/// Accepting them keeps the tracker usable; the real controllers stay filtered out because
		/// they are also flagged <c>Left</c>/<c>Right</c>.
		/// </summary>
		private static bool AllowControllerFlaggedTrackers
			=> TrackersIncludeControllerSetting.Value;

		/// <summary>
		/// Whether full-body tracking may run at all: the local player must be using the XR proxy,
		/// i.e. the XR controller must be the active controller. This prevents calibration (and the
		/// tracker driving) from being offered or applied outside of XR (desktop mode, other proxies).
		/// </summary>
		public static bool IsXRControllerActive
			=> XRController.IsCurrent();

		/// <summary>
		/// Usable trackers right now, with the same filters as the calibration: hands and controllers are
		/// excluded, a tracker flagged as a controller counts when <see cref="AllowControllerFlaggedTrackers"/> is on.
		/// 0 outside XR or when full-body tracking is off.
		/// </summary>
		public static int TrackerCount {
			get {
				if (!IsXRControllerActive || !FullBodyTrackingSetting.Value)
					return 0;

				_probeTrackers.Clear();
				TrackerResolver.Get(_probeTrackers, excludeHanded: true, excludeControllers: !AllowControllerFlaggedTrackers);
				return _probeTrackers.Count;
			}
		}

		/// <summary>Shared working list of <see cref="TrackerCount"/>, which only counts.</summary>
		private static readonly List<TrackerPose> _probeTrackers = new();

	}
}
