using Nox.XR.Bindings;
using Nox.XR.Runtime.Loaders;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Runtime {
	/// <summary>
	/// Accès aux bindings XR pour les consommateur du proxy XR (connecteurs du prefab).
	///
	/// <para>
	/// Façade uniquement : les bindings appartiennent au mod de loader actif, qui les enregistre
	/// et répond aux lectures (<see cref="IXRLoaderProvider.Binding"/>). nox.xr ne déclenche que
	/// leur (re)liaison, et relaie les valeurs.
	/// </para>
	///
	/// <para>
	/// Les valeurs sont lues à la demande (<see cref="IBinding.Get{T}"/>), donc toujours à jour :
	/// aucun cache ni abonnement à maintenir ici.
	/// </para>
	/// </summary>
	public static class Keybindings {
		private static bool _hooked;
		private static bool _rebinding;

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

		/// <summary>
		/// Demande au runtime XR actif de (re)lier ses bindings aux devices connectés.
		///
		/// <para>
		/// Appelé à la création du proxy XR, puis à chaque changement de device : une manette peut
		/// apparaître après le casque, ou être remplacée par un autre modèle en cours de session.
		/// </para>
		/// </summary>
		public static void Rebind() {
			if (_rebinding)
				return;

			_rebinding = true;
			try {
				HookInputSystem();

				var binding = Binding;
				if (binding == null) {
					Logger.LogDebug("No XR binding runtime, XR inputs are left unbound.");
					return;
				}

				Logger.LogDebug($"Refreshing XR bindings with '{XRLoaderManager.Current?.Id}'.");
				binding.Refresh();
			} finally {
				_rebinding = false;
			}
		}

		/// <summary>
		/// Délie les bindings XR (arrêt du proxy XR) : le runtime libère ses actions et sauvegarde
		/// les overrides du joueur.
		/// </summary>
		public static void Clear()
			=> Binding?.Clear();

		/// <summary>
		/// Re-lie les bindings quand un device suivi apparaît ou disparaît.
		/// Clavier, souris et autres périphériques non XR sont ignorés : ils ne peuvent pas
		/// changer les contrôles disponibles.
		/// </summary>
		private static void HookInputSystem() {
			if (_hooked)
				return;

			_hooked = true;
			InputSystem.onDeviceChange += OnDeviceChange;
		}

		private static void OnDeviceChange(InputDevice device, InputDeviceChange change) {
			if (device is not TrackedDevice)
				return;

			switch (change) {
				case InputDeviceChange.Added:
				case InputDeviceChange.Removed:
				case InputDeviceChange.Reconnected:
				case InputDeviceChange.UsageChanged:
					break;
				default:
					return;
			}

			if (!XRLoaderManager.IsRunning)
				return;

			Logger.LogDebug($"XR device change ({change}): {device.name}");
			Rebind();
		}
	}
}
