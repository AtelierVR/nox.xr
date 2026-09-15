using UnityEngine;

namespace Nox.CCK.XR {
	/// <summary>
	/// Contrat public du proxy XR, exposé aux mods : recentrage et remise à hauteur de la vue.
	/// <para>
	/// La « hauteur recommandée » est celle de l'avatar courant (module d'échelle). Elle sert
	/// de cible quand la vue démarre sous le sol — cas typique quand la pose de tête n'est pas
	/// suivie (aucun casque, simulateur, OpenXR qui rapporte une origine au sol).
	/// </para>
	/// </summary>
	public interface IXRController {
		/// <summary>
		/// Hauteur actuelle de la vue dans le monde, en mètres.
		/// </summary>
		/// <returns>Position Y (monde) de la caméra de tête.</returns>
		public float GetViewHeight();

		/// <summary>
		/// Hauteur recommandée de la vue, en mètres, déduite de la taille de l'avatar courant.
		/// </summary>
		/// <returns>Hauteur cible, ou une valeur par défaut si aucun avatar n'est chargé.</returns>
		public float GetRecommendedHeight();

		/// <summary>
		/// Replace la vue à la hauteur voulue, sans la déplacer horizontalement.
		/// </summary>
		/// <param name="height">Hauteur cible en mètres. Une valeur &lt;= 0 utilise <see cref="GetRecommendedHeight"/>.</param>
		public void ReHeight(float height = -1f);

		/// <summary>
		/// Recentre la vue à l'aplomb de la position du rig, orientée vers son avant.
		/// La hauteur n'est pas modifiée (voir <see cref="ReHeight"/>).
		/// </summary>
		public void ReCenter();

		/// <summary>
		/// Recentre la vue à l'aplomb de <paramref name="worldPosition"/>, orientée vers
		/// <paramref name="forward"/>. La hauteur n'est pas modifiée.
		/// </summary>
		/// <param name="worldPosition">Position monde au-dessus de laquelle placer la vue.</param>
		/// <param name="forward">Direction (aplatie sur le plan horizontal) que doit regarder la vue.</param>
		public void ReCenter(Vector3 worldPosition, Vector3 forward);

		/// <summary>
		/// Recentre la vue puis la replace à la hauteur recommandée.
		/// Raccourci de <see cref="ReCenter()"/> suivi de <see cref="ReHeight"/>.
		/// </summary>
		/// <param name="height">Hauteur cible en mètres. Une valeur &lt;= 0 utilise <see cref="GetRecommendedHeight"/>.</param>
		public void ReCenterAndReHeight(float height = -1f);
	}
}
