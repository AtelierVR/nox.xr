using Nox.CCK.Utils;
using UnityEngine.XR.Management;

namespace Nox.XR.Loaders {
	/// <summary>
	/// Volet <b>éditeur</b> d'un loader XR : permet à nox.xr de configurer XR Plug-in Management
	/// sans connaître le type, la priorité ni l'asset d'aucun loader.
	///
	/// <para>
	/// L'implémentation est enregistrée auprès de <c>XRLoaderEditorRegistry</c> quand le mod
	/// s'initialise ; nox.xr en déduit la liste des loaders à déclarer pour la plateforme
	/// courante. C'est ce qui remplace les tables de types et les chemins d'assets codés en dur.
	/// </para>
	/// </summary>
	public interface IXRLoaderEditorProvider : IXRLoaderProvider {
		/// <summary>
		/// Indique si ce loader doit être déclaré dans XR Plug-in Management sur
		/// <paramref name="platform"/> (ex. OpenXR n'est pas déclaré sur Linux, où Unity ne
		/// fournit pas de loader OpenXR).
		/// </summary>
		bool IsSupported(Platform platform);

		/// <summary>
		/// Asset de loader à enregistrer dans XR Plug-in Management.
		/// <c>null</c> s'il est introuvable : nox.xr ignore alors ce loader plutôt que
		/// d'enregistrer une référence morte.
		/// </summary>
		XRLoader Loader { get; }
	}
}
