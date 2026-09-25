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
		/// <summary>
		/// Vrai dès que l'éditeur se démonte (rechargement de domaine, fermeture) : pendant cette
		/// phase les mods se désenregistrent un par un, et le registre des providers n'est plus
		/// représentatif de rien.
		/// </summary>
		private static bool _suspended;

		public void OnInitializeEditor(IEditorModCoreAPI api) {
			ApplyXRStartupSettings();

			_suspended = false;

			// nox.xr s'initialise avant les mods de loader (contrainte `after: xr`) : on rejoue
			// la configuration chaque fois qu'un provider arrive, au lieu d'attendre les autres.
			XRLoaderEditorRegistry.Changed.AddListener(ApplyPlatformLoaders);
			AssemblyReloadEvents.beforeAssemblyReload += Suspend;
			EditorApplication.quitting                += Suspend;
			EditorApplication.playModeStateChanged    += OnPlayModeChanged;
			ApplyPlatformLoaders();
		}

		public void OnDisposeEditor() {
			XRLoaderEditorRegistry.Changed.RemoveListener(ApplyPlatformLoaders);
			AssemblyReloadEvents.beforeAssemblyReload -= Suspend;
			EditorApplication.quitting                -= Suspend;
			EditorApplication.playModeStateChanged    -= OnPlayModeChanged;
		}

		private static void Suspend()
			=> _suspended = true;

		/// <summary>
		/// Dernière réconciliation avant d'entrer en Play. Sans elle, une liste restée incomplète
		/// (session précédente interrompue pendant un rechargement de domaine) priverait OpenXR de
		/// son loader — et `IsValid` étant défini sur cette même liste, nox.xr le sauterait au
		/// profit d'OpenVR pour toute la session, sans moyen de rattraper le coup en Play.
		/// </summary>
		private static void OnPlayModeChanged(PlayModeStateChange state) {
			if (state == PlayModeStateChange.ExitingEditMode)
				ApplyPlatformLoaders();
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
		/// supportent la plateforme, du plus prioritaire au moins prioritaire : OpenXR (20) devant
		/// OpenVR (10), qui lui-même devance le repli de nox.xr (0).
		///
		/// <para>
		/// Les loaders déjà configurés qu'aucun provider enregistré ne revendique sont conservés,
		/// et la configuration n'est jamais vidée. Un loader retiré ici ne pourrait pas toujours
		/// revenir tout seul : <c>IXRLoaderProvider.IsValid</c> de certains providers (OpenXR)
		/// est défini sur la présence de leur loader dans cette liste, donc une suppression les
		/// empêcherait de se redéclarer.
		/// </para>
		/// </summary>
		private static void ApplyPlatformLoaders() {
			// En Play, XR Management tient déjà ses instances : toucher la liste la casserait.
			if (EditorApplication.isPlaying)
				return;

			// L'éditeur se démonte : les providers s'en vont un par un (`OnDisposeMain` →
			// `XRLoaderEditorRegistry.Unregister` → `Changed`), donc le registre est incomplet.
			// Écrire la config maintenant en retirerait des loaders parfaitement valides.
			if (_suspended)
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

			var current = manager.activeLoaders;
			if (current == null)
				return;

			var platform = PlatformExtensions.CurrentPlatform;

			// Un provider = un asset à lire. `Loader` relit l'AssetDatabase à chaque appel : on
			// l'évalue une fois par provider, y compris ceux qui ne supportent pas la plateforme —
			// il faut connaître leur type pour savoir qu'il faut les retirer.
			var providers = XRLoaderEditorRegistry.Registered.OrderByDescending(p => p.Priority).ToList();
			var assets    = providers.Select(p => p.Loader).ToList();

			// 1) Les loaders des providers qui supportent la plateforme, par priorité décroissante :
			//    c'est cet ordre que XR Plug-in Management puis nox.xr suivront. OpenXR (priorité 20)
			//    passe donc devant OpenVR (10) partout où Unity fournit un loader OpenXR.
			var wanted = new List<XRLoader>();
			var owned  = new HashSet<Type>();

			for (var i = 0; i < providers.Count; i++) {
				var loader = assets[i];
				if (loader != null)
					owned.Add(loader.GetType());   // ce type a un propriétaire, supporté ou non

				if (!providers[i].IsSupported(platform))
					continue;

				if (loader == null) {
					Logger.LogWarning($"XR loader '{providers[i].Id}' supports {platform} but its loader asset was not found; skipping it.");
					continue;
				}

				// Deux providers peuvent viser le même type de loader : XR Management ne doit le
				// contenir qu'une fois.
				if (wanted.All(l => l.GetType() != loader.GetType()))
					wanted.Add(loader);
			}

			// 2) Les loaders déjà configurés qu'aucun provider enregistré ne revendique sont
			//    conservés : ils peuvent appartenir à un provider momentanément absent du registre
			//    (mod en cours de démontage, asset introuvable le temps d'un import). Les retirer ici
			//    les condamnerait définitivement, car un provider comme OpenXR conditionne `IsValid`
			//    à leur présence dans cette liste — il ne pourrait plus jamais se redéclarer.
			foreach (var loader in current) {
				if (loader == null || owned.Contains(loader.GetType()))
					continue;

				owned.Add(loader.GetType());
				wanted.Add(loader);
			}

			// Sans provider, on ne touche à rien : ne jamais vider la configuration d'un projet
			// qui n'utilise pas ces mods.
			if (wanted.Count == 0)
				return;

			if (SameLoaders(current, wanted))
				return;

			if (!manager.TrySetLoaders(wanted)) {
				Logger.LogWarning($"Could not set the XR loaders for {platform}: {Describe(wanted)}.");
				return;
			}

			EditorUtility.SetDirty(settings);
			AssetDatabase.SaveAssets();
			Logger.Log($"XR loaders for {platform}: {Describe(wanted)}");
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
