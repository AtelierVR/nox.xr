using Nox.Avatars;
using Nox.CCK.Entities;
using Nox.CCK.Utils;
using Nox.XR.Runtime.Tracking;
using UnityEngine;

namespace Nox.XR.Controller.V2 {
	/// <summary>
	/// Calibration side of the v2 XR controller: the rig does not use the AutoHand proxy, so it hosts its own
	/// <see cref="CalibrationManager"/> and feeds it the loaded avatar and the player root.
	/// <para>
	/// A calibration suspends the part binding (the rig goes back to its animation for the calibration pose);
	/// when it ends, the parts are rebuilt from the new tracker → rig bindings and bound again.
	/// </para>
	/// </summary>
	public partial class XRControllerV2 {
		private CalibrationManager _calibration;

		/// <summary>Calibration manager of this rig, or <c>null</c> when it could not be created.</summary>
		public CalibrationManager Calibration
			=> _calibration;

		private void SetupCalibration() {
			if (!player)
				return;

			_calibration = gameObject.GetOrAddComponent<CalibrationManager>();

			// The v2 parts follow the calibrated poses themselves: the manager must not write the bones.
			_calibration.driveRigParts = false;
			_calibration.root         = player.transform;

			// The parts are unbound while calibrating, so the head bone stops following the view: the manager puts
			// the avatar root where the head lands on the player eyes, the same pose the rig gives outside a
			// calibration.
			_calibration.headReference = player.Head ? player.Head.RigTarget : null;

			// The player tracking container is the space the tracked parts are written in: it is what the raw
			// device poses are relative to, and it replaces the XROrigin of the v1 proxy.
			TrackerResolver.FallbackTrackingSpace = player.TrackingContainer;

			CalibrationManager.CalibrationStateChanged.AddListener(OnCalibrationStateChanged);
		}

		private void DisposeCalibration() {
			CalibrationManager.CalibrationStateChanged.RemoveListener(OnCalibrationStateChanged);

			if (_calibration) {
				_calibration.Unbind();
				_calibration.headReference = null;
			}

			if (player && ReferenceEquals(TrackerResolver.FallbackTrackingSpace, player.TrackingContainer))
				TrackerResolver.FallbackTrackingSpace = null;
		}

		/// <summary>Gives the calibration manager the avatar that was just loaded.</summary>
		private void BindCalibration(IRuntimeAvatar avatar) {
			if (!_calibration || !player)
				return;

			_calibration.Bind(avatar, player.transform, driveParts: false);
			TrackerResolver.FallbackTrackingSpace = player.TrackingContainer;
		}

		/// <summary>Forgets the avatar (no avatar is loaded anymore).</summary>
		private void UnbindCalibration() {
			_calibration?.Unbind();

			if (player && ReferenceEquals(TrackerResolver.FallbackTrackingSpace, player.TrackingContainer))
				TrackerResolver.FallbackTrackingSpace = null;
		}

		/// <summary>
		/// A calibration started or ended. The part binding is suspended while the avatar is in its
		/// calibration pose, then rebuilt: the calibration may have moved a tracker to another rig slot.
		/// </summary>
		private void OnCalibrationStateChanged(bool calibrating) {
			if (!player)
				return;

			if (calibrating) {
				ResetAvatarOnContainer();
				UnbindAllParts(EntityRig.Head);
				return;
			}

			binder?.RefreshParts();
			ResetAvatarOnContainer();

			var rig = _boundRig ?? _rig?.Rig;
			if (rig != null) {
				rig.SetExternalRootControl(true);
				BindParts(rig);
			}

			RefreshFullBodyOverride();
		}

		/// <summary>
		/// Puts the avatar back on its container. A calibration aligns the avatar root on the head every frame
		/// (so the player can line the body up); that local offset must not survive the calibration, otherwise
		/// the avatar stays behind or ahead of the player and looks like it is sliding.
		/// </summary>
		private void ResetAvatarOnContainer() {
			var anchor = _avatar?.Descriptor?.Anchor;
			if (!anchor)
				return;

			anchor.transform.localPosition = Vector3.zero;
			anchor.transform.localRotation = Quaternion.identity;
		}
	}
}
