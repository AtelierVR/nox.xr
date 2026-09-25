using Nox.XR.Bindings;
using Nox.XR.Runtime.Loaders;
using UnityEngine;

namespace Nox.XR.Runtime {
	/// <summary>
	/// Accès aux bindings XR pour les consommateurs du proxy XR (connecteurs du prefab).
	///
	/// <para>
	/// Façade de lecture uniquement : les bindings appartiennent au mod de loader actif, qui les
	/// enregistre et les met à jour selon les devices qu'il pilote (voir
	/// <see cref="IXRLoaderProvider.Binding"/>). Ici, on ne fait que relayer les valeurs.
	/// </para>
	///
	/// <para>
	/// Les valeurs sont lues à la demande (<see cref="IBinding.Get{T}"/>), donc toujours à jour :
	/// aucun cache ni abonnement à maintenir ici.
	/// </para>
	/// </summary>
	public static class Keybindings {
		/// <summary>
		/// Bindings du runtime XR actif, ou <c>null</c> si aucun loader n'est démarré.
		/// </summary>
		private static IBinding Binding
			=> XRLoaderManager.Current?.Binding;

		/// <summary>
		/// Gets the value of a specific key binding, read live from the running XR runtime.
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		public static float GetFloatValue(string key)
			=> Binding?.Get<float>(key) ?? 0f;

		/// <summary>
		/// Gets the value of a specific key binding.
		/// </summary>
		/// <param name="binding"></param>
		/// <returns></returns>
		public static float GetFloatValue(XRBinding binding)
			=> GetFloatValue(binding.GetKey());

		/// <summary>
		/// Gets the Vector2 value of a specific key binding, read live from the running XR runtime.
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		public static Vector2 GetVector2Value(string key)
			=> Binding?.Get<Vector2>(key) ?? Vector2.zero;

		/// <summary>
		/// Gets the Vector2 value of a specific key binding.
		/// </summary>
		/// <param name="binding"></param>
		/// <returns></returns>
		public static Vector2 GetVector2Value(XRBinding binding)
			=> GetVector2Value(binding.GetKey());

		/// <summary>
		/// Checks if a specific key binding is pressed.
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		public static bool IsPressed(string key)
			=> GetFloatValue(key) > 0.1f;

		/// <summary>
		/// Checks if a specific key binding is pressed.
		/// </summary>
		/// <param name="binding"></param>
		/// <returns></returns>
		public static bool IsPressed(XRBinding binding)
			=> GetFloatValue(binding) > 0.1f;
	}
}
