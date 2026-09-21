using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.CCK.XR {
	/// <summary>
	/// Pont vers XR Plug-in Management (<c>Unity.XR.Management</c>), partagé par les loaders.
	///
	/// <para>
	/// <c>XRManagerSettings.InitializeLoader()</c> essaie chaque loader configuré <b>dans l'ordre</b>
	/// et retient le premier qui s'initialise. Un provider qui veut <i>son</i> loader ne peut donc pas
	/// simplement appeler XR Management : il doit imposer l'ordre, ce que fait
	/// <see cref="StartAsync{T}()"/>.
	/// </para>
	/// </summary>
	public static class XRManagementLoader {
		/// <summary>Loader réellement actif, ou <c>null</c> si XR n'est pas démarré.</summary>
		public static XRLoader Active
			=> Manager()?.activeLoader;

		/// <summary>
		/// Indique si un loader du type <typeparamref name="T"/> est configuré pour le build
		/// courant (donc que XR Management pourra le démarrer).
		/// </summary>
		public static bool HasLoader<T>() where T : XRLoader
			=> Configured<T>() != null;

		/// <summary>
		/// Loader du type <typeparamref name="T"/> configuré dans XR Plug-in Management,
		/// ou <c>null</c> s'il n'y figure pas.
		/// </summary>
		public static T Configured<T>() where T : XRLoader
			=> Manager()?.activeLoaders?.OfType<T>().FirstOrDefault();

		/// <summary>
		/// Initialise et démarre le premier loader XR Plug-in Management qui répond.
		/// </summary>
		/// <returns><c>false</c> si aucun loader n'a pu démarrer.</returns>
		public static UniTask<bool> StartAsync()
			=> StartCore(null);

		/// <summary>
		/// Initialise et démarre <b>le</b> loader <typeparamref name="T"/> configuré dans XR
		/// Plug-in Management, sans laisser l'ordre de la liste décider à notre place.
		/// </summary>
		/// <returns>
		/// <c>false</c> si ce loader n'est pas configuré, si XR Management en a démarré un autre
		/// (celui-ci est alors arrêté pour laisser la main au provider suivant) ou s'il n'a pas pu
		/// démarrer.
		/// </returns>
		public static async UniTask<bool> StartAsync<T>() where T : XRLoader {
			var manager = Manager();
			if (manager == null) {
				Logger.LogError("XR Plug-in Management is not configured (no XRGeneralSettings).");
				return false;
			}

			var loader = Configured<T>();
			if (loader == null) {
				Logger.LogError($"XR loader '{typeof(T).Name}' is not configured in XR Plug-in Management.");
				return false;
			}

			// XR Management démarre le premier loader de la liste qui répond : mettre le nôtre en
			// tête est le seul moyen de garantir que c'est bien lui qui démarre (et seulement lui).
			if (!Promote(manager, loader))
				return false;

			return await StartCore(loader);
		}

		/// <summary>
		/// Arrête les sous-systèmes et désinitialise le loader actif.
		/// </summary>
		public static async UniTask Stop() {
			var manager = Manager();
			if (manager == null || !manager.isInitializationComplete)
				return;

			await UniTask.Yield();

			Logger.Log("Stopping XR...");
			manager.StopSubsystems();
			manager.DeinitializeLoader();
			Logger.Log("XR stopped.");
		}

		private static XRManagerSettings Manager()
			=> XRGeneralSettings.Instance?.Manager;

		/// <summary>
		/// Cœur du démarrage. <paramref name="expected"/> est le loader exigé, ou <c>null</c> quand
		/// n'importe quel loader configuré fait l'affaire (repli de nox.xr).
		/// </summary>
		private static async UniTask<bool> StartCore(XRLoader expected) {
			var manager = Manager();
			if (manager == null) {
				Logger.LogError("XR Plug-in Management is not configured (no XRGeneralSettings).");
				return false;
			}

			if (!manager.isInitializationComplete) {
				Logger.Log("Initializing XR...");
				await manager.InitializeLoader().ToUniTask();
			}

			// XR Management appelle déjà `XRLoader.Initialize()` en retenant le premier loader qui
			// répond : ne pas rappeler cette méthode ici, un loader n'est pas idempotent (OpenVR
			// recréerait ses sous-systèmes).
			var loader = manager.activeLoader;
			if (!loader) {
				Logger.LogError(expected != null
					? $"XR loader error: {DescribeFailure(expected)}"
					: "XR loader is not active.");
				return false;
			}

			if (expected != null && loader.GetType() != expected.GetType()) {
				Logger.LogError($"XR Management initialized '{loader.name}' instead of '{expected.name}'; stopping it.");
				await Stop();
				return false;
			}

			Logger.Log($"XR loader initialized: {loader.name}");
			manager.StartSubsystems();
			Logger.Log("XR initialized. Subsystems started.");
			return true;
		}

		/// <summary>
		/// Place <paramref name="loader"/> en tête de la liste configurée, en gardant les autres
		/// loaders derrière lui (l'ordre est celui de XR Plug-in Management, pas une copie locale).
		/// </summary>
		private static bool Promote(XRManagerSettings manager, XRLoader loader) {
			var loaders = manager.activeLoaders;
			if (loaders == null || loaders.Count == 0 || loaders[0] == loader)
				return true;

			var reordered = loaders.Where(l => l != loader).ToList();
			reordered.Insert(0, loader);

			if (manager.TrySetLoaders(reordered))
				return true;

			Logger.LogError($"Could not move XR loader '{loader.name}' to the front of the loader list.");
			return false;
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
