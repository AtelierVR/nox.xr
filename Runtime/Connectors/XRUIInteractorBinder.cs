using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit.UI;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Runtime.Connectors {
	/// <summary>
	/// Keeps the hand interactors (near/far rays and pokes) registered with the UI input module the game really
	/// uses.
	///
	/// <para>
	/// XRI resolves the module through a global component cache and only when the interactor is enabled. An
	/// interactor created while another controller is still around - the desktop one, when the player switches
	/// from desktop to VR - binds to that controller's module, which sits on an inactive EventSystem: its rays
	/// and pokes then never reach the menu (3D interaction still works, it does not go through the module).
	/// Re-registering the interactor moves it for good, the later enable/disable cycles of the interactor
	/// (grabbing, finger poses) then use the right module.
	/// </para>
	/// </summary>
	public static class XRUIInteractorBinder {
		/// <summary>UI input module of the active EventSystem, <c>null</c> while there is none.</summary>
		public static XRUIInputModule Current() {
			var eventSystem = EventSystem.current;
			return eventSystem ? eventSystem.GetComponent<XRUIInputModule>() : null;
		}

		/// <summary>
		/// Registers an interactor with <paramref name="module"/>, dropping every other one: XRI keeps the module
		/// it used before in its own cache and would otherwise keep talking to it.
		/// </summary>
		public static void Bind(IUIInteractor interactor, XRUIInputModule module) {
			if (interactor == null || !module || IsBound(interactor, module))
				return;

			foreach (var other in Object.FindObjectsByType<XRUIInputModule>(FindObjectsInactive.Include))
				if (other != module)
					other.UnregisterInteractor(interactor);

			ForgetResolvedModule(interactor);
			module.RegisterInteractor(interactor);
		}

		/// <summary>True when the module already knows this interactor, whatever its state.</summary>
		public static bool IsBound(IUIInteractor interactor, XRUIInputModule module)
			=> interactor != null && module && module.GetTrackedDeviceModel(interactor, out _);

		private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
		private const string CacheField      = "m_RegisteredUIInteractorCache";
		private const string ModuleField     = "m_InputModule";
		private const string RegisteredField = "m_RegisteredInputModule";

		/// <summary>
		/// Drops the module XRI resolved for this interactor: it keeps it in a private cache, only ever clears it
		/// when that module is destroyed, and logs a warning whenever the interactor is registered with another
		/// module while the cache still holds the previous one. Does nothing when the cache is not found, the
		/// registration then just logs that warning.
		/// </summary>
		private static void ForgetResolvedModule(IUIInteractor interactor) {
			try {
				for (var type = interactor.GetType(); type != null; type = type.BaseType) {
					var cache = type.GetField(CacheField, Flags)?.GetValue(interactor);
					if (cache == null)
						continue;

					var cacheType = cache.GetType();
					cacheType.GetField(ModuleField, Flags)?.SetValue(cache, null);
					cacheType.GetField(RegisteredField, Flags)?.SetValue(cache, null);
					return;
				}
			} catch (System.Exception e) {
				Logger.LogDebug($"{nameof(XRUIInteractorBinder)}: XRI UI module cache not cleared ({e.GetType().Name}).");
			}
		}
	}
}
