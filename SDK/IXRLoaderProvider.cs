using Cysharp.Threading.Tasks;
using Nox.XR.Bindings;

namespace Nox.XR.Loaders {
	/// <summary>
	/// Fournisseur d'un loader XR (OpenXR, OpenVR/SteamVR, ...).
	///
	/// <para>
	/// nox.xr ne connaît aucun loader : il expose cette interface et laisse les paquets
	/// spécialisés (<c>nox.xr.openxr</c>, <c>nox.xr.openvr</c>) en fournir une implémentation.
	/// Au démarrage, <see cref="XRLoaderManager"/> interroge les mods chargés, écarte ceux dont
	/// <see cref="IsValid"/> est faux, puis pilote celui de plus haute priorité.
	/// </para>
	///
	/// <para>
	/// L'implémentation est récupérée via <c>IMod.GetInstances&lt;IXRLoaderProvider&gt;()</c> :
	/// la classe qui implémente l'interface doit donc être instanciée par le mod
	/// (entrée <c>main</c> du <c>nox.mod.json</c>, comme <c>Nox.XR.Runtime.Main</c>).
	/// </para>
	/// </summary>
	public interface IXRLoaderProvider {
		/// <summary>
		/// Identifiant du loader (ex. <c>"openxr"</c>, <c>"openvr"</c>).
		/// Utilisé pour le log et la déduplication.
		/// </summary>
		string Id { get; }

		/// <summary>
		/// Priorité du loader : quand plusieurs sont valides, le plus élevé gagne.
		/// Ex. OpenXR (20) l'emporte sur OpenVR (10) sur Windows ; sur Linux OpenXR est
		/// invalide, donc OpenVR est choisi.
		/// </summary>
		int Priority { get; }

		/// <summary>
		/// Indique si ce loader peut réellement être utilisé sur l'OS et le build courants
		/// (ex. OpenXR renvoie faux sur Linux, où Unity ne fournit pas de loader).
		/// nox.xr n'appelle <see cref="Initialize"/> que si cette propriété est vraie.
		/// </summary>
		bool IsValid { get; }

		/// <summary>
		/// Bindings de ce runtime, lus par clé (<c>"move"</c>, <c>"jump"</c>, ...).
		///
		/// <para>
		/// C'est le mod de loader qui les possède : il sait quels contrôles existent sur les
		/// devices qu'il pilote. nox.xr se contente de déclencher <see cref="IBinding.Refresh"/> à
		/// la création du proxy XR et à chaque changement de device, puis de relayer les valeurs
		/// aux consommateurs.
		/// </para>
		///
		/// <para>
		/// <c>null</c> tant que le loader n'a pas été initialisé (ou pour un loader qui ne
		/// fournit pas de bindings, comme le repli générique de XR Plug-in Management).
		/// </para>
		/// </summary>
		IBinding Binding { get; }

		/// <summary>
		/// Démarre le loader et ses sous-systèmes.
		/// </summary>
		/// <returns>
		/// <c>false</c> si le démarrage a échoué : nox.xr essaie alors le loader valide suivant.
		/// </returns>
		UniTask<bool> Initialize();

		/// <summary>
		/// Arrête le loader et libère ses sous-systèmes.
		/// </summary>
		UniTask Deinitialize();
	}
}
