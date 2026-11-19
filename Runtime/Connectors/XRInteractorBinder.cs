using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.UI;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Runtime.Connectors {
	/// <summary>
	/// Binds a hand interactor to its rig: the interaction manager and the UI input module in use.
	///
	/// <para>
	/// XRI resolves both references once, when the interactor is enabled, through global component caches. An
	/// interactor created around a controller change - the player switching from desktop to VR - then keeps
	/// whatever was current at that instant: the desktop controller's UI module (on an inactive EventSystem) or
	/// no interaction manager at all. The pokes and the near/far rays are the interactors the rig builds itself,
	/// so they are the ones left inert while everything else keeps working.
	/// </para>
	/// </summary>
	public static class XRInteractorBinder {
		/// <summary>UI input module of the active EventSystem, <c>null</c> while there is none.</summary>
		public static XRUIInputModule Current() {
			var eventSystem = EventSystem.current;
			return eventSystem ? eventSystem.GetComponent<XRUIInputModule>() : null;
		}

		/// <summary>
		/// Binds an interactor: the interaction manager is always resolved (setting it is enough for a disabled
		/// interactor, XRI registers it on its next enable), the UI module only while the interactor is on,
		/// since a disabled one must not stay a UI pointer.
		/// </summary>
		public static void Bind(XRBaseInteractor interactor, XRUIInputModule module) {
			if (interactor == null)
				return;

			BindManager(interactor);

			if (module && interactor.isActiveAndEnabled)
				BindModule(interactor, module);
		}

		/// <summary>
		/// Gives the interactor the interaction manager of its rig: XRI resolves it once, at enable, and the rig
		/// manager sits on a child of the rig root, next to the rig nodes.
		/// </summary>
		private static void BindManager(XRBaseInteractor interactor) {
			if (interactor.interactionManager)
				return;

			var manager = interactor.GetComponentInParent<XRInteractionManager>(true);
			if (!manager)
				manager = interactor.transform.root.GetComponentInChildren<XRInteractionManager>(true);
			if (!manager)
				return;

			interactor.interactionManager = manager;
		}

		/// <summary>
		/// Registers the interactor with <paramref name="module"/>, after dropping every other one: XRI keeps the
		/// module it used before in its own cache and would otherwise keep talking to it.
		/// </summary>
		private static void BindModule(XRBaseInteractor interactor, XRUIInputModule module) {
			if (!(interactor is IUIInteractor uiInteractor) || IsBound(uiInteractor, module))
				return;

			foreach (var other in Object.FindObjectsByType<XRUIInputModule>(FindObjectsInactive.Include))
				if (other != module)
					other.UnregisterInteractor(uiInteractor);

			ForgetResolvedModule(uiInteractor);
			module.RegisterInteractor(uiInteractor);
		}

		/// <summary>True when the module already knows this interactor, whatever its state.</summary>
		public static bool IsBound(IUIInteractor interactor, XRUIInputModule module)
			=> interactor != null && module && module.GetTrackedDeviceModel(interactor, out _);

		private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
		private const string CacheField      = "m_RegisteredUIInteractorCache";
		private const string ModuleField     = "m_InputModule";
		private const string RegisteredField = "m_RegisteredInputModule";

		private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.NonPublic;
		private const string HitDataField   = "s_InteractorHitData";
		private const string HitRaycasterField = "s_InteractorRaycasters";

		private static FieldInfo _hitDataField;
		private static FieldInfo _hitRaycasterField;

		/// <summary>
		/// Drops the element the graphic raycaster remembers for a poke.
		/// <para>
		/// <see cref="TrackedDeviceGraphicRaycaster"/> keeps one hit per interactor and only replaces it with a hit
		/// that sorts <b>behind</b> it. The first element a fingertip reaches is often a label inside a card, i.e.
		/// deeper in the canvas than the card itself: it then stays the remembered hit, every later element is
		/// rejected, the interactor is never marked as selecting again, and the following buttons stop clicking -
		/// even though the poke does meet the select requirements. The only way out used to be a disabled poke.
		/// Forgetting the hit while the poke is not selecting gives the next raycast a clean start.
		/// </para>
		/// </summary>
		public static void ForgetUiHit(IUIInteractor interactor) {
			if (interactor == null)
				return;

			try {
				_hitDataField ??= typeof(TrackedDeviceGraphicRaycaster).GetField(HitDataField, StaticFlags);
				(_hitDataField?.GetValue(null) as System.Collections.IDictionary)?.Remove(interactor);

				_hitRaycasterField ??= typeof(TrackedDeviceGraphicRaycaster).GetField(HitRaycasterField, StaticFlags);
				(_hitRaycasterField?.GetValue(null) as System.Collections.IDictionary)?.Remove(interactor);
			} catch (System.Exception e) {
				Logger.LogDebug($"{nameof(XRInteractorBinder)}: UI poke hit not cleared ({e.GetType().Name}).");
			}
		}

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
				Logger.LogDebug($"{nameof(XRInteractorBinder)}: XRI UI module cache not cleared ({e.GetType().Name}).");
			}
		}
	}
}
