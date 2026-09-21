#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
#endif
using UnityEngine.XR.Management;

namespace Nox.XR.Runtime.Loaders {
	/// <summary>
	/// Accès aux assets <see cref="XRLoader"/> du projet.
	///
	/// <para>
	/// Ces assets sont créés par XR Plug-in Management quand un paquet de loader est installé,
	/// ou livrés par le mod de loader lui-même : les providers les retrouvent par type, sans
	/// chemin codé en dur.
	/// </para>
	/// </summary>
	public static class XRLoaderAssets {
		/// <summary>
		/// Premier asset de loader du type <typeparamref name="T"/> trouvé dans le projet.
		/// Toujours <c>null</c> hors éditeur : la sélection des loaders est un travail d'éditeur,
		/// la build embarque la liste déjà sérialisée dans XR Plug-in Management.
		/// </summary>
		public static XRLoader Find<T>() where T : XRLoader {
#if UNITY_EDITOR
			return AssetDatabase.FindAssets("t:XRLoader")
				.Select(AssetDatabase.GUIDToAssetPath)
				// Les assets gérés par XR Plug-in Management vivent dans `Assets/` : on les
				// préfère à ceux livrés par un paquet, pour ne pas faire pointer les réglages
				// du projet vers un mod. Le tri est stable, l'ordre de l'AssetDatabase est gardé.
				.OrderByDescending(path => path.StartsWith("Assets/"))
				.Select(AssetDatabase.LoadAssetAtPath<XRLoader>)
				.OfType<T>()
				.FirstOrDefault();
#else
			return null;
#endif
		}
	}
}
