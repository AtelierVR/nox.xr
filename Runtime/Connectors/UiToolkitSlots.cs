using System.Reflection;


namespace Nox.XR.Runtime.Connectors {
	/// <summary>
	/// Frees the UI Toolkit registration of an interactor.
	/// <para>
	/// The toolkit hands out eight slots for the whole rig and keeps one per enabled poke and per ray, so a hand with five
	/// pokes exhausts them with the ray of the other hand and the next poke is dropped with a registration error. This
	/// project does not use UI Toolkit world space (no <c>UIDocument</c>), so those slots are pure overhead: the
	/// registration is released as soon as it is taken and every finger keeps its own poke. The uGUI path
	/// (<c>XRUIInputModule</c>) is a different registry and is left untouched.
	/// </para>
	/// </summary>
	internal static class UiToolkitSlots {
		private const string HandlerType = "UnityEngine.XR.Interaction.Toolkit.UI.XRUIToolkitHandler";
		private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

		private static bool _resolved;
		private static PropertyInfo _supported;
		private static MethodInfo _unregister;

		/// <summary>Whether the project actually uses UI Toolkit world space: the slots are then kept.</summary>
		private static bool Supported {
			get {
				Resolve();

				return _supported == null || _supported.GetValue(null) is not bool supported || supported;
			}
		}

		public static void Release(UnityEngine.XR.Interaction.Toolkit.Interactors.IXRInteractor interactor) {
			if (interactor == null)
				return;

			Resolve();

			if (_unregister == null || Supported)
				return;

			_unregister.Invoke(null, new object[] { interactor });
		}

		private static void Resolve() {
			if (_resolved)
				return;

			_resolved = true;

			var handler = typeof(UnityEngine.XR.Interaction.Toolkit.Interactors.IXRInteractor).Assembly.GetType(HandlerType);
			if (handler == null)
				return;

			_supported  = handler.GetProperty("uiToolkitSupportEnabled", Flags);
			_unregister = handler.GetMethod("Unregister", Flags, null, new[] { typeof(UnityEngine.XR.Interaction.Toolkit.Interactors.IXRInteractor) }, null);
		}
	}
}
