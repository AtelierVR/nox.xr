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
		/// Asks the avatar for a pose through its standard "Pose" layer (see <see cref="AvatarPose"/>): only the
		/// integer is written, the avatar cuts tracking and drives its own layers from it.
		/// </summary>
		private void SetPose(AvatarPose pose) {
			var parameter = GetParameters()?.GetParameter(AvatarPoseConstants.ParameterName);
			if (parameter == null) {
				if (pose != AvatarPose.Normal)
					Logger.LogWarning($"Calibration: l'avatar n'a pas de paramÃƒÂ¨tre Ã‚Â« {AvatarPoseConstants.ParameterName} Ã‚Â» : la pose Ã‚Â« {pose} Ã‚Â» ne sera pas appliquÃƒÂ©e (couches : {AvailableLayers()}).", this, tag: nameof(CalibrationManager));
				return;
			}

			parameter.Value = (int)pose;

			// The layer must be running for the pose to be visible.
			if (pose != AvatarPose.Normal)
				GetPlayableLayers()?.StartLayer(PlayableLayerNaming.Pose.ToString());

			Logger.LogDebug($"Calibration: pose Ã‚Â« {pose} Ã‚Â» demandÃƒÂ©e ÃƒÂ  l'avatar.", this, tag: nameof(CalibrationManager));
		}

		/// <summary>Avatar layers, for the diagnostic messages.</summary>
		private string AvailableLayers() {
			var layers = GetPlayableLayers();
			if (layers == null)
				return "none";

			var keys = new List<string>();
			for (var i = 0; i < layers.LayerCount; i++)
				keys.Add($"{layers.GetLayerKey(i)}[{layers.GetLayerRole(i)}]");
			return string.Join(", ", keys);
		}

	}
}
