using System.Collections.Generic;

namespace Nox.XR.Trackers {
	/// <summary>
	/// Trackers d'un runtime XR, lus par nox.xr.
	///
	/// <para>
	/// C'est l'objet qu'un loader rend via <c>IXRLoaderProvider.Trackers</c>, exactement comme
	/// <see cref="Bindings.IBinding"/> pour les entrées. Le <b>mod de loader en est le
	/// propriétaire</b> : il sait quels trackers son runtime expose et comment les lire. nox.xr ne
	/// fait que les relayer au suivi du corps entier — il ne connaît aucun runtime, ni OpenXR, ni
	/// OpenVR, et c'est lui qui décide à quoi sert un tracker (calibration), jamais le provider.
	/// </para>
	///
	/// <para>
	/// Plusieurs loaders peuvent en fournir en même temps : les trackers de tous les loaders
	/// chargés sont lus (celui du runtime actif d'abord), puis dédupliqués par
	/// <see cref="TrackerPose.Id"/>. Un loader peut donc n'exposer que les trackers que les autres
	/// ne voient pas.
	/// </para>
	/// </summary>
	public interface ITrackerProvider {
		/// <summary>
		/// Identifiant du runtime (ex. <c>"openvr"</c>), utilisé pour le log et les diagnostics.
		/// </summary>
		string Id { get; }

		/// <summary>
		/// Indique si ce runtime peut réellement fournir des trackers maintenant (session ouverte,
		/// serveur joignable...). Un provider indisponible est ignoré.
		/// </summary>
		bool IsAvailable { get; }

		/// <summary>État en une ligne, pour les diagnostics (panneau, opérateurs, terminal).</summary>
		string Describe();

		/// <summary>
		/// Ajoute les trackers utilisables à <paramref name="into"/>.
		/// </summary>
		/// <param name="excludeHanded">Ignorer les devices actuellement utilisés comme mains.</param>
		/// <param name="excludeControllers">Ignorer les devices que le runtime signale comme manettes.</param>
		void Get(List<TrackerPose> into, bool excludeHanded, bool excludeControllers);
	}
}
