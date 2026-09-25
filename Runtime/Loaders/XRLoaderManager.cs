using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Nox.CCK.Mods.Mods;
using Nox.XR.Loaders;
using Nox.XR.Runtime.Settings;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Runtime.Loaders {
	/// <summary>
	/// Choisit et pilote le loader XR à utiliser.
	///
	/// <para>
	/// Les fournisseurs viennent des mods chargés (<c>nox.xr.openxr</c>, <c>nox.xr.openvr</c>, ...)
	/// auxquels s'ajoute toujours <see cref="XRManagementLoaderProvider"/>, le repli générique de
	/// nox.xr qui laisse XR Plug-in Management démarrer ce qui est configuré. Le tri se fait sur
	/// <see cref="IXRLoaderProvider.IsValid"/> (support de l'OS/build) puis sur la priorité.
	/// </para>
	/// </summary>
	public static class XRLoaderManager {
		/// <summary>Fournisseurs trouvés dans les mods chargés.</summary>
		private static readonly List<IXRLoaderProvider> Discovered = new();

		/// <summary>
		/// Repli : XR Plug-in Management démarre le loader configuré, quel qu'il soit.
		/// </summary>
		private static readonly IXRLoaderProvider Fallback = new XRManagementLoaderProvider();

		/// <summary>Loader en cours d'utilisation, ou <c>null</c>.</summary>
		public static IXRLoaderProvider Current { get; private set; }

		public static bool IsRunning 
			=> Current != null;

		/// <summary>
		/// Fournisseurs connus, triés par priorité décroissante (repli inclus).
		///
		/// <para>
		/// Le loader choisi à la main (<see cref="XRLoaderPreference"/>) passe devant, quelle que soit
		/// sa priorité — c'est tout l'intérêt du réglage. Il ne dispense pas d'être valide :
		/// <see cref="Start"/> l'ignore si ce n'est pas le cas et poursuit la chaîne.
		/// </para>
		/// </summary>
		public static IReadOnlyList<IXRLoaderProvider> Providers
			=> Discovered
				.Append(Fallback)
				.OrderByDescending(p => XRLoaderPreference.IsPreferred(p.Id))
				.ThenByDescending(p => p.Priority)
				.ToList();

		/// <summary>
		/// Scanne les mods chargés à la recherche de <see cref="IXRLoaderProvider"/>.
		/// Les mods qui ne référencent pas <c>Nox.XR</c> sont simplement ignorés.
		/// </summary>
		public static void Discover(IModAPI modAPI) {
			Discovered.Clear();
			if (modAPI == null)
				return;

			foreach (var mod in modAPI.GetMods()) {
				if (mod == null || !mod.IsLoaded())
					continue;

				IXRLoaderProvider[] found;
				try {
					found = mod.GetInstances<IXRLoaderProvider>();
				} catch (Exception e) {
					Logger.LogDebug($"Mod '{mod.GetMetadata()?.GetId() ?? "?"}' does not provide XR loaders: {e.Message}");
					continue;
				}

				if (found == null)
					continue;

				foreach (var provider in found.Where(p => p != null))
					if (Discovered.All(p => p.Id != provider.Id))
						Discovered.Add(provider);
			}

			if (Discovered.Count > 0)
				Logger.LogDebug($"XR loader providers found: {string.Join(", ", Discovered.Select(p => p.Id))}");
		}

		/// <summary>
		/// Démarre le premier loader valide, par ordre de priorité.
		/// </summary>
		/// <returns><c>false</c> si aucun loader n'a pu démarrer.</returns>
		public static async UniTask<bool> Start(IModAPI modAPI) {
			if (IsRunning) {
				Logger.LogWarning("XR already initialized.");
				return true;
			}

			Discover(modAPI);

			foreach (var provider in Providers) {
				if (!provider.IsValid)
					continue;

				Logger.Log($"Starting XR loader '{provider.Id}' (priority {provider.Priority})...");
				if (!await provider.Initialize()) {
					Logger.LogWarning($"XR loader '{provider.Id}' failed to initialize; trying the next one.");
					continue;
				}

				Current = provider;
				return true;
			}

			Logger.LogError("No valid XR loader found for this platform.");
			return false;
		}

		/// <summary>
		/// Arrête le loader courant. Sans effet si XR n'est pas démarré.
		/// </summary>
		public static async UniTask Stop() {
			if (!IsRunning) {
				Logger.LogWarning("XR not initialized.");
				return;
			}

			await Current.Deinitialize();
			Current = null;
		}
	}
}
