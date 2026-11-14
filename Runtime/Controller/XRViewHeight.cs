using System.Linq;
using Nox.Avatars;
using Nox.Avatars.Scale;

namespace Nox.XR.Runtime {
	/// <summary>
	/// Height the XR view has to sit at for an avatar: the height of its <b>eyes</b> above its feet, which is what
	/// the rig solves from.
	/// <para>
	/// Aiming at the avatar's stature instead leaves its head a few centimetres too high, the legs cannot reach
	/// the floor from the eye position and the avatar ends up standing on the tips of its toes (or hanging from
	/// the camera) after a recenter.
	/// </para>
	/// </summary>
	public static class XRViewHeight {
		/// <summary>
		/// Eye height of the avatar of <paramref name="descriptor"/>, in metres, or <c>0</c> when it exposes no size
		/// (the caller then falls back to its default view height).
		/// </summary>
		public static float EyeHeight(IAvatarDescriptor descriptor)
			=> descriptor?
				.GetModules<IScaleAvatarModule>()
				.FirstOrDefault()?
				.EyeHeight ?? 0f;
	}
}
