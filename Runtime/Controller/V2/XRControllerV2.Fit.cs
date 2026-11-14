using System.Linq;
using Nox.Avatars;
using Nox.Avatars.Scale;
using Nox.XR.Runtime.Settings;
using Nox.XR.Runtime.Tracking;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Controller.V2 {
	/// <summary>
	/// Fits the real body of the player to the avatar's proportions.
	/// <para>
	/// The avatar is the reference: it is never rescaled. A 2 m avatar driven by a 1.8 m player instead stretches
	/// every tracked pose (headset, hands, feet) by <c>avatar height / player height</c>, so the head lands on the
	/// eyes of the player and the feet stay on the ground. Without it the extra height has nowhere to go and the
	/// avatar either folds its legs or sinks under the floor.
	/// </para>
	/// <para>
	/// The factor is only recomputed when one of its two inputs changes: the avatar is bound (or resized) and the
	/// player height is written. Nothing polls per frame, <see cref="TrackerResolver.PoseScale"/> is a plain value
	/// the pose pipeline reads.
	/// </para>
	/// </summary>
	public partial class XRControllerV2 {
		private IAvatarDescriptor   _fitDescriptor;
		private IScaleAvatarModule  _fitModule;

		/// <summary>Factor the tracked poses are stretched by (1 when the avatar is the player's size).</summary>
		public float PoseFit
			=> TrackerResolver.PoseScale;

		/// <summary>Starts following the size of the avatar and the height of the player.</summary>
		private void SubscribeFit(IAvatarDescriptor descriptor) {
			UnsubscribeFit();

			if (descriptor != null) {
				_fitDescriptor = descriptor;

				// The scale module can join or leave the avatar after it was bound (module swap, late setup): the
				// fit follows the module list instead of only reading it once.
				_fitDescriptor.OnModuleRegistered.AddListener(OnFitModuleRegistered);
				_fitDescriptor.OnModuleUnregistered.AddListener(OnFitModuleUnregistered);

				BindFitModule(descriptor.GetModules<IScaleAvatarModule>().FirstOrDefault());
			}

			RealHeightSetting.Changed.AddListener(OnFitInputChanged);

			RefreshFit();
		}

		/// <summary>Stops following them and hands the pose back at its real size.</summary>
		private void UnsubscribeFit() {
			if (_fitDescriptor != null) {
				_fitDescriptor.OnModuleRegistered.RemoveListener(OnFitModuleRegistered);
				_fitDescriptor.OnModuleUnregistered.RemoveListener(OnFitModuleUnregistered);
				_fitDescriptor = null;
			}

			BindFitModule(null);

			RealHeightSetting.Changed.RemoveListener(OnFitInputChanged);

			RefreshFit();
		}

		/// <summary>Follows the scale of one module, or nothing when <paramref name="module"/> is <c>null</c>.</summary>
		private void BindFitModule(IScaleAvatarModule module) {
			if (ReferenceEquals(_fitModule, module))
				return;

			if (_fitModule != null)
				_fitModule.OnScaleChanged.RemoveListener(OnFitInputChanged);

			_fitModule = module;

			if (_fitModule != null)
				_fitModule.OnScaleChanged.AddListener(OnFitInputChanged);

			RefreshFit();
		}

		private void OnFitModuleRegistered(IAvatarModule module) {
			if (module is IScaleAvatarModule scale)
				BindFitModule(scale);
		}

		private void OnFitModuleUnregistered(IAvatarModule module) {
			if (module is IScaleAvatarModule scale && ReferenceEquals(_fitModule, scale))
				BindFitModule(null);
		}

		private void OnFitInputChanged(float _)
			=> RefreshFit();

		/// <summary>Recomputes the pose scale from the avatar height and the player height.</summary>
		public void RefreshFit() {
			var avatarHeight = _fitModule != null ? _fitModule.Height : 0f;
			var playerHeight = RealHeightSetting.Value;

			var factor = avatarHeight > 0.1f && playerHeight > 0.1f
				? avatarHeight / playerHeight
				: 1f;

			if (Mathf.Approximately(TrackerResolver.PoseScale, factor))
				return;

			TrackerResolver.PoseScale = factor;

			Logger.LogDebug($"Player pose x{factor:F3} (avatar {avatarHeight:F2} m / player {playerHeight:F2} m).", this, tag: nameof(XRControllerV2));
		}
	}
}
