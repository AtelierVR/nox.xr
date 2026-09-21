using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Runtime.Loaders {
	/// <summary>
	/// Pont vers XR Plug-in Management (<c>Unity.XR.Management</c>), partagé par les loaders.
	///
	/// <para>
	/// <c>XRManagerSettings.InitializeLoader()</c> essaie chaque loader configuré dans l'ordre
	/// et retient le premier qui s'initialise : c'est ce mécanisme qui fait le tri entre
	/// OpenXR (Windows/Android) et OpenVR (Windows/Linux) sans que nox.xr ait à connaître
	/// leurs types.
	/// </para>
	/// </summary>
	public static class XRManagementLoader {
		/// <summary>Loader réellement actif, ou <c>null</c> si XR n'est pas démarré.</summary>
		public static XRLoader Active
			=> XRGeneralSettings.Instance?.Manager?.activeLoader;

		/// <summary>
		/// Indique si un loader du type <typeparamref name="T"/> est configuré pour le build
		/// courant (donc que XR Management pourra le démarrer).
		/// </summary>
		public static bool HasLoader<T>() where T : XRLoader {
			var loaders = XRGeneralSettings.Instance?.Manager?.activeLoaders;
			return loaders != null && loaders.Any(l => l is T);
		}

		/// <summary>
		/// Initialise et démarre le premier loader XR Plug-in Management qui répond.
		/// </summary>
		/// <returns><c>false</c> si aucun loader n'a pu démarrer.</returns>
		public static async UniTask<bool> StartAsync() {
			var manager = XRGeneralSettings.Instance?.Manager;
			if (manager == null) {
				Logger.LogError("XR Plug-in Management is not configured (no XRGeneralSettings).");
				return false;
			}

			if (!manager.isInitializationComplete) {
				Logger.Log("Initializing XR...");
				await manager.InitializeLoader().ToUniTask();
			}

			var loader = manager.activeLoader;
			if (!loader) {
				Logger.LogWarning("XR loader is not active.");
				return false;
			}

			Logger.Log("Loading XR...");
			if (!loader.Initialize()) {
				Logger.LogError($"XR loader error: {DescribeFailure(loader)}");
				return false;
			}

			Logger.Log($"XR loader initialized: {loader.name}");
			manager.StartSubsystems();
			Logger.Log("XR initialized. Subsystems started.");
			return true;
		}

		/// <summary>
		/// Arrête les sous-systèmes et désinitialise le loader actif.
		/// </summary>
		public static void Stop() {
			var manager = XRGeneralSettings.Instance?.Manager;
			if (manager == null)
				return;

			Logger.Log("Stopping XR...");
			manager.StopSubsystems();
			manager.DeinitializeLoader();
			Logger.Log("XR stopped.");
		}

		/// <summary>
		/// Décrit la sous-système manquant quand un loader refuse de s'initialiser.
		/// </summary>
		private static string DescribeFailure(XRLoader loader)
			=> loader.GetLoadedSubsystem<XRDisplaySubsystem>() == null
				? "Display subsystem is null."
				: loader.GetLoadedSubsystem<XRInputSubsystem>() == null
					? "Input subsystem is null."
					: "Unknown error.";
	}
}
