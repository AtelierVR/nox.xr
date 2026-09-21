namespace Nox.XR.Bindings {
	/// <summary>
	/// Bindings d'un runtime XR (OpenXR, OpenVR/SteamVR, ...), lus par clé.
	///
	/// <para>
	/// C'est l'objet que le loader actif rend via <c>IXRLoaderProvider.Binding</c>. Le <b>mod de
	/// loader en est le propriétaire</b> : il enregistre ses actions auprès du système de key
	/// bindings, résout leurs chemins selon les devices réellement connectés, et répond aux
	/// lectures. nox.xr ne fait que déclencher <see cref="Refresh"/> au bon moment et relayer les
	/// valeurs aux consommateurs.
	/// </para>
	/// </summary>
	public interface IBinding {
		/// <summary>
		/// Valeur courante d'un binding logique, par sa clé (ex. <c>"move"</c>, <c>"jump"</c>,
		/// <c>"finger.left.index"</c>).
		/// </summary>
		/// <typeparam name="T">
		/// Type attendu : <c>float</c> pour un contrôle analogique, <see cref="UnityEngine.Vector2"/>
		/// pour un axe (stick/trackpad).
		/// </typeparam>
		/// <returns>
		/// La valeur courante, ou <c>default</c> si ce runtime ne fournit pas ce binding, ou si
		/// <typeparamref name="T"/> ne correspond pas à son type — jamais d'exception.
		/// </returns>
		T Get<T>(string key) where T : struct;

		/// <summary>
		/// (Re)lie les bindings du runtime : le mod résout les chemins contre les devices
		/// actuellement connectés et les enregistre auprès du système de key bindings.
		///
		/// <para>
		/// Appelé par nox.xr à la création du proxy XR et à chaque changement de device : le
		/// matériel peut changer en cours de session (une manette se connecte après le casque,
		/// l'utilisateur change de modèle...).
		/// </para>
		/// </summary>
		void Refresh();

		/// <summary>
		/// Délie les bindings du runtime (arrêt du proxy XR, arrêt du mod).
		/// </summary>
		void Clear();
	}
}
