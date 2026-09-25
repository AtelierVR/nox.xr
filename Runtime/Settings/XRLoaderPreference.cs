using System;
using Nox.CCK.Utils;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// Loader XR choisi à la main, pour passer outre la priorité des providers
	/// (<c>openxr</c> à 20 devant <c>openvr</c> à 10, sur les plateformes où Unity fournit OpenXR).
	///
	/// <para>
	/// Vide = aucune préférence : la priorité décide, comme avant. La préférence ne fait que
	/// <b>changer l'ordre d'essai</b> (<see cref="Nox.XR.Runtime.Loaders.XRLoaderManager.Providers"/>),
	/// jamais forcer un loader : un loader préféré mais invalide sur la plateforme est ignoré et la
	/// chaîne de repli continue. C'est ce qui rend le réglage sûr — on ne peut pas casser la VR en
	/// choisissant un loader qui ne sait pas démarrer ici.
	/// </para>
	///
	/// <para>
	/// Le choix est relu au démarrage de la XR : en changer pendant une session n'a d'effet qu'après
	/// un arrêt/redémarrage (le panel s'en charge, voir <c>XRPanel.SwitchLoader</c>).
	/// </para>
	/// </summary>
	public static class XRLoaderPreference {
		private const string ConfigKey = "settings.xr.loader.preferred";

		/// <summary>
		/// Identifiant du loader à essayer en premier, ou <c>null</c> s'il n'y a pas de préférence
		/// (chaîne par défaut, triée par priorité).
		/// </summary>
		public static string Preferred {
			get => Config.Load().Get<string>(ConfigKey);
			set {
				var config = Config.Load();
				// `null` retire la clé : un config.json sans préférence reste propre.
				var wanted = string.IsNullOrWhiteSpace(value) ? null : value;
				if (config.Get<string>(ConfigKey) == wanted)
					return;

				config.Set(ConfigKey, wanted);
				config.Save();
			}
		}

		/// <summary>Indique si <paramref name="id"/> est le loader choisi.</summary>
		public static bool IsPreferred(string id)
			=> !string.IsNullOrEmpty(id) && string.Equals(id, Preferred, StringComparison.OrdinalIgnoreCase);

		/// <summary>Revient au choix automatique (priorité des providers).</summary>
		public static void Clear()
			=> Preferred = null;
	}
}
