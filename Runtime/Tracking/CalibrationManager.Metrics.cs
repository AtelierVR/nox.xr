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

		/// <summary>Duration of the T-pose capture started by <see cref="StartMetricsEstimate"/>.</summary>
		public const float MetricsEstimateDelay = 5f;

		/// <summary>
		/// Eye height as a fraction of the total height (anthropometric ratio). The headset gives the eye height,
		/// the total height follows.
		/// </summary>
		private const float EyeHeightRatio = 0.936f;

		/// <summary>Plausible player height: bounds an estimation made crouched or while moving.</summary>
		private const float MinPlayerHeight = 1.2f;
		private const float MaxPlayerHeight = 2.2f;

		private float _metricsRemaining;
		private float _metricsEyeHeight;
		private float _metricsArmSpan;
		private int   _metricsShownSecond = -1;

		/// <summary>True while the capture runs (shown by the settings button).</summary>
		public bool IsEstimatingMetrics
			=> _metricsRemaining > 0f;

		/// <summary>Seconds left in the capture.</summary>
		public float MetricsEstimateRemaining
			=> Mathf.Max(0f, _metricsRemaining);

		/// <summary>
		/// Starts the capture: stay in T-pose without moving. The highest eye position and the widest hand span
		/// are kept, then written to <see cref="RealHeightSetting"/> and <see cref="PlayerArmSpanSetting"/>.
		/// </summary>
		public void StartMetricsEstimate() {
			if (!IsXRControllerActive || IsCalibrating)
				return;

			_metricsRemaining = MetricsEstimateDelay;
			_metricsEyeHeight = 0f;
			_metricsArmSpan   = 0f;
			_metricsShownSecond = Mathf.CeilToInt(MetricsEstimateDelay);
			SettingsNotifier.NotifyUpdated(null);
		}

		private void TickMetricsEstimate() {
			_metricsRemaining -= Time.unscaledDeltaTime;

			var camera = player && player.headCamera ? player.headCamera.transform : null;
			if (camera)
				_metricsEyeHeight = Mathf.Max(_metricsEyeHeight, camera.position.y);

			// In T-pose the hand span is the arm span (wrist to wrist).
			if (player && player.handLeft && player.handRight)
				_metricsArmSpan = Mathf.Max(_metricsArmSpan, Vector3.Distance(player.handLeft.transform.position, player.handRight.transform.position));

			var second = Mathf.CeilToInt(Mathf.Max(0f, _metricsRemaining));
			if (second != _metricsShownSecond) {
				_metricsShownSecond = second;
				SettingsNotifier.NotifyUpdated(null);
			}

			// Same controller validation as a calibration, so the capture can be confirmed before the delay.
			if (_metricsRemaining > 0f && MetricsEstimateDelay - _metricsRemaining > ValidationArmDelay) {
				var leftConnected  = XRInputs.HasHandLeft;
				var rightConnected = XRInputs.HasHandRight;
				var left  = leftConnected && IsHandPressed(XRNode.LeftHand);
				var right = rightConnected && IsHandPressed(XRNode.RightHand);
				if ((left || right) && (!leftConnected || left) && (!rightConnected || right))
					_metricsRemaining = 0f;
			}

			if (_metricsRemaining > 0f)
				return;

			if (_metricsEyeHeight > 0.5f)
				RealHeightSetting.Value = Mathf.Clamp(_metricsEyeHeight / EyeHeightRatio, MinPlayerHeight, MaxPlayerHeight);

			if (_metricsArmSpan > 0f)
				PlayerArmSpanSetting.Value = _metricsArmSpan;

			Logger.LogDebug($"Mesures joueur : taille {RealHeightSetting.Value:F3} m (yeux {_metricsEyeHeight:F3} m), envergure {PlayerArmSpanSetting.Value:F3} m Ã¢â‚¬â€ avatar {AvatarHeight():F2} m", this, tag: nameof(CalibrationManager));
			SettingsNotifier.NotifyUpdated(null);
		}

		/// <summary>
		/// Real (world) height of the avatar in metres, from its scale module. The value is measured on the
		/// model's rest pose, so it does not follow the animation or the trackers.
		/// </summary>
		private float AvatarHeight()
			=> avatarLoader?.GetAvatar()?.Descriptor
				?.GetModules<IScaleAvatarModule>()
				.FirstOrDefault()?.Height ?? 0f;

	}
}
