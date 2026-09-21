#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using Nox.CCK.Utils;
using Nox.CCK.XR;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEngine.XR.Management;

namespace Nox.XR.Editor {
	/// <summary>
	/// Configure XR Plug-in Management : démarrage au lancement et liste des loaders.
	///
	/// <para>
	/// La liste des loaders est reconstruite pour la cible de build courante : XR Management
	/// essaie les loaders <b>dans l'ordre</b> et retient le premier qui s'initialise
	/// (<c>XRManagerSettings.InitializeLoader</c>). L'ordre encode donc la préférence par OS —
	/// il vient de la priorité des <c>IXRLoaderEditorProvider</c>, qui savent seuls sur
	/// quelles plateformes ils ont un sens. nox.xr ne connaît ni type de loader ni chemin d'asset.
	/// </para>
	/// </summary>
	public class XRSettingsSetup : IEditorModInitializer {
		public void OnInitializeEditor(IEditorModCoreAPI api) {
			ApplyXRStartupSettings();

			// nox.xr s'initialise avant les mods de loader (contrainte `after: xr`) : on rejoue
			// la configuration chaque fois qu'un provider arrive, au lieu d'attendre les autres.
			XRLoaderEditorRegistry.Changed.AddListener(ApplyPlatformLoaders);
			ApplyPlatformLoaders();
		}

		public void OnDisposeEditor() {
			XRLoaderEditorRegistry.Changed.RemoveListener(ApplyPlatformLoaders);
		}

		/// <summary>
		/// Désactive « Initialize XR on Startup » partout sauf Android XR et Vision OS, les seules
		/// plateformes qui doivent démarrer la XR automatiquement (ailleurs c'est nox.xr qui pilote).
		/// </summary>
		private static void ApplyXRStartupSettings() {
			var dirty = false;

			foreach (BuildTargetGroup group in Enum.GetValues(typeof(BuildTargetGroup))) {
				if (group == BuildTargetGroup.Unknown)
					continue;

				var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
				if (settings == null)
					continue;

				var shouldEnable = group == BuildTargetGroup.Android
					|| group == BuildTargetGroup.VisionOS;

				if (settings.InitManagerOnStart == shouldEnable)
					continue;

				settings.InitManagerOnStart = shouldEnable;
				EditorUtility.SetDirty(settings);
				dirty = true;
			}

			if (dirty)
				AssetDatabase.SaveAssets();
		}

		/// <summary>
		/// Déclare, pour la cible de build courante, les loaders des providers enregistrés qui
		/// supportent la plateforme, du plus prioritaire au moins prioritaire.
		/// Sans effet si aucun provider n'est présent, pour ne jamais vider la configuration
		/// d'un projet qui n'utilise pas ces mods.
		/// </summary>
		private static void ApplyPlatformLoaders() {
			// En Play, XR Management tient déjà ses instances : toucher la liste la casserait.
			if (EditorApplication.isPlaying)
				return;

			// `EditorUserBuildSettings.activeBuildTargetGroup` a été retiré : le groupe se déduit
			// de la cible active, comme dans `PlatformExtensions` (nox.cck).
			var group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
			if (group == BuildTargetGroup.Unknown)
				return;

			var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
			var manager = settings?.Manager;
			if (manager == null)
				return;

			var platform = PlatformExtensions.CurrentPlatform;
			var loaders = new List<XRLoader>();

			foreach (var provider in XRLoaderEditorRegistry.Registered
					.Where(p => p.IsSupported(platform))
					.OrderByDescending(p => p.Priority)) {
				var loader = provider.Loader;
				if (loader == null) {
					Logger.LogWarning($"XR loader '{provider.Id}' supports {platform} but its loader asset was not found; skipping it.");
					continue;
				}

				loaders.Add(loader);
			}

			if (loaders.Count == 0)
				return;

			if (SameLoaders(manager.activeLoaders, loaders))
				return;

			if (!manager.TrySetLoaders(loaders)) {
				Logger.LogWarning($"Could not set the XR loaders for {platform}: {Describe(loaders)}.");
				return;
			}

			EditorUtility.SetDirty(settings);
			AssetDatabase.SaveAssets();
			Logger.Log($"XR loaders for {platform}: {Describe(loaders)}");
		}

		/// <summary>
		/// Compare les loaders par <b>type</b> : deux assets du même loader (celui créé par XR
		/// Plug-in Management et celui livré par un mod) sont équivalents, et comparer les
		/// références ferait réécrire l'asset à chaque session.
		/// </summary>
		private static bool SameLoaders(IReadOnlyList<XRLoader> current, List<XRLoader> wanted) {
			if (current.Count != wanted.Count)
				return false;

			for (var i = 0; i < wanted.Count; i++)
				if (current[i]?.GetType() != wanted[i].GetType())
					return false;

			return true;
		}

		private static string Describe(IEnumerable<XRLoader> loaders)
			=> string.Join(", ", loaders.Select(l => l.GetType().Name));
	}
}
#endif
